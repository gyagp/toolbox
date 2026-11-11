using System.ComponentModel;
using System.Text.Json;

namespace Toolbox.Platform;

public sealed record CodexInstallationsReport(int KeepNewest, IReadOnlyList<CodexReleaseReport> Installations)
{
    public IReadOnlyList<CodexRelease> Releases => Installations.SelectMany(report => report.Releases).ToArray();
    public IReadOnlyList<string> Warnings => Installations.SelectMany(report =>
        report.Warnings.Select(warning => $"{Path.GetFileName(report.Root)}：{warning}")).ToArray();
    public long CandidateBytes => Installations.Sum(report => report.CandidateBytes);
    public int CandidateCount => Installations.Sum(report => report.CandidateCount);
}

/// <summary>Combines the two known installations, retaining and validating each independently.</summary>
public sealed class CodexInstallationsCleaner
{
    private readonly IReadOnlyDictionary<string, CodexReleaseCleaner> _cleaners;
    public CodexInstallationsCleaner() : this([
        new CodexReleaseCleaner(), new CodexReleaseCleaner(CodexReleaseCleaner.AppServerDaemonRoot)]) { }
    internal CodexInstallationsCleaner(IReadOnlyList<CodexReleaseCleaner> cleaners)
    { _cleaners = cleaners.ToDictionary(cleaner => cleaner.Root, StringComparer.OrdinalIgnoreCase); }

    public CodexInstallationsReport Scan(int keepNewest = 2, CancellationToken cancellation = default)
    {
        if (keepNewest is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(keepNewest));
        var reports = new List<CodexReleaseReport>();
        foreach (var (root, cleaner) in _cleaners)
        {
            cancellation.ThrowIfCancellationRequested();
            try { reports.Add(cleaner.Scan(keepNewest, cancellation)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or Win32Exception)
            {
                // An invalid installation contributes no candidates; the other root remains independently usable.
                reports.Add(new(root, keepNewest, [], [$"无法安全核对，已跳过此安装：{error.Message}"]));
            }
        }
        return new(keepNewest, reports);
    }

    public async Task<CodexCleanupResult> DeleteAsync(CodexInstallationsReport reviewed, CancellationToken cancellation = default,
        IProgress<CodexCleanupProgress>? progress = null)
    {
        if (reviewed.KeepNewest is not (1 or 2) || reviewed.Installations.Select(report => report.Root).Distinct(StringComparer.OrdinalIgnoreCase).Count() != reviewed.Installations.Count)
            throw new InvalidOperationException("预览保留策略或安装目录重复，请重新扫描。");
        // Validate every installation before the first mutation. A preview cannot introduce another root.
        foreach (var report in reviewed.Installations)
        {
            if (report.KeepNewest != reviewed.KeepNewest || !_cleaners.TryGetValue(report.Root, out var cleaner))
                throw new InvalidOperationException("预览安装目录或保留策略不匹配，请重新扫描。");
            cleaner.ValidateReview(report);
        }
        var deleted = new List<string>(); var skipped = new List<string>(); var completed = 0;
        foreach (var report in reviewed.Installations.Where(report => report.CandidateCount > 0))
        {
            if (cancellation.IsCancellationRequested) return new(deleted, skipped, true);
            var source = Path.GetFileName(report.Root);
            try
            {
                var offset = completed;
                var result = await _cleaners[report.Root].DeleteAsync(report, cancellation,
                    new ForwardProgress(update => progress?.Report(new(offset + update.Completed, reviewed.CandidateCount, $"{source} · {update.Name}")))).ConfigureAwait(false);
                deleted.AddRange(result.Deleted.Select(name => $"{source} / {name}"));
                skipped.AddRange(result.Skipped.Select(message => $"{source} / {message}"));
                if (result.Cancelled) return new(deleted, skipped, true);
            }
            catch (OperationCanceledException) { return new(deleted, skipped, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
            { skipped.AddRange(report.Releases.Where(release => release.CanDelete).Select(release => $"{source} / {release.Name}：{error.Message}")); }
            completed += report.CandidateCount;
        }
        progress?.Report(new(completed, reviewed.CandidateCount, "处理完成"));
        return new(deleted, skipped, cancellation.IsCancellationRequested);
    }

    private sealed class ForwardProgress(Action<CodexCleanupProgress> action) : IProgress<CodexCleanupProgress>
    { public void Report(CodexCleanupProgress value) => action(value); }
}

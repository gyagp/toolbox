using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Toolbox.Platform;

public sealed record CodexRelease(string Name, string Path, string Version, string Target, long Bytes,
    string Fingerprint, string? KeepReason)
{
    public bool CanDelete => KeepReason is null;
}
public sealed record CodexReleaseReport(string Root, int KeepNewest, IReadOnlyList<CodexRelease> Releases, IReadOnlyList<string> Warnings)
{
    public long CandidateBytes => Releases.Where(item => item.CanDelete).Sum(item => item.Bytes);
    public int CandidateCount => Releases.Count(item => item.CanDelete);
}
public sealed record CodexCleanupProgress(int Completed, int Total, string Name);
public sealed record CodexCleanupResult(IReadOnlyList<string> Deleted, IReadOnlyList<string> Skipped, bool Cancelled);

/// <summary>Explicit, narrowly scoped cleanup of recognized stable standalone packages.
/// No configuration/session paths, process termination, elevation, or background cleanup.</summary>
public sealed class CodexReleaseCleaner
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "packages", "standalone");
    private readonly string _root;
    private readonly Func<RunningPackages> _running;
    private static readonly Regex ReleaseName = new(@"^(\d+\.\d+\.\d+)-(x86_64-pc-windows-msvc|aarch64-pc-windows-msvc)$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Executables = new(StringComparer.OrdinalIgnoreCase)
    { "codex", "codex-exec-server", "codex-code-mode-host", "codex-voice-host", "codex-command-runner", "codex-windows-sandbox-setup", "rg" };
    private const FileAttributes Unsafe = FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;
    private const uint ReadAttributes = 0x80, DeleteAccess = 0x10000, OpenReparseDirectory = 0x02200000;

    public CodexReleaseCleaner() : this(DefaultRoot, ReadRunningPackages) { }
    internal CodexReleaseCleaner(string root, Func<RunningPackages> running)
    { _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); _running = running; }
    internal sealed record RunningPackages(IReadOnlyList<string> Paths, string? Error = null);
    private sealed record Entry(string Relative, bool Directory, long Length, long Created, long Written);
    private sealed record Package(CodexRelease Release, IReadOnlyList<Entry> Entries);

    public CodexReleaseReport Scan(int keepNewest = 2, CancellationToken cancellation = default)
    {
        if (keepNewest is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(keepNewest), "只能保留最近 1 或 2 个版本。");
        cancellation.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        var releases = new List<CodexRelease>();
        if (!Directory.Exists(_root)) return new(_root, keepNewest, [], ["未发现 Codex standalone 安装。"]);
        SafeAncestors(_root);
        var releaseRoot = Path.Combine(_root, "releases");
        SafeAncestors(releaseRoot);
        var protectedNames = ReadReferences(); // Malformed/missing references fail closed.
        var running = _running();
        if (running.Error is not null) warnings.Add(running.Error);
        var directories = Directory.EnumerateFileSystemEntries(releaseRoot).Take(501).ToArray();
        if (directories.Length > 500) throw new IOException("版本条目过多；停止自动整理。");
        foreach (var path in directories)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var package = ReadPackage(path, cancellation);
                var reason = protectedNames.GetValueOrDefault(package.Release.Name);
                if (running.Paths.Any(executable => Inside(executable, path))) reason ??= "正在运行或被辅助进程使用";
                if (running.Error is not null) reason ??= "运行进程检查不完整，禁止删除";
                releases.Add(package.Release with { KeepReason = reason });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                releases.Add(new(Path.GetFileName(path), path, "", "", 0, "", "未识别或无法完整核对"));
                warnings.Add($"{Path.GetFileName(path)}：{error.Message}");
            }
        }
        // Retention is semantic-version order within each architecture, not timestamps or lexical names.
        foreach (var group in releases.Where(item => item.Fingerprint.Length > 0).GroupBy(item => item.Target))
            foreach (var newest in group.OrderByDescending(item => System.Version.Parse(item.Version)).Take(keepNewest))
            {
                var index = releases.IndexOf(newest);
                releases[index] = newest with { KeepReason = newest.KeepReason ?? $"保留最近 {keepNewest} 个版本" };
            }
        return new(_root, keepNewest, releases.OrderByDescending(item => System.Version.TryParse(item.Version, out var version) ? version : new(0, 0)).ToArray(), warnings);
    }

    public Task<CodexCleanupResult> DeleteAsync(CodexReleaseReport reviewed, CancellationToken cancellation = default,
        IProgress<CodexCleanupProgress>? progress = null) => Task.Run(() => Delete(reviewed, cancellation, progress));

    private CodexCleanupResult Delete(CodexReleaseReport reviewed, CancellationToken cancellation, IProgress<CodexCleanupProgress>? progress)
    {
        if (!Path.GetFullPath(reviewed.Root).Equals(_root, StringComparison.OrdinalIgnoreCase) || reviewed.KeepNewest is not (1 or 2))
            throw new InvalidOperationException("预览目录或保留策略不匹配，请重新扫描。");
        var deleted = new List<string>(); var skipped = new List<string>();
        var selected = reviewed.Releases.Where(item => item.CanDelete).DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var item in selected)
            if (!Path.GetDirectoryName(Path.GetFullPath(item.Path))!.Equals(Path.Combine(_root, "releases"), StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(item.Path) != item.Name || !ReleaseName.IsMatch(item.Name))
                throw new InvalidOperationException("删除范围不在已核对的版本目录内。");
        if (cancellation.IsCancellationRequested) return new(deleted, skipped, true);
        var roots = new List<SafeFileHandle>();
        try
        {
            // Pin ancestors before following child paths; the live current junction is pinned without following it.
            foreach (var ancestor in Ancestors(Path.Combine(_root, "releases"))) roots.Add(Pin(ancestor, false));
            using var installerLock = new FileStream(Path.Combine(_root, "install.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var current = Pin(Path.Combine(_root, "current"), false, allowLink: true, share: FileShare.Read);
            using var marker = new FileStream(Path.Combine(_root, "auto-update-version"), FileMode.Open, FileAccess.Read, FileShare.Read);
            for (var index = 0; index < selected.Length; index++)
            {
                if (cancellation.IsCancellationRequested) return new(deleted, skipped, true);
                var item = selected[index];
                progress?.Report(new(index, selected.Length, item.Name));
                try
                {
                    // Re-read retention, current references and running executables for every package.
                    var fresh = Scan(reviewed.KeepNewest, cancellation).Releases.SingleOrDefault(candidate => candidate.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
                    if (fresh is null || !fresh.CanDelete || fresh.Fingerprint != item.Fingerprint)
                        throw new IOException(fresh?.KeepReason ?? "版本内容或目录已变化，请重新扫描。");
                    DeletePackage(item, cancellation);
                    deleted.Add(item.Name);
                }
                catch (OperationCanceledException)
                {
                    skipped.Add($"{item.Name}：已取消，请核对剩余内容；已完成的删除不会回滚。");
                    return new(deleted, skipped, true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
                { skipped.Add($"{item.Name}：{error.Message}"); }
            }
            progress?.Report(new(selected.Length, selected.Length, "处理完成"));
            return new(deleted, skipped, cancellation.IsCancellationRequested);
        }
        finally { foreach (var handle in roots.AsEnumerable().Reverse()) handle.Dispose(); }
    }

    private void DeletePackage(CodexRelease reviewed, CancellationToken cancellation)
    {
        var handles = new List<(Entry Entry, SafeFileHandle Handle)>();
        var armed = new List<SafeFileHandle>();
        var committed = false;
        try
        {
            var before = ReadPackage(reviewed.Path, cancellation);
            if (before.Release.Fingerprint != reviewed.Fingerprint) throw new IOException("版本在预览后发生变化。");
            foreach (var entry in before.Entries.OrderBy(entry => entry.Relative.Length))
            {
                cancellation.ThrowIfCancellationRequested();
                handles.Add((entry, Pin(Path.Combine(reviewed.Path, entry.Relative), true, share: FileShare.Read)));
            }
            // Every existing node is now pinned against replacement and modification. New unlisted files are never deleted.
            if (ReadPackage(reviewed.Path, cancellation).Release.Fingerprint != reviewed.Fingerprint)
                throw new IOException("版本内容在打开句柄时发生变化。");
            var running = _running();
            if (running.Error is not null || running.Paths.Any(path => Inside(path, reviewed.Path)))
                throw new IOException("版本正在使用或无法完整核对运行状态。");
            if (ReadReferences().ContainsKey(reviewed.Name)) throw new IOException("启动器或自动更新现在指向此版本，已保留。");
            // Arm all files first, without closing any of them. A mapped image or refusal
            // can then clear the pending deletions before a single file is actually removed.
            foreach (var (_, handle) in handles.Where(entry => !entry.Entry.Directory))
            {
                cancellation.ThrowIfCancellationRequested();
                Mark(handle, true); armed.Add(handle);
            }
            cancellation.ThrowIfCancellationRequested(); committed = true;
            foreach (var entry in handles.Where(entry => !entry.Entry.Directory)) entry.Handle.Dispose();
            foreach (var entry in handles.Where(entry => entry.Entry.Directory).OrderByDescending(entry => entry.Entry.Relative.Length))
            { cancellation.ThrowIfCancellationRequested(); Mark(entry.Handle, true); entry.Handle.Dispose(); }
        }
        finally
        {
            if (!committed)
                foreach (var handle in armed)
                {
                    var undo = new Disposition { Delete = false };
                    SetFileInformationByHandle(handle, 4, ref undo, Marshal.SizeOf<Disposition>());
                }
            foreach (var entry in handles) entry.Handle.Dispose();
        }
    }

    private static void Mark(SafeFileHandle handle, bool delete)
    {
        // Classic (non-POSIX) disposition refuses mapped executable images; no path-based recursive delete.
        var disposition = new Disposition { Delete = delete };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, Marshal.SizeOf<Disposition>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "文件正被使用、只读或无法删除；已停止此版本。已完成的删除不会回滚。");
    }

    private Package ReadPackage(string path, CancellationToken cancellation)
    {
        var name = Path.GetFileName(path); var match = ReleaseName.Match(name);
        if (!match.Success || !System.Version.TryParse(match.Groups[1].Value, out _)) throw new IOException("仅识别稳定版 Windows Codex 包。");
        SafeAncestors(path);
        var manifest = SmallFile(Path.Combine(path, "codex-package.json"));
        using var document = JsonDocument.Parse(manifest);
        var json = document.RootElement;
        string? Value(string key) => json.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (!json.TryGetProperty("layoutVersion", out var layout) || !layout.TryGetInt32(out var layoutNumber) || layoutNumber != 1 ||
            Value("version") != match.Groups[1].Value || Value("target") != match.Groups[2].Value || Value("variant") != "codex" ||
            Value("entrypoint") != "bin/codex.exe" || Value("pathDir") != "codex-path" || Value("resourcesDir") != "codex-resources" ||
            (json.TryGetProperty("execServer", out var server) && (server.ValueKind != JsonValueKind.String || server.GetString() != "bin/codex-exec-server.exe")))
            throw new IOException("包清单与受支持的安装布局不匹配。");
        if (!File.Exists(Path.Combine(path, "bin", "codex.exe")) || !Directory.Exists(Path.Combine(path, "codex-path")) || !Directory.Exists(Path.Combine(path, "codex-resources")))
            throw new IOException("安装不完整，不能作为可删除包。");
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
            if (Path.GetFileName(child) is not ("bin" or "codex-path" or "codex-resources" or "codex-package.json"))
                throw new IOException("包内有额外内容，需人工检查。");
        var entries = new List<Entry>();
        var pending = new Stack<string>(); pending.Push(path);
        long bytes = 0;
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            var info = new DirectoryInfo(current); SafeAttributes(info.Attributes);
            entries.Add(new(Path.GetRelativePath(path, current), true, 0, info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks));
            foreach (var child in info.EnumerateFileSystemInfos())
            {
                cancellation.ThrowIfCancellationRequested(); SafeAttributes(child.Attributes);
                if (entries.Count + pending.Count >= 2000 || Path.GetRelativePath(path, child.FullName).Count(character => character == Path.DirectorySeparatorChar) > 16)
                    throw new IOException("包内容超过核对上限。");
                if ((child.Attributes & FileAttributes.Directory) != 0) pending.Push(child.FullName);
                else
                {
                    if (child.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !Executables.Contains(Path.GetFileNameWithoutExtension(child.Name)))
                        throw new IOException("包内存在未知可执行文件。");
                    var file = (FileInfo)child; bytes = checked(bytes + file.Length);
                    entries.Add(new(Path.GetRelativePath(path, file.FullName), false, file.Length, file.CreationTimeUtc.Ticks, file.LastWriteTimeUtc.Ticks));
                }
            }
        }
        var identity = manifest + "\n" + string.Join("\n", entries.OrderBy(entry => entry.Relative, StringComparer.Ordinal).Select(entry => $"{entry.Relative}|{entry.Directory}|{entry.Length}|{entry.Created}|{entry.Written}"));
        return new(new(name, path, match.Groups[1].Value, match.Groups[2].Value, bytes, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))), null), entries);
    }

    private Dictionary<string, string> ReadReferences()
    {
        var current = new DirectoryInfo(Path.Combine(_root, "current"));
        if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) == 0) throw new IOException("缺少可核对的 current 目录链接，停止整理。");
        var target = current.ResolveLinkTarget(false)?.FullName ?? throw new IOException("无法解析 current 指向。");
        var releaseRoot = Path.Combine(_root, "releases");
        if (!Path.GetDirectoryName(target)!.Equals(releaseRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("current 不指向本安装的直接版本目录。");
        SafeAncestors(target);
        var marker = SmallFile(Path.Combine(_root, "auto-update-version")).Trim();
        if (!ReleaseName.IsMatch(marker) || !Directory.Exists(Path.Combine(releaseRoot, marker))) throw new IOException("自动更新版本指向无法核对，停止整理。");
        SafeAncestors(Path.Combine(releaseRoot, marker));
        if ((File.GetAttributes(Path.Combine(_root, "install.lock")) & (Unsafe | FileAttributes.Directory)) != 0) throw new IOException("更新锁文件不符合预期。");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Path.GetFileName(target)] = "current 启动器正在指向" };
        result.TryAdd(marker, "自动更新版本指向");
        return result;
    }

    private static string SmallFile(string path)
    {
        SafeAncestors(Path.GetDirectoryName(path)!); SafeAttributes(File.GetAttributes(path));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > 16_384) throw new IOException("安装元数据超出核对上限。");
        using var reader = new StreamReader(stream, Encoding.UTF8, true); return reader.ReadToEnd();
    }
    private static bool Inside(string path, string folder) => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static IEnumerable<string> Ancestors(string path)
    {
        var pending = new Stack<string>();
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent) pending.Push(directory.FullName);
        return pending;
    }
    private static void SafeAncestors(string path) { foreach (var ancestor in Ancestors(path)) SafeAttributes(File.GetAttributes(ancestor)); }
    private static void SafeAttributes(FileAttributes attributes)
    { if ((attributes & Unsafe) != 0) throw new IOException("发现链接、脱机或云占位内容，已保留。"); }
    private static SafeFileHandle Pin(string path, bool deleting, bool allowLink = false, FileShare share = FileShare.Read | FileShare.Write)
    {
        // READ_ATTRIBUTES alone does not participate in Windows share-access checks.
        // READ_DATA / LIST_DIRECTORY is required to actually prevent replacement.
        var handle = CreateFile(path, ReadAttributes | 1 | (deleting ? DeleteAccess : 0), share, 0, 3, OpenReparseDirectory, 0);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!allowLink) SafeAttributes((FileAttributes)info.Attributes);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static RunningPackages ReadRunningPackages()
    {
        var paths = new List<string>(); string? error = null;
        foreach (var process in Process.GetProcesses())
        using (process)
        {
            try
            {
                var relevant = Executables.Contains(process.ProcessName);
                using var handle = OpenProcess(0x1000, false, process.Id);
                var path = new StringBuilder(32768); var length = path.Capacity;
                if (!handle.IsInvalid && QueryFullProcessImageName(handle, 0, path, ref length)) paths.Add(path.ToString());
                else if (relevant && !process.HasExited) error = "有 Codex/辅助进程的路径无法读取，禁止删除，未请求提权。";
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { error = "进程检查不完整，禁止删除。"; }
        }
        return new(paths, error);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct FileInfoNative
    { public uint Attributes; public long Created, Accessed, Written; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [StructLayout(LayoutKind.Sequential)] private struct Disposition { [MarshalAs(UnmanagedType.Bool)] public bool Delete; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfoNative info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref Disposition info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
}

namespace Toolbox.Core;

public enum CaptureKind { Image, Video }
public enum LibraryFilter { All, Images, Videos, Deleted }
public enum LibrarySort { Newest, Oldest, Name, Largest }
public sealed record CaptureEntry(string FullPath, string Name, CaptureKind Kind, long Size,
    DateTime CreatedUtc, DateTime ModifiedUtc, DateTime? DeletedUtc)
{
    public bool IsDeleted => DeletedUtc.HasValue;
    public string Stem => Path.GetFileNameWithoutExtension(Name);
}
public sealed record LibrarySnapshot(IReadOnlyList<CaptureEntry> Entries, IReadOnlyList<string> Errors);

/// <summary>A disk-backed catalog. The files are the source of truth; no database
/// migration is needed to discover captures from older versions.</summary>
public sealed class CaptureLibrary
{
    private const string TrashName = ".shike-trash";
    private readonly string[] _roots;
    public IReadOnlyList<string> Roots => _roots;

    public CaptureLibrary(params string[] roots)
    {
        if (roots.Length == 0) throw new ArgumentException("需要至少一个保存目录。", nameof(roots));
        _roots = roots.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public LibrarySnapshot Scan()
    {
        var entries = new List<CaptureEntry>();
        var errors = new List<string>();
        foreach (var root in _roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                ScanFiles(root, null, entries);
                var trash = Path.Combine(root, TrashName);
                if (!Directory.Exists(trash) || IsLink(trash)) continue;
                foreach (var folder in Directory.EnumerateDirectories(trash))
                {
                    if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out _) || IsLink(folder)) continue;
                    ScanFiles(folder, Directory.GetCreationTimeUtc(folder), entries);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { errors.Add($"{root}：{error.Message}"); }
        }
        return new(entries, errors);
    }

    private static void ScanFiles(string directory, DateTime? deletedUtc, List<CaptureEntry> entries)
    {
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            if (!TryGetKind(path, out var kind)) continue;
            try
            {
                var info = new FileInfo(path);
                if (info.LinkTarget is not null) continue;
                entries.Add(new(info.FullName, info.Name, kind, info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc, deletedUtc));
            }
            catch (FileNotFoundException) { } // An external rename/delete raced the scan.
            catch (DirectoryNotFoundException) { }
        }
    }

    public static IReadOnlyList<CaptureEntry> Query(IEnumerable<CaptureEntry> entries, LibraryFilter filter, string search, LibrarySort sort)
    {
        var query = entries.Where(e => filter == LibraryFilter.Deleted ? e.IsDeleted : !e.IsDeleted);
        if (filter == LibraryFilter.Images) query = query.Where(e => e.Kind == CaptureKind.Image);
        if (filter == LibraryFilter.Videos) query = query.Where(e => e.Kind == CaptureKind.Video);
        var term = search.Trim();
        if (term.Length > 0) query = query.Where(e => e.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
        var ordered = sort switch
        {
            LibrarySort.Oldest => query.OrderBy(e => e.DeletedUtc ?? e.CreatedUtc),
            LibrarySort.Name => query.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            LibrarySort.Largest => query.OrderByDescending(e => e.Size),
            _ => query.OrderByDescending(e => e.DeletedUtc ?? e.CreatedUtc)
        };
        return ordered.ThenBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public string Rename(string path, string stem)
    {
        var (source, root, deleted) = Resolve(path);
        if (deleted) throw new InvalidOperationException("请先恢复此文件，再重命名。");
        ValidateStem(stem);
        var destination = Path.Combine(root, stem + Path.GetExtension(source));
        if (!TryGetKind(destination, out _)) throw new ArgumentException("此名称保留给未完成的录屏，请换一个名称。");
        if (string.Equals(source, destination, StringComparison.Ordinal)) return source;
        File.Move(source, destination, overwrite: false);
        return destination;
    }

    public string MoveToDeleted(string path)
    {
        var (source, root, deleted) = Resolve(path);
        if (deleted) throw new InvalidOperationException("此文件已在最近删除中。");
        var trash = Path.Combine(root, TrashName);
        if (Directory.Exists(trash) && IsLink(trash)) throw new IOException("最近删除目录不能是链接。");
        Directory.CreateDirectory(trash);
        var folder = Path.Combine(trash, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, Path.GetFileName(source));
        try { File.Move(source, destination, overwrite: false); }
        catch { TryRemoveEmpty(folder); throw; }
        return destination;
    }

    public string Restore(string path)
    {
        var (source, root, deleted) = Resolve(path);
        if (!deleted) throw new InvalidOperationException("此文件不在最近删除中。");
        var destination = Path.Combine(root, Path.GetFileName(source));
        if (File.Exists(destination)) throw new IOException("保存目录已有同名文件，请先重命名现有文件，再恢复。");
        File.Move(source, destination, overwrite: false);
        TryRemoveEmpty(Path.GetDirectoryName(source)!);
        return destination;
    }

    public void PermanentlyDelete(string path)
    {
        var (source, _, deleted) = Resolve(path);
        if (!deleted) throw new InvalidOperationException("请先将文件移至最近删除。");
        File.Delete(source);
        TryRemoveEmpty(Path.GetDirectoryName(source)!);
    }

    public string ValidatePath(string path) => Resolve(path).Path;

    private (string Path, string Root, bool Deleted) Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        if (!TryGetKind(full, out _) || !File.Exists(full)) throw new FileNotFoundException("文件已被移动或删除，请刷新内容库。", full);
        if (IsLink(full)) throw new IOException("内容库不操作链接文件。");
        var parent = Path.GetDirectoryName(full)!;
        foreach (var root in _roots)
        {
            if (string.Equals(parent, root, StringComparison.OrdinalIgnoreCase)) return (full, root, false);
            var trash = Path.Combine(root, TrashName);
            if (string.Equals(Path.GetDirectoryName(parent), trash, StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(Path.GetFileName(parent), "N", out _) && !IsLink(trash) && !IsLink(parent))
                return (full, root, true);
        }
        throw new InvalidOperationException("此文件不在工具箱管理的保存目录内。");
    }

    private static bool TryGetKind(string path, out CaptureKind kind)
    {
        kind = CaptureKind.Image;
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return true;
        kind = CaptureKind.Video;
        return path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".partial.mp4", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateStem(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem) || stem.Length > 180 || stem != stem.Trim() || stem.EndsWith('.') ||
            stem.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)))
            throw new ArgumentException("请输入有效名称（1–180 个字符），不能包含路径、特殊符号或末尾的空格、句点。");
        var first = stem.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
        if (first is "CON" or "PRN" or "AUX" or "NUL" ||
            (first.Length == 4 && (first.StartsWith("COM") || first.StartsWith("LPT")) && first[3] is >= '1' and <= '9'))
            throw new ArgumentException("此名称由 Windows 保留，请换一个名称。");
    }

    private static bool IsLink(string path) => Directory.Exists(path) ? new DirectoryInfo(path).LinkTarget is not null : new FileInfo(path).LinkTarget is not null;
    private static void TryRemoveEmpty(string directory)
    {
        try { Directory.Delete(directory, recursive: false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Toolbox.Platform;

public sealed record CleanupDrive(string Root, long TotalBytes, long AvailableBytes);
public sealed record CleanupLocation(string Title, string Path, string Guidance, bool Recursive = true, string Pattern = "*");
public sealed record CleanupEstimate(long Bytes, long Files, int Skipped, bool Complete, bool HasData, string Note);
public sealed record CleanupItem(CleanupLocation Location, CleanupEstimate Estimate);
public sealed record CleanupReport(string DriveRoot, DateTimeOffset ScannedAt, IReadOnlyList<CleanupItem> Items);

/// <summary>
/// Metadata-only inspection. Estimates are NOT deletion plans or promised reclaimable space.
/// This component has no delete, move, process termination, privilege or registry operations.
/// </summary>
public static class DiskCleanupInspector
{
    // Offline and recall attributes must be skipped without opening or hydrating file content.
    private const FileAttributes UnsafeAttributes = FileAttributes.ReparsePoint | FileAttributes.Offline |
        (FileAttributes)0x00040000 | (FileAttributes)0x00400000;

    public static IReadOnlyList<CleanupDrive> GetDrives()
    {
        var result = new List<CleanupDrive>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                    result.Add(new(drive.Name, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (Exception error) when (IsReadFailure(error)) { }
        }
        return result;
    }

    public static CleanupReport Scan(string driveRoot, CancellationToken cancellation = default, IProgress<string>? progress = null)
    {
        driveRoot = WindowsCleanupLauncher.ValidateDriveRoot(driveRoot);
        cancellation.ThrowIfCancellationRequested();
        if (!GetDrives().Any(drive => drive.Root.Equals(driveRoot, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("请选择可用的本地固定磁盘。");

        var items = new List<CleanupItem>();
        foreach (var location in KnownLocations().Where(location =>
            string.Equals(System.IO.Path.GetPathRoot(location.Path), driveRoot, StringComparison.OrdinalIgnoreCase)))
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report("正在只读检查：" + location.Title);
            items.Add(new(location, Measure(location, cancellation)));
        }
        cancellation.ThrowIfCancellationRequested();
        progress?.Report("正在读取回收站大小…");
        var recycle = new RecycleBinInfo { Size = (uint)Marshal.SizeOf<RecycleBinInfo>() };
        var status = SHQueryRecycleBin(driveRoot, ref recycle);
        cancellation.ThrowIfCancellationRequested();
        items.Add(new(new("回收站 · 仅查看", driveRoot + "$Recycle.Bin",
            "保留以便恢复误删文件。Toolbox 不清空回收站；请先逐项核对。"),
            status >= 0 && recycle.Bytes >= 0 && recycle.Items >= 0
                ? new(recycle.Bytes, recycle.Items, 0, true, true, "Windows 回收站报告值；不计作建议清理量。")
                : new(0, 0, 0, false, false, $"回收站大小不可用（0x{status:X8}）；不代表为空。")));
        return new(driveRoot, DateTimeOffset.Now, items.AsReadOnly());
    }

    internal static IReadOnlyList<CleanupLocation> KnownLocations()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        // Do not trust TEMP/TMP overrides and never crawl an entire user profile, disk or AppData.
        var locations = new List<CleanupLocation>();
        if (!string.IsNullOrWhiteSpace(local))
        {
            locations.Add(new("当前用户的临时文件", System.IO.Path.Combine(local, "Temp"),
                "可能包含安装中间文件、未保存工作或恢复数据。仅供查看，不能因文件较旧就判定可删。"));
            locations.Add(new("DirectX 着色器缓存", System.IO.Path.Combine(local, "D3DSCache"),
                "由 Windows 判断可清理部分。清理后游戏或图形应用可能需要重新编译缓存。"));
            locations.Add(new("资源管理器缩略图缓存", System.IO.Path.Combine(local, "Microsoft", "Windows", "Explorer"),
                "仅统计 thumbcache_*.db。由 Windows 清理后，缩略图会重新生成。", false, "thumbcache_*.db"));
            locations.Add(new("Windows 错误报告", System.IO.Path.Combine(local, "Microsoft", "Windows", "WER"),
                "排查崩溃时可能需要这些报告；正在诊断问题时请保留。"));
        }
        if (!string.IsNullOrWhiteSpace(windows))
        {
            locations.Add(new("Windows 临时文件", System.IO.Path.Combine(windows, "Temp"),
                "可能有系统服务或安装任务正在使用。Toolbox 不提权、不处理占用，交给 Windows 审查。"));
            locations.Add(new("Windows 更新下载", System.IO.Path.Combine(windows, "SoftwareDistribution", "Download"),
                "仅作占用参考。更新仍可能需要这些文件，不手动删除更新目录或停止服务。"));
        }
        return locations;
    }

    internal static CleanupEstimate Measure(CleanupLocation location, CancellationToken cancellation = default,
        int maxEntries = 50_000, TimeSpan? timeLimit = null)
    {
        if (maxEntries < 1) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        var clock = Stopwatch.StartNew();
        var limit = timeLimit ?? TimeSpan.FromSeconds(8);
        if (limit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeLimit));
        long bytes = 0, files = 0;
        var skipped = 0;
        var entries = 0;
        var pending = new Stack<(string Path, int Depth)>();
        cancellation.ThrowIfCancellationRequested();
        try
        {
            if (!SafeAncestors(location.Path)) return new(0, 0, 1, false, false, "位置或上级目录是链接、脱机或云占位内容，已跳过。");
            if ((File.GetAttributes(location.Path) & FileAttributes.Directory) == 0)
                return new(0, 0, 1, false, false, "预期位置不是目录，已跳过。");
        }
        catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException)
        { return new(0, 0, 0, true, true, "未发现此位置。"); }
        catch (Exception error) when (IsReadFailure(error))
        { return new(0, 0, 1, false, false, "无法读取此位置；不代表为空。" ); }
        pending.Push((location.Path, 0));
        while (pending.TryPop(out var directory))
        {
            cancellation.ThrowIfCancellationRequested();
            if (clock.Elapsed >= limit) return Partial("达到时间上限");
            try
            {
                if (!SafeAncestors(directory.Path)) { skipped++; continue; }
                // Enumerate lazily; never open file contents. Do not suppress access failures.
                var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0,
                    IgnoreInaccessible = false, ReturnSpecialDirectories = false, MatchType = MatchType.Simple,
                    MatchCasing = MatchCasing.CaseInsensitive };
                foreach (var entry in new DirectoryInfo(directory.Path).EnumerateFileSystemInfos(location.Pattern, options))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (entries >= maxEntries || clock.Elapsed >= limit) return Partial("达到扫描上限");
                    entries++;
                    try
                    {
                        var attributes = entry.Attributes;
                        if ((attributes & UnsafeAttributes) != 0) { skipped++; continue; }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (location.Recursive)
                            {
                                if (directory.Depth >= 64) skipped++;
                                else pending.Push((entry.FullName, directory.Depth + 1));
                            }
                        }
                        else { bytes = checked(bytes + ((FileInfo)entry).Length); files++; }
                    }
                    catch (Exception error) when (IsReadFailure(error)) { skipped++; }
                }
            }
            catch (Exception error) when (IsReadFailure(error)) { skipped++; }
        }
        return new(bytes, files, skipped, skipped == 0, skipped == 0 || files > 0, skipped == 0
            ? "文件大小估算，不是可释放空间。" : "部分内容不可读、发生变化或已跳过；数值不完整。");

        CleanupEstimate Partial(string reason) => new(bytes, files, skipped, false, entries > 0, reason + "；仅显示已读取部分，可再次扫描。");
    }

    private static bool SafeAncestors(string path)
    {
        var ancestors = new Stack<string>();
        for (var current = new DirectoryInfo(System.IO.Path.GetFullPath(path)); current is not null; current = current.Parent)
            ancestors.Push(current.FullName);
        // Check from the volume down, before looking through a linked ancestor.
        while (ancestors.TryPop(out var ancestor))
            if ((File.GetAttributes(ancestor) & UnsafeAttributes) != 0) return false;
        return true;
    }

    private static bool IsReadFailure(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct RecycleBinInfo { public uint Size; public long Bytes; public long Items; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin(string root, ref RecycleBinInfo info);
}

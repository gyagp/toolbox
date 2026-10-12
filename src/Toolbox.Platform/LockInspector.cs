using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Toolbox.Core;

namespace Toolbox.Platform;

public sealed record LockingProcess(int Id, long StartTime, string Name, string ServiceName, string[] Files)
{
    public string Label => $"{Name}  ·  PID {Id}";
    public string Details => (ServiceName.Length > 0 ? $"服务：{ServiceName}\n" : "") +
        (StartTime == 0 ? "无法核对进程身份，不能安全结束；请刷新或手动关闭。\n" : "") +
        string.Join("\n", Files.Select(path => Directory.Exists(path) ? path + "  [目录句柄 / 可能是工作目录]" : path));
}

public sealed record LockReport(IReadOnlyList<LockingProcess> Processes, int FilesScanned, IReadOnlyList<string> Warnings, int DirectoriesScanned = 0);

/// <summary>Restart Manager reports users of registered files, not all kernel handles.
/// Directory handles are queried separately. Never traverses links or shuts applications down.</summary>
public static class LockInspector
{
    public static LockReport Inspect(string source, CancellationToken token = default, IProgress<int>? progress = null)
    {
        source = FileActionPlan.Normalize(source);
        var warnings = new List<string>();
        var files = new List<string>();
        var pending = new Stack<string>();
        var scanned = 0;
        var directoriesScanned = 0;
        var owners = new Dictionary<(int, long), (ProcessInfo Info, List<string> Files)>();
        var directoryOwners = new List<LockingProcess>();

        void ReadBatch()
        {
            token.ThrowIfCancellationRequested();
            if (files.Count == 0) return;
            // Query batches first; inspect individual files only when the batch has owners.
            // A busy file registration must not hide results for the rest of a folder.
            var inspectIndividually = true;
            try { inspectIndividually = Query(files.ToArray(), token).Length > 0; }
            catch (Win32Exception error) { warnings.Add($"批量查询失败，将逐文件重试：{error.Message}"); }
            if (inspectIndividually)
            {
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        foreach (var info in Query([file], token))
                        {
                            var key = (info.Process.Id, info.Process.StartTime);
                            if (!owners.TryGetValue(key, out var owner))
                                owner = (info, []);
                            owner.Files.Add(file);
                            owners[key] = owner;
                        }
                    }
                    catch (Win32Exception error) { warnings.Add($"{file}：{error.Message}"); }
                }
            }
            scanned += files.Count;
            files.Clear();
            progress?.Report(scanned);
        }

        void Visit(string path)
        {
            token.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                warnings.Add($"未跟随链接或重解析点：{path}");
                return;
            }
            if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
            else
            {
                files.Add(path);
                if (files.Count >= 64) ReadBatch();
            }
        }

        Visit(source);
        while (pending.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            try { directoryOwners.AddRange(DirectoryLockQuery.Inspect(folder, token)); }
            catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
            { warnings.Add($"目录占用查询未完成：{folder}：{error.Message}"); }
            directoriesScanned++;
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                {
                    try { Visit(entry); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { warnings.Add($"{entry}：{error.Message}"); }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { warnings.Add($"{folder}：{error.Message}"); }
        }
        ReadBatch();
        var combined = owners.Values.Select(owner => new LockingProcess(owner.Info.Process.Id,
            owner.Info.Process.StartTime, owner.Info.AppName, owner.Info.ServiceName, owner.Files.ToArray()))
            .Concat(directoryOwners).GroupBy(p => (p.Id, p.StartTime))
            .Select(group => group.First() with { Files = group.SelectMany(p => p.Files).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() });
        return new(combined.OrderBy(p => p.Name).ThenBy(p => p.Id).ToArray(), scanned, warnings, directoriesScanned);
    }

    public static async Task TerminateAsync(LockingProcess selected)
    {
        // PID reuse must never cause us to kill a different process than the user selected.
        if (selected.Id <= 4 || selected.Id == Environment.ProcessId)
            throw new InvalidOperationException("不能结束系统进程或 Toolbox 自身。");
        if (selected.StartTime == 0)
            throw new InvalidOperationException("无法核对进程身份，不能安全结束。请重新查询或手动关闭该程序。");
        Process process;
        try { process = Process.GetProcessById(selected.Id); }
        catch (ArgumentException) { return; } // It already exited.
        using (process)
        {
            if (process.HasExited) return;
            if (process.StartTime.ToUniversalTime().ToFileTimeUtc() != selected.StartTime)
                throw new InvalidOperationException($"PID {selected.Id} 已被其他进程复用，请重新查找占用。");
            if (!IsProcessCritical(process.Handle, out var critical))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法检查进程的系统保护状态。");
            if (critical) throw new InvalidOperationException("不能结束 Windows 关键进程。");
            process.Kill(entireProcessTree: false);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static ProcessInfo[] Query(string[] files, CancellationToken token)
    {
        var key = new StringBuilder(33);
        Check(RmStartSession(out var session, 0, key));
        try
        {
            Check(RmRegisterResources(session, (uint)files.Length, files, 0, nint.Zero, 0, nint.Zero));
            uint count = 0;
            var result = RmGetList(session, out var needed, ref count, null, out _);
            for (var attempt = 0; result == 234 && attempt < 8; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var list = new ProcessInfo[needed];
                count = needed;
                result = RmGetList(session, out needed, ref count, list, out _);
                if (result == 0) return list.Take((int)count).ToArray();
            }
            Check(result);
            return [];
        }
        finally { RmEndSession(session); }
    }

    private static void Check(int result) { if (result != 0) throw new Win32Exception(result); }

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess
    {
        public int Id;
        public uint StartLow, StartHigh;
        public readonly long StartTime => (long)(((ulong)StartHigh << 32) | StartLow);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public int ApplicationType;
        public uint AppStatus, SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[] names, uint applications, nint processes, uint services, nint serviceNames);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[]? processes, out uint rebootReasons);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint session);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(nint process, [MarshalAs(UnmanagedType.Bool)] out bool critical);
}

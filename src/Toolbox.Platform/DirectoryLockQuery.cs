using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Toolbox.Platform;

/// <summary>Restart Manager cannot register directories. Query the filesystem for
/// processes holding the directory itself, including a shell's working directory.</summary>
internal static class DirectoryLockQuery
{
    internal static LockingProcess[] Inspect(string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // No data access requested, all sharing enabled, and never follow a junction.
        using var handle = CreateFileW(directory, 0, 7, nint.Zero, 3, 0x02200000, nint.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var snapshotTime = DateTime.UtcNow.ToFileTimeUtc();
        for (var size = 4096; size <= 1024 * 1024; size *= 2)
        {
            token.ThrowIfCancellationRequested();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQueryInformationFile(handle, out var io, buffer, (uint)size, 47);
                if (status is unchecked((int)0x80000005) or unchecked((int)0xC0000004) or unchecked((int)0xC0000023)) continue;
                if (status < 0) throw new Win32Exception((int)RtlNtStatusToDosError(status));
                var count = Marshal.ReadInt32(buffer);
                // ULONG count followed by a pointer-aligned ULONG_PTR array.
                var offset = IntPtr.Size;
                if (count < 0 || count > (size - offset) / IntPtr.Size ||
                    (nuint)(offset + count * IntPtr.Size) > io.Information)
                    throw new IOException("Windows 返回了无效的目录占用信息。");
                var owners = new List<LockingProcess>();
                for (var index = 0; index < count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var pid = Marshal.ReadIntPtr(buffer, offset + index * IntPtr.Size).ToInt64();
                    // Our temporary query handle must not appear as a user-held lock.
                    if (pid <= 0 || pid > int.MaxValue || pid == Environment.ProcessId) continue;
                    try
                    {
                        using var process = Process.GetProcessById((int)pid);
                        var started = process.StartTime.ToUniversalTime().ToFileTimeUtc();
                        if (started > snapshotTime || process.HasExited) continue;
                        owners.Add(new((int)pid, started, process.ProcessName, "", [directory]));
                    }
                    catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
                    {
                        // Keep protected owners visible but not eligible for termination.
                        owners.Add(new((int)pid, 0, "进程（身份无法读取或已退出）", "", [directory]));
                    }
                }
                return owners.DistinctBy(p => p.Id).ToArray();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("目录占用信息过大，请缩小查询范围后重试。");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public nint Status; public nuint Information; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationFile(SafeFileHandle file, out IoStatusBlock status, nint information, uint length, int informationClass);
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
}

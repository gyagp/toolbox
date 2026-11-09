using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Toolbox.Platform;

public sealed record DesktopWindow(nint Handle, uint ProcessId, long ProcessStartTime, string ProcessName,
    string Title, string ClassName, int X, int Y, int Width, int Height, string? BlockReason)
{
    public bool CanClose => BlockReason is null;
    public string Label => $"{ProcessName}  ·  PID {ProcessId}";
    public bool Contains(double x, double y) => x >= X && y >= Y && x < (long)X + Width && y < (long)Y + Height;
}

public enum WindowCloseOutcome { Closed, StillOpen, ObservationCancelled }

/// <summary>Lists visible desktop windows and sends one normal WM_CLOSE to a revalidated selection.
/// Never terminates processes, elevates, confirms save prompts, or closes a process's other windows.</summary>
public static class WindowCloser
{
    private const uint QueryLimitedInformation = 0x1000;

    public static IReadOnlyList<DesktopWindow> Enumerate()
    {
        var result = new List<DesktopWindow>();
        // Keep ineligible surfaces in Z order: they must block selection of windows underneath.
        if (!EnumWindows((handle, data) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle)) return true;
            if (DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (!GetWindowRect(handle, out var bounds) || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return true;
            if (DwmGetWindowAttribute(handle, 9, out Rect frame, Marshal.SizeOf<Rect>()) == 0 && frame.Right > frame.Left && frame.Bottom > frame.Top)
                bounds = frame;
            GetWindowThreadProcessId(handle, out var pid);
            var title = ReadText(handle);
            var className = ReadClass(handle);
            var reason = BasicBlockReason(handle, pid, title, className);
            var name = "未知程序";
            long start = 0;
            using var process = OpenProcess(QueryLimitedInformation, false, pid);
            if (!process.IsInvalid && GetProcessTimes(process, out start, out _, out _, out _))
            {
                var path = new StringBuilder(32768);
                var length = path.Capacity;
                if (QueryFullProcessImageName(process, 0, path, ref length)) name = Path.GetFileName(path.ToString());
                if (!IsProcessCritical(process, out var critical) || critical) reason ??= "系统关键进程或无法核对保护状态。";
            }
            else reason ??= "无法核对进程身份，请使用程序自己的关闭按钮。";
            result.Add(new(handle, pid, start, name, title, className, bounds.Left, bounds.Top,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, reason));
            return true;
        }, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return result;
    }

    public static DesktopWindow? HitTest(IReadOnlyList<DesktopWindow> windows, double x, double y) =>
        windows.FirstOrDefault(window => window.Contains(x, y));

    public static async Task<WindowCloseOutcome> RequestCloseAsync(DesktopWindow selected, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        ValidateSelection(selected);
        cancellation.ThrowIfCancellationRequested();
        if (!PostMessage(selected.Handle, 0x0010, 0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        // Posting succeeds before the application processes its queue. Do not claim it has closed.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!IsSameWindow(selected)) return WindowCloseOutcome.Closed;
            try { await Task.Delay(100, cancellation); }
            catch (OperationCanceledException) { return WindowCloseOutcome.ObservationCancelled; }
        }
        return IsSameWindow(selected) ? WindowCloseOutcome.StillOpen : WindowCloseOutcome.Closed;
    }

    public static void ValidateSelection(DesktopWindow selected)
    {
        if (!selected.CanClose || selected.Handle == 0 || selected.ProcessId <= 4 ||
            selected.ProcessId == Environment.ProcessId || selected.ProcessStartTime <= 0)
            throw new InvalidOperationException(selected.BlockReason ?? "此窗口不能由本工具关闭。");
        if (!IsWindow(selected.Handle) || !IsWindowVisible(selected.Handle) || IsIconic(selected.Handle))
            throw new InvalidOperationException("窗口已关闭、隐藏或最小化，请重新选择。");
        GetWindowThreadProcessId(selected.Handle, out var pid);
        var title = ReadText(selected.Handle);
        var className = ReadClass(selected.Handle);
        if (pid != selected.ProcessId || title != selected.Title || className != selected.ClassName)
            throw new InvalidOperationException("窗口或标题已变化，请重新选择并确认。");
        if (BasicBlockReason(selected.Handle, pid, title, className) is { } reason) throw new InvalidOperationException(reason);
        if (DwmGetWindowAttribute(selected.Handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            throw new InvalidOperationException("窗口已切换到其他桌面，请重新选择。");
        using var process = OpenProcess(QueryLimitedInformation, false, pid);
        if (process.IsInvalid || !GetProcessTimes(process, out var start, out _, out _, out _) || start != selected.ProcessStartTime)
            throw new InvalidOperationException("进程身份已变化或无法核对，请重新选择。");
        if (!IsProcessCritical(process, out var critical) || critical)
            throw new InvalidOperationException("系统关键进程或无法核对保护状态，不能关闭。");
    }

    private static bool IsSameWindow(DesktopWindow selected)
    {
        if (!IsWindow(selected.Handle)) return false;
        GetWindowThreadProcessId(selected.Handle, out var pid);
        if (pid != selected.ProcessId || ReadClass(selected.Handle) != selected.ClassName) return false;
        using var process = OpenProcess(QueryLimitedInformation, false, pid);
        // Access failure is inconclusive, so keep reporting 'still open'.
        return process.IsInvalid || !GetProcessTimes(process, out var start, out _, out _, out _) || start == selected.ProcessStartTime;
    }

    private static string? BasicBlockReason(nint handle, uint pid, string title, string className)
    {
        if (pid <= 4 || handle == GetDesktopWindow() || handle == GetShellWindow() ||
            className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "#32768")
            return "桌面、任务栏和系统菜单不能选择。";
        if (pid == Environment.ProcessId) return "不能选择 Toolbox 自身。";
        if (!IsWindowEnabled(handle)) return "窗口正在等待其他对话框，请选择前面的对话框。";
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(className)) return "窗口没有可确认的标题。";
        return null;
    }

    private static string ReadText(nint handle)
    {
        var text = new StringBuilder(4096);
        GetWindowText(handle, text, text.Capacity);
        return text.ToString();
    }
    private static string ReadClass(nint handle)
    {
        var text = new StringBuilder(256);
        GetClassName(handle, text, text.Capacity);
        return text.ToString();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool WindowEnum(nint handle, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(WindowEnum callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool PostMessage(nint handle, uint message, nuint wParam, nint lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, uint attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, uint attribute, out Rect value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessCritical(SafeProcessHandle process, out bool critical);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
}

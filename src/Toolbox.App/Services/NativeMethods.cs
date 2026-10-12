using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Toolbox.Core;

namespace Toolbox.App.Services;

public sealed record DisplayInfo(string DeviceName, CaptureRect Bounds, bool IsPrimary)
{
    public string Label => $"{(IsPrimary ? "主显示器" : "显示器")} · {Bounds.Width} × {Bounds.Height} ({DeviceName})";
}

internal sealed record CaptureWindow(nint Handle, string Title, CaptureRect Bounds);

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPels, YPels;
        public uint Used, Important;
    }
    private delegate bool MonitorEnum(nint monitor, nint dc, ref Rect bounds, nint data);
    private delegate bool WindowEnum(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowEnum callback, nint data);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public nint ExtraInfo;
    }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int GetFrameBounds(nint window, uint attribute, out Rect value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int GetCloaked(nint window, uint attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnum callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(nint target, int x, int y, int w, int h, nint source, int sourceX, int sourceY, uint rop);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint window, int id);
    internal delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] internal static extern bool SetWindowSubclass(nint window, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] internal static extern bool RemoveWindowSubclass(nint window, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] internal static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    internal static List<DisplayInfo> GetDisplays()
    {
        var result = new List<DisplayInfo>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref Rect bounds, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfo(monitor, ref info))
                result.Add(new(info.Device, new(info.Monitor.Left, info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top), (info.Flags & 1) != 0));
            return true;
        }, 0);
        return result.OrderByDescending(d => d.IsPrimary).ToList();
    }

    // EnumWindows returns top-level windows in front-to-back Z order. Snapshot before
    // creating the overlay so its own HWND can never intercept window hit testing.
    internal static List<CaptureWindow> GetCaptureWindows(params nint[] excludedHandles)
    {
        var result = new List<CaptureWindow>();
        EnumWindows((window, _) =>
        {
            if (excludedHandles.Contains(window) || !IsWindowVisible(window) || IsIconic(window)) return true;
            if ((GetWindowLongPtr(window, -20).ToInt64() & 0x80) != 0) return true; // tool windows
            if (GetCloaked(window, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var title = new StringBuilder(512);
            var className = new StringBuilder(256);
            if (GetWindowText(window, title, title.Capacity) == 0) return true;
            GetClassName(window, className, className.Capacity);
            if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            if (GetFrameBounds(window, 9, out var rect, Marshal.SizeOf<Rect>()) != 0 && !GetWindowRect(window, out rect)) return true;
            if (rect.Right - rect.Left < 8 || rect.Bottom - rect.Top < 8) return true;
            result.Add(new(window, title.ToString(), new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)));
            return true;
        }, 0);
        return result;
    }

    internal static void ScrollDown()
    {
        var input = new Input { Data = new InputUnion { Mouse = new MouseInput { Data = unchecked((uint)-120), Flags = 0x0800 } } };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            throw new InvalidOperationException("无法滚动目标窗口；请确认目标程序没有以更高权限运行。");
    }

    internal static PixelFrame Capture(CaptureRect region)
    {
        var bytes = checked(region.Width * region.Height * 4);
        if (region.Width <= 0 || region.Height <= 0 || bytes > ScrollStitcher.MaxBytes)
            throw new ArgumentOutOfRangeException(nameof(region));
        var source = GetDC(0);
        var target = CreateCompatibleDC(source);
        nint bitmap = 0, previous = 0;
        try
        {
            if (source == 0 || target == 0) throw new Win32Exception("无法读取桌面。");
            var info = new BitmapInfo { Size = 40, Width = region.Width, Height = -region.Height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(source, ref info, 0, out var bits, 0, 0);
            if (bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = SelectObject(target, bitmap);
            if (!BitBlt(target, 0, 0, region.Width, region.Height, source, region.X, region.Y, 0x40CC0020))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var pixels = new byte[bytes];
            Marshal.Copy(bits, pixels, 0, bytes);
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return new(region.Width, region.Height, pixels);
        }
        finally
        {
            if (previous != 0) SelectObject(target, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (target != 0) DeleteDC(target);
            if (source != 0) ReleaseDC(0, source);
        }
    }
}

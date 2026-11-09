using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Toolbox.Platform;

internal static class WindowCloseTests
{
    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        var back = new DesktopWindow(1, 100, 1, "fixture", "back", "fixture", -200, -100, 500, 400, null);
        var front = back with { Handle = 2, Title = "front", X = -100, Width = 200, BlockReason = "blocked" };
        check(WindowCloser.HitTest([front, back], -50, 0) == front, "Ineligible foreground windows block selection of windows underneath");
        check(WindowCloser.HitTest([front, back], -150, 0) == back, "Visual hit testing supports negative monitor coordinates and Z order");
        check(WindowCloser.HitTest([back], 300, 0) is null && WindowCloser.HitTest([back], -200, -100) == back,
            "Hit testing includes left/top edges and excludes right/bottom edges");

        async Task RejectAsync(DesktopWindow target, string name)
        {
            var rejected = false;
            try { await WindowCloser.RequestCloseAsync(target); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected, name);
        }

        foreach (var mode in new[] { "accept", "refuse" })
        {
            var ready = Path.Combine(root, $"window-{mode}.ready");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--window-fixture"); start.ArgumentList.Add(mode); start.ArgumentList.Add(ready);
            using var child = Process.Start(start)!;
            try
            {
                for (var attempt = 0; attempt < 100 && !File.Exists(ready); attempt++) await Task.Delay(50);
                if (!File.Exists(ready)) throw new Exception("Window fixture did not start.");
                var handles = File.ReadAllLines(ready).Select(value => (nint)long.Parse(value)).ToArray();
                var windows = WindowCloser.Enumerate().Where(window => window.ProcessId == child.Id).ToArray();
                var selected = windows.Single(window => window.Handle == handles[0]);
                check(windows.Length == 2 && selected.CanClose && selected.ProcessStartTime == child.StartTime.ToUniversalTime().ToFileTimeUtc(),
                    $"{mode}: enumerate both fixture windows with verified identity");
                await RejectAsync(selected with { ProcessStartTime = selected.ProcessStartTime - 1 }, "Stale process creation time prevents a close request");
                await RejectAsync(selected with { ProcessId = (uint)Environment.ProcessId }, "Toolbox cannot send a close request to itself");
                await RejectAsync(selected with { ClassName = "wrong" }, "Changed window class prevents a close request");
                await RejectAsync(selected with { Title = "wrong" }, "Changed window title requires reselection");
                await RejectAsync(selected with { BlockReason = "protected" }, "Blocked selections cannot be closed");
                ShowWindow(selected.Handle, 6);
                await RejectAsync(selected, "A minimized selection must be selected again");
                ShowWindow(selected.Handle, 9);
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel(); var stopped = false;
                    try { await WindowCloser.RequestCloseAsync(selected, canceled.Token); }
                    catch (OperationCanceledException) { stopped = true; }
                    check(stopped && !File.Exists(ready + ".close"), "Cancellation before dispatch sends no WM_CLOSE");
                }
                var outcome = await WindowCloser.RequestCloseAsync(selected);
                check(outcome == (mode == "accept" ? WindowCloseOutcome.Closed : WindowCloseOutcome.StillOpen),
                    $"{mode}: reported outcome matches the real window rather than successful message posting");
                check(File.ReadAllText(ready + ".close") == "1", "Exactly one normal WM_CLOSE is sent to the selected fixture");
                check(!child.HasExited && IsWindow(handles[1]), "Closing one window leaves its process and sibling window running");
                if (mode == "accept") await RejectAsync(selected, "Closed HWND is rejected without sending another request");
                else
                {
                    using var canceled = new CancellationTokenSource(200);
                    check(await WindowCloser.RequestCloseAsync(selected, canceled.Token) == WindowCloseOutcome.ObservationCancelled,
                        "Canceling observation does not claim the already dispatched request was undone");
                    check(!child.HasExited && IsWindow(selected.Handle), "A window refusing WM_CLOSE is never forcibly terminated");
                }
            }
            finally
            {
                // Teardown targets only the child this test created; never discovered user windows.
                if (!child.HasExited)
                {
                    if (File.Exists(ready))
                    {
                        var liveWindow = File.ReadLines(ready).Select(value => (nint)long.Parse(value)).FirstOrDefault(IsWindow);
                        if (liveWindow != 0) PostMessage(liveWindow, 0x8001, 0, 0);
                    }
                    var exit = child.WaitForExitAsync();
                    if (await Task.WhenAny(exit, Task.Delay(3000)) != exit && !child.HasExited) child.Kill();
                    await child.WaitForExitAsync();
                }
            }
        }
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint handle, uint message, nuint wParam, nint lParam);
}

internal static class WindowCloseFixture
{
    private static readonly WindowProc Proc = HandleMessage;
    private static readonly List<nint> Windows = [];
    private static string _mode = "", _ready = "";
    private static int _requests;
    internal static void Run(string mode, string ready)
    {
        _mode = mode; _ready = ready;
        var className = "ToolboxCloseFixture-" + Environment.ProcessId;
        var cls = new WindowClass { Proc = Marshal.GetFunctionPointerForDelegate(Proc), Instance = GetModuleHandle(null), ClassName = className };
        if (RegisterClass(ref cls) == 0) throw new Exception("Could not register fixture window.");
        for (var index = 0; index < 2; index++)
        {
            var handle = CreateWindowEx(0, className, $"Toolbox isolated close fixture {index} ({mode})", 0x10CF0000,
                40 + index * 240, 40, 220, 160, 0, 0, cls.Instance, 0);
            if (handle == 0) throw new Exception("Could not create fixture window.");
            Windows.Add(handle);
        }
        File.WriteAllLines(ready, Windows.Select(handle => handle.ToInt64().ToString()));
        while (GetMessage(out var message, 0, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
        GC.KeepAlive(Proc);
    }
    private static nint HandleMessage(nint handle, uint message, nuint wParam, nint lParam)
    {
        if (message == 0x0010)
        {
            File.WriteAllText(_ready + ".close", (++_requests).ToString());
            if (_mode == "accept") DestroyWindow(handle);
            return 0;
        }
        if (message == 0x8001) { foreach (var window in Windows.ToArray()) DestroyWindow(window); PostQuitMessage(0); return 0; }
        return DefWindowProc(handle, message, wParam, lParam);
    }
    private delegate nint WindowProc(nint handle, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Style; public nint Proc; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background;
        public string? MenuName; public string ClassName;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        public nint Handle; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extended, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint handle, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessage(out Message message, nint handle, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DispatchMessage(ref Message message);
}

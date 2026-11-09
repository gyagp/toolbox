using Microsoft.UI.Xaml;
using Toolbox.App.Services;
using Toolbox.App.Views;
using Toolbox.Core;
using Toolbox.Platform;

namespace Toolbox.App;

public sealed partial class MainWindow
{
    internal async Task RunWindowCloseTestAsync(string output)
    {
        await Task.Delay(500);
        await NavigateAsync(Tools.ToolId.WindowClose);
        if (WindowClosePage.Visibility != Visibility.Visible || CapturePage.Visibility != Visibility.Collapsed || CaptureTabs.Visibility != Visibility.Collapsed)
            throw new Exception("Window close tool navigation is not independent.");
        await RenderUiAsync(output, "window-close-empty.png");
        var frame = new PixelFrame(800, 600, new byte[800 * 600 * 4]);
        for (var index = 0; index < frame.Pixels.Length; index += 4)
        { frame.Pixels[index] = 234; frame.Pixels[index + 1] = 244; frame.Pixels[index + 2] = 238; frame.Pixels[index + 3] = 255; }
        var selected = new DesktopWindow(12345, 12345, 1, "example.exe", "示例文档 · 未保存的工作 — 仅供测试的窗口", "fixture", 20, 60, 480, 300, null);
        var blocked = selected with { Handle = 12346, X = 80, Y = 100, Width = 200, Height = 100, BlockReason = "不可选择的测试窗口" };
        var bounds = new CaptureRect(-100, -100, 800, 600);
        var picker = new WindowPickerWindow(bounds, frame, bounds, [blocked, selected]);
        picker.Activate();
        try
        {
            await Task.Delay(200);
            picker.BeginClick(new(100, 120)); picker.CompleteClick(new(100, 120));
            if (picker.Result.IsCompleted || picker.Hovered != blocked) throw new Exception("Picker selected a blocked foreground window.");
            picker.MoveSelection(new(40, 80));
            if (picker.Hovered != selected) throw new Exception("Visual selection did not use desktop coordinates.");
            await RenderUiAsync(output, "window-picker.png", (FrameworkElement)picker.Content);
            picker.BeginClick(new(40, 80));
            if (picker.Result.IsCompleted) throw new Exception("Picker closed before consuming mouse button-up.");
            picker.CompleteClick(new(100, 120));
            if (picker.Result.IsCompleted) throw new Exception("Dragging into another window selected the wrong target.");
            picker.BeginClick(new(40, 80)); picker.CompleteClick(new(40, 80));
            if (await picker.Result != selected) throw new Exception("Click did not select exactly the highlighted window.");
        }
        finally { if (!picker.Result.IsCompleted) picker.Cancel(); }
        var canceledPicker = new WindowPickerWindow(bounds, frame, bounds, [selected]);
        canceledPicker.Activate(); canceledPicker.BeginClick(new(40, 80), right: true);
        if (canceledPicker.Result.IsCompleted) throw new Exception("Right-click cancellation happened before button-up.");
        canceledPicker.CompleteClick(new(40, 80));
        if (await canceledPicker.Result is not null) throw new Exception("Picker cancellation returned a target.");
        Activate();
        WindowClosePage.ShowSelection(selected, frame);
        await RenderUiAsync(output, "window-close-selected.png");
        var original = AppWindow.Size;
        AppWindow.Resize(new(880, 680)); await Task.Delay(200);
        await RenderUiAsync(output, "window-close-narrow.png");
        await WindowClosePage.VerifyCancelConfirmationAsync((element, name) => RenderUiAsync(output, name, element));
        WindowClosePage.ShowOutcome(WindowCloseOutcome.StillOpen);
        await RenderUiAsync(output, "window-close-still-open.png");
        AppWindow.Resize(original);
        await NavigateAsync(Tools.ToolId.Screenshot);
        WindowClosePage.VerifyClearedSelection();
        await NavigateAsync(Tools.ToolId.WindowClose);
        await VerifyCloseCancelsWindowToolAsync(output, selected, frame);
    }

    private async Task VerifyCloseCancelsWindowToolAsync(string output, DesktopWindow selected, PixelFrame frame)
    {
        var other = new MainWindow(new CaptureLibrary(Path.Combine(output, "close-images"), Path.Combine(output, "close-videos")));
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        other.Closed += (_, _) => closed.TrySetResult(); other.Activate();
        try
        {
            await other.NavigateAsync(Tools.ToolId.WindowClose);
            other.WindowClosePage.ShowSelection(selected, frame);
            var running = other.WindowClosePage.CloseSelectedAsync();
            await Task.Delay(200);
            if (!other.WindowClosePage.IsBusy) throw new Exception("Confirmation did not block conflicting operations.");
            if (!PostDiagnosticClose(other._handle, 0x0010, 0, 0)) throw new Exception("Could not close diagnostic Toolbox.");
            if (await Task.WhenAny(closed.Task, Task.Delay(5000)) != closed.Task) throw new Exception("Closing Toolbox did not cancel window-close confirmation.");
            await running;
            if (other.WindowClosePage.IsBusy) throw new Exception("Close did not await cancellation.");
        }
        finally { other.WindowClosePage.Cancel(); if (!closed.Task.IsCompleted) other.Close(); }
    }
}

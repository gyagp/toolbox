using System.Runtime.InteropServices;
using Toolbox.Core;

namespace Toolbox.App;

public sealed partial class MainWindow
{
    internal async Task RunFileToolsTestAsync(string output)
    {
        await Task.Delay(400);
        await NavigateAsync(Tools.ToolId.FileManager);
        await Task.Delay(150);
        await RenderUiAsync(output, "file-tools.png");
        var originalSize = AppWindow.Size;
        AppWindow.Resize(new(980, 760));
        await Task.Delay(150);
        await RenderUiAsync(output, "file-tools-narrow.png");
        var fixture = Path.Combine(output, "仅供测试-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(fixture, "This diagnostic must preserve this fixture.");
        await FileToolsPage.VerifyPermanentDeleteUiAsync(fixture, (element, name) => RenderUiAsync(output, name, element));
        if (File.ReadAllText(fixture) != "This diagnostic must preserve this fixture.")
            throw new Exception("Cancel/locked-failure UI diagnostic altered its fixture.");
        AppWindow.Resize(originalSize);
        await VerifyCloseCancelsFileOperationAsync(output);
    }

    private async Task VerifyCloseCancelsFileOperationAsync(string output)
    {
        var other = new MainWindow(new CaptureLibrary(Path.Combine(output, "close-images"), Path.Combine(output, "close-videos")));
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        other.Closed += (_, _) => closed.TrySetResult();
        other.Activate();
        try
        {
            await other.NavigateAsync(Tools.ToolId.FileManager);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = other.FileToolsPage.RunCancellationFixtureAsync(started);
            await started.Task;
            // Send the same close request as the title-bar X, only to our diagnostic window.
            if (!PostDiagnosticClose(other._handle, 0x0010, 0, 0)) throw new Exception("Could not request diagnostic window close.");
            if (await Task.WhenAny(closed.Task, Task.Delay(5000)) != closed.Task)
                throw new Exception("Closing during a file operation failed to cancel and close the window.");
            await running;
            if (!other._closeAfterFileOperation || other.FileToolsPage.IsBusy)
                throw new Exception("Close did not wait for file-operation cancellation.");
        }
        finally
        {
            other.FileToolsPage.RequestCancel();
            if (!closed.Task.IsCompleted) other.Close();
        }
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostDiagnosticClose(nint window, uint message, nuint wParam, nint lParam);
}

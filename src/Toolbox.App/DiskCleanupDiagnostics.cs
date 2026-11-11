using Toolbox.Platform;

namespace Toolbox.App;

public sealed partial class MainWindow
{
    internal async Task RunCleanupTestAsync(string output)
    {
        await Task.Delay(500);
        await RenderUiAsync(output, "capture-startup.png");
        await NavigateAsync(Tools.ToolId.DiskCleanup);
        await Task.Delay(250);
        await RenderUiAsync(output, "cleanup-empty.png");
        await DiskCleanupPage.VerifyCodexUiAsync((element, name) => RenderUiAsync(output, name, element));
        await RenderUiAsync(output, "codex-preview.png");
        // Render complete, partial and unavailable states with synthetic data. No cleanup is launched.
        var report = new CleanupReport("C:\\", DateTimeOffset.Now,
        [
            new(new("当前用户的临时文件", @"C:\Users\Example\AppData\Local\Temp", "可能包含未保存的工作或恢复数据，请保留不确定的内容。"),
                new(2_450_000_000, 1024, 0, true, true, "文件大小估算，不是可释放空间。")),
            new(new("DirectX 着色器缓存", @"C:\Users\Example\AppData\Local\D3DSCache", "由 Windows 审查，清理后可能需要重新编译。"),
                new(250_000_000, 20, 2, false, true, "部分内容不可读或已跳过。")),
            new(new("Windows 临时文件", @"C:\Windows\Temp", "不请求管理员权限。"),
                new(0, 0, 1, false, false, "无法读取，不代表为空。")),
            new(new("回收站 · 仅查看", @"C:\$Recycle.Bin", "保留以便恢复误删文件。"),
                new(800_000_000, 18, 0, true, true, "Windows 回收站报告值；不计作建议清理量。"))
        ]);
        await DiskCleanupPage.VerifyDiagnosticScanControlsAsync(report);
        DiskCleanupPage.ShowDiagnosticReport(report);
        await Task.Delay(200);
        await RenderUiAsync(output, "cleanup-results.png");
        var original = AppWindow.Size;
        AppWindow.Resize(new(980, 760));
        await Task.Delay(250);
        await RenderUiAsync(output, "cleanup-narrow.png");
        DiskCleanupPage.ScrollDiagnosticResultsIntoView();
        await Task.Delay(200);
        await RenderUiAsync(output, "cleanup-details-narrow.png");
        AppWindow.Resize(original);
        await NavigateAsync(Tools.ToolId.FileManager);
        await NavigateAsync(Tools.ToolId.DiskCleanup);
        DiskCleanupPage.ShowDiagnosticReport(report);
        await VerifyCloseCancelsCodexCleanupAsync(output);
    }

    private async Task VerifyCloseCancelsCodexCleanupAsync(string output)
    {
        var other = new MainWindow(new Toolbox.Core.CaptureLibrary(Path.Combine(output, "close-images"), Path.Combine(output, "close-videos")));
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        other.Closed += (_, _) => closed.TrySetResult(); other.Activate();
        try
        {
            await other.NavigateAsync(Tools.ToolId.DiskCleanup);
            await Task.Delay(250);
            var running = other.DiskCleanupPage.BeginCodexCloseFixtureAsync();
            await Task.Delay(150);
            if (!other.DiskCleanupPage.IsBusy) throw new Exception("Codex confirmation is not busy.");
            if (!PostDiagnosticClose(other._handle, 0x0010, 0, 0)) throw new Exception("Could not close the diagnostic Toolbox.");
            if (await Task.WhenAny(closed.Task, Task.Delay(5000)) != closed.Task) throw new Exception("Closing Toolbox did not cancel Codex cleanup confirmation.");
            await running;
            if (other.DiskCleanupPage.IsBusy) throw new Exception("Close did not wait for Codex cleanup to stop.");
        }
        finally { other.DiskCleanupPage.RequestCancel(); if (!closed.Task.IsCompleted) other.Close(); }
    }
}

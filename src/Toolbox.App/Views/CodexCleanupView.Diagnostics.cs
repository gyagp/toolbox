using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Toolbox.Platform;

namespace Toolbox.App.Views;

public sealed partial class CodexCleanupView
{
    internal async Task VerifyConfirmationAsync(Func<FrameworkElement, string, Task> render)
    {
        if (KeepPicker.SelectedIndex != 1) throw new Exception("Codex retention must default to two versions.");
        var fixture = new CodexReleaseReport(CodexReleaseCleaner.DefaultRoot, 2,
        [
            new("0.167.1-x86_64-pc-windows-msvc", "fixture", "0.167.1", "x64", 700_000_000, "fixture", "current 启动器正在指向"),
            new("0.167.0-x86_64-pc-windows-msvc", "fixture", "0.167.0", "x64", 650_000_000, "fixture", "保留最近 2 个版本"),
            new("0.166.0-x86_64-pc-windows-msvc", "fixture", "0.166.0", "x64", 600_000_000, "fixture", null)
        ], ["合成界面测试，不操作实际 Codex 安装。"]);
        ShowReport(fixture);
        if (!DeleteButton.IsEnabled) throw new Exception("A reviewed candidate must enable the confirmation action.");
        KeepPicker.SelectedIndex = 0;
        if (_report is not null || DeleteButton.IsEnabled) throw new Exception("Changing retention must invalidate the old preview.");
        KeepPicker.SelectedIndex = 1; ShowReport(fixture);
        var running = DeleteAsync(); await Task.Delay(150);
        if (!IsBusy || _confirmation is null || _confirmation.DefaultButton != ContentDialogButton.Close || _confirmation.IsPrimaryButtonEnabled)
            throw new Exception("Permanent Codex cleanup must require typed confirmation and default to cancel.");
        var input = (TextBox)((StackPanel)_confirmation.Content).Children.Last();
        input.Text = "wrong";
        await Task.Delay(50);
        if (_confirmation.IsPrimaryButtonEnabled) throw new Exception("Incorrect confirmation enabled deletion.");
        input.Text = "删除";
        await Task.Delay(50);
        if (!_confirmation.IsPrimaryButtonEnabled) throw new Exception("Typed confirmation did not enable the primary action.");
        await render(_confirmation, "codex-confirmation.png");
        Cancel(); await running;
        if (IsBusy || !PreviewButton.IsEnabled || ResultStatus.Title != "已取消") throw new Exception("Cancel confirmation did not restore idle controls.");
        ShowReport(fixture);
    }
}

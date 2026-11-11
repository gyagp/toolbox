using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Toolbox.Platform;

namespace Toolbox.App.Views;

public sealed partial class CodexCleanupView : UserControl
{
    private readonly CodexReleaseCleaner _cleaner = new();
    private CodexReleaseReport? _report;
    private CancellationTokenSource? _work;
    private ContentDialog? _confirmation;
    public bool IsBusy => _work is not null;
    public event Action<bool>? BusyChanged;
    public CodexCleanupView() { InitializeComponent(); PackageRoot.Text = CodexReleaseCleaner.DefaultRoot + "\\releases"; }
    internal void Cancel() { _work?.Cancel(); _confirmation?.Hide(); }
    private int Keep => KeepPicker.SelectedIndex == 0 ? 1 : 2;
    private void Keep_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DeleteButton is null) return;
        _report = null; ReleaseList.ItemsSource = null; DeleteButton.IsEnabled = false;
        Summary.Text = "保留策略已更改，请重新扫描。";
    }
    private void UpdateActions()
    {
        PreviewButton.IsEnabled = KeepPicker.IsEnabled = !IsBusy;
        DeleteButton.IsEnabled = !IsBusy && _report?.CandidateCount > 0;
        WorkProgress.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
        BusyChanged?.Invoke(IsBusy);
    }
    private async void Preview_Click(object sender, RoutedEventArgs e) => await ScanAsync();
    internal async Task ScanAsync()
    {
        if (IsBusy) return;
        _report = null; ReleaseList.ItemsSource = null; Details.Text = ""; ResultStatus.IsOpen = false;
        using var work = new CancellationTokenSource(); _work = work; WorkProgress.IsIndeterminate = true; UpdateActions();
        Summary.Text = "只读核对版本、启动指向和运行进程…";
        try
        {
            var keep = Keep;
            var report = await Task.Run(() => _cleaner.Scan(keep, work.Token));
            work.Token.ThrowIfCancellationRequested(); ShowReport(report);
        }
        catch (OperationCanceledException) { Summary.Text = "扫描已取消，没有删除文件。"; }
        catch (Exception error) { Notify("无法安全核对安装", error.Message, InfoBarSeverity.Error); Summary.Text = "没有执行删除；请修复安装或稍后重试。"; }
        finally { _work = null; UpdateActions(); }
    }
    internal void ShowReport(CodexReleaseReport report)
    {
        _report = report;
        ReleaseList.ItemsSource = report.Releases.Select(release => new ReleaseRow(release)).ToArray();
        Summary.Text = $"{report.Releases.Count} 个版本 · 保留 / 跳过 {report.Releases.Count - report.CandidateCount} 个 · 待删除 {report.CandidateCount} 个（{Size(report.CandidateBytes)} 逻辑大小，实际释放量可能不同）";
        Details.Text = string.Join("\n", report.Warnings);
        DeleteButton.IsEnabled = !IsBusy && report.CandidateCount > 0;
    }
    private async void Delete_Click(object sender, RoutedEventArgs e) => await DeleteAsync();
    internal async Task DeleteAsync()
    {
        if (IsBusy || _report is not { CandidateCount: > 0 } report) return;
        using var work = new CancellationTokenSource(); _work = work; WorkProgress.IsIndeterminate = true; UpdateActions();
        try
        {
            _confirmation = CreateConfirmation(report);
            var decision = await _confirmation.ShowAsync(); _confirmation = null;
            if (decision != ContentDialogResult.Primary || work.IsCancellationRequested)
            { Notify("已取消", "没有发送删除请求。", InfoBarSeverity.Informational); return; }
            ResultStatus.IsOpen = false;
            var progress = new Progress<CodexCleanupProgress>(update =>
            {
                if (_work != work) return;
                WorkProgress.IsIndeterminate = false; WorkProgress.Maximum = Math.Max(1, update.Total); WorkProgress.Value = update.Completed;
                Summary.Text = $"{update.Completed} / {update.Total} · {update.Name}";
            });
            var result = await _cleaner.DeleteAsync(report, work.Token, progress);
            // A used preview is invalid even when no package could be removed; a new action needs a new scan.
            _report = null; ReleaseList.ItemsSource = null;
            Details.Text = string.Join("\n", result.Deleted.Select(name => "已删除：" + name).Concat(result.Skipped.Select(message => "跳过 / 未完成：" + message)));
            Summary.Text = $"已完整删除 {result.Deleted.Count} 个旧版本。请重新扫描核对剩余版本。";
            Notify(result.Cancelled ? "已取消后续删除" : "旧版本整理结束", "已完成的删除不会回滚；没有结束进程。" +
                (result.Skipped.Count > 0 ? "部分版本未完成，请查看处理记录。" : ""),
                result.Cancelled || result.Skipped.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { _report = null; Notify("已取消", "没有继续删除。已完成的步骤不会回滚。", InfoBarSeverity.Warning); }
        catch (Exception error) { _report = null; Notify("已停止整理", error.Message + "\n请重新扫描。没有结束进程。", InfoBarSeverity.Error); }
        finally { _confirmation = null; _work = null; UpdateActions(); }
    }
    internal ContentDialog CreateConfirmation(CodexReleaseReport report)
    {
        var input = new TextBox { Header = "输入“删除”确认", PlaceholderText = "删除" };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = $"永久删除 {report.CandidateCount} 个旧程序包，保留每种架构最近 {report.KeepNewest} 个版本及 current / 更新指向 / 运行版本。\n不经过回收站。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = report.Root + "\\releases", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 11 });
        content.Children.Add(new ScrollViewer { MaxHeight = 180, Content = new TextBlock { Text = string.Join("\n", report.Releases.Where(release => release.CanDelete).Select(release => release.Name)), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 } });
        content.Children.Add(input);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "删除这些 Codex 旧版本？", Content = content,
            PrimaryButtonText = "永久删除旧版本", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = false };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = input.Text == "删除";
        return dialog;
    }
    private void Notify(string title, string message, InfoBarSeverity severity)
    { ResultStatus.Title = title; ResultStatus.Message = message; ResultStatus.Severity = severity; ResultStatus.IsOpen = true; }
    private static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.##} GiB" : $"{bytes / (double)(1L << 20):0.##} MiB";
    private sealed class ReleaseRow(CodexRelease release)
    {
        public string Name => release.Name;
        public string Status => release.CanDelete ? "待删除 · 已识别旧程序包" : "保留 · " + release.KeepReason;
        public string Size => release.Fingerprint.Length == 0 ? "未统计" : CodexCleanupView.Size(release.Bytes);
    }
}

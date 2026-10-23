using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Toolbox.Core;
using Toolbox.Platform;
using Windows.Storage.Pickers;

namespace Toolbox.App.Views;

public sealed partial class FileToolsView : UserControl
{
    private nint _owner;
    private bool _ready;
    private FileActionPlan? _pending;
    private CancellationTokenSource? _scan;
    private CancellationTokenSource? _action;
    private ContentDialog? _dialog;
    internal bool IsBusy { get; private set; }
    internal event Action<bool>? BusyChanged;

    public FileToolsView() { InitializeComponent(); _ready = true; }
    internal void Initialize(nint owner) => _owner = owner;

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        SourcePath.IsEnabled = DestinationPath.IsEnabled = PickSourceButton.IsEnabled =
            PickDestinationButton.IsEnabled = MoveButton.IsEnabled = DeleteButton.IsEnabled = PermanentDeleteButton.IsEnabled = ProcessList.IsEnabled = !busy;
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyRing.IsActive = busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = busy;
        CancelButton.Content = "取消操作";
        UpdateActions();
        BusyChanged?.Invoke(busy);
    }

    private void UpdateActions()
    {
        TerminateButton.IsEnabled = !IsBusy && ProcessList.SelectedItems.Count > 0;
        RetryButton.IsEnabled = !IsBusy && _pending is not null;
        RetryButton.Content = _pending?.Action == FileAction.PermanentDelete ? "重试彻底删除…" : "重试操作";
        TerminateButton.Content = _pending is null ? "结束选中进程…" : "结束选中进程并重试…";
    }

    private void Notify(string title, string detail, InfoBarSeverity severity)
    {
        OperationStatus.Title = title; OperationStatus.Message = detail;
        OperationStatus.Severity = severity; OperationStatus.IsOpen = true;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        using var actionCancellation = new CancellationTokenSource();
        _action = actionCancellation;
        OperationStatus.IsOpen = false;
        ProgressText.Text = "正在准备…";
        SetBusy(true);
        try { await action(); }
        catch (OperationCanceledException)
        {
            _pending = null;
            ProcessList.ItemsSource = null;
            ScanWarnings.Visibility = Visibility.Collapsed;
            LockSummary.Text = "操作已取消。请核对剩余内容后重新选择操作。";
            Notify("操作已取消", "已停止后续处理；已完成的移动或删除不会自动回滚。请核对源路径、目标路径或回收站中的内容。", InfoBarSeverity.Warning);
        }
        catch (Exception error) { Notify("操作未完成", error.Message, InfoBarSeverity.Error); }
        finally { _action = null; SetBusy(false); }
    }

    internal void RequestCancel()
    {
        if (!IsBusy) return;
        _action?.Cancel();
        _scan?.Cancel();
        _dialog?.Hide();
        CancelButton.IsEnabled = false;
        ProgressText.Text = "正在请求取消，等待 Windows 停止当前步骤…";
    }

    private async Task<bool> ShowConfirmationAsync(ContentDialog dialog)
    {
        _action?.Token.ThrowIfCancellationRequested();
        _dialog = dialog;
        try
        {
            var result = await dialog.ShowAsync();
            _action?.Token.ThrowIfCancellationRequested();
            return result == ContentDialogResult.Primary;
        }
        finally { _dialog = null; }
    }

    private async Task<bool> ConfirmAsync(string title, string detail, string action)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = action, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        return await ShowConfirmationAsync(dialog);
    }

    private (ContentDialog Dialog, TextBox Confirmation) CreatePermanentDeleteDialog(FileActionPlan plan)
    {
        var name = Path.GetFileName(plan.Source);
        var confirmation = new TextBox { Header = "输入以下名称以确认", PlaceholderText = name };
        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(new TextBlock { Text = plan.Source, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        content.Children.Add(new TextBlock { Text = "此项目及文件夹内的全部内容将直接删除，不进入回收站，无法通过回收站或 Toolbox 恢复。请先确认没有需要保留的内容。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        content.Children.Add(confirmation);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "彻底删除此项目？",
            Content = new ScrollViewer { Content = content, MaxHeight = 360, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            PrimaryButtonText = "彻底删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = false };
        confirmation.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = string.Equals(confirmation.Text, name, StringComparison.Ordinal);
        return (dialog, confirmation);
    }

    private async Task<bool> ConfirmPermanentDeleteAsync(FileActionPlan plan)
    {
        var (dialog, _) = CreatePermanentDeleteDialog(plan.Revalidate());
        return await ShowConfirmationAsync(dialog);
    }

    private async Task ExecuteAsync(FileActionPlan plan, bool permanentDeleteConfirmed = false)
    {
        _action?.Token.ThrowIfCancellationRequested();
        // All initial/retry paths require fresh confirmation. The process-termination
        // path confirms BEFORE terminating anything and passes that one-use decision.
        if (plan.Action == FileAction.PermanentDelete && !permanentDeleteConfirmed && !await ConfirmPermanentDeleteAsync(plan)) return;
        ProgressText.Text = plan.Action switch
        {
            FileAction.Move => "正在移动，请稍候…",
            FileAction.Recycle => "正在移入回收站，请稍候…",
            _ => "正在彻底删除，请稍候…"
        };
        _pending = plan;
        try
        {
            // A retry revalidates the original intent; no fresh destructive action is inferred.
            var token = _action?.Token ?? CancellationToken.None;
            await ShellFileOperation.ExecuteAsync(plan, _owner, token);
            _pending = null;
            ProcessList.ItemsSource = null;
            ScanWarnings.Visibility = Visibility.Collapsed;
            LockSummary.Text = "操作已完成。可选择下一个文件或文件夹。";
            Notify(plan.Action switch { FileAction.Move => "移动完成", FileAction.PermanentDelete => "彻底删除完成", _ => "删除操作完成" },
                plan.Action switch
                {
                    FileAction.Move => plan.Destination!,
                    FileAction.PermanentDelete => $"{plan.Source}\n未进入回收站，无法通过回收站或 Toolbox 恢复。",
                    _ => "默认移入 Windows 回收站。若 Windows 另行询问了永久删除，以你的选择为准。"
                }, InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            _action?.Token.ThrowIfCancellationRequested();
            Notify("操作未完成", error.Message + (plan.Action == FileAction.PermanentDelete
                ? "\n文件夹可能已有部分内容被彻底删除，无法通过回收站恢复；请核对剩余内容后再重试。" : ""), InfoBarSeverity.Error);
            try { await InspectAsync(plan.Source); }
            catch (OperationCanceledException) { throw; }
            catch (Exception inspectError) { ScanWarnings.Text = "占用查询也未完成：" + inspectError.Message; ScanWarnings.Visibility = Visibility.Visible; }
        }
    }

    private async Task InspectAsync(string path)
    {
        ProcessList.ItemsSource = null;
        LockSummary.Text = "正在查询…";
        ScanWarnings.Visibility = Visibility.Collapsed;
        ProgressText.Text = "正在查找占用…";
        using var scan = CancellationTokenSource.CreateLinkedTokenSource(_action?.Token ?? CancellationToken.None);
        _scan = scan;
        CancelButton.Visibility = Visibility.Visible;
        CancelButton.Content = "取消查询";
        try
        {
            var progress = new Progress<int>(count => { if (_scan == scan && !scan.IsCancellationRequested) ProgressText.Text = $"已检查 {count} 个文件…"; });
            var report = await Task.Run(() => LockInspector.Inspect(path, scan.Token, progress));
            ProcessList.ItemsSource = report.Processes;
            LockSummary.Text = report.Processes.Count > 0
                ? $"检查了 {report.DirectoriesScanned} 个目录和 {report.FilesScanned} 个文件，发现 {report.Processes.Count} 个关联进程。勾选你要结束的进程。"
                : $"检查了 {report.DirectoriesScanned} 个目录和 {report.FilesScanned} 个文件，未发现占用进程。可能是权限、驱动占用，或占用已解除；查询结果不保证文件一定可移动。";
            ScanWarnings.Text = string.Join("\n", report.Warnings.Take(12)) +
                (report.Warnings.Count > 12 ? $"\n另有 {report.Warnings.Count - 12} 条未列出的查询警告。" : "");
            ScanWarnings.Visibility = report.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            // Results may be below the fold at high DPI. Measure the new rows before
            // scrolling; calling BringIntoView before layout can silently do nothing.
            UpdateLayout();
            PageScroller.ChangeView(null, PageScroller.ScrollableHeight, null, true);
        }
        finally { _scan = null; CancelButton.Visibility = Visibility.Collapsed; }
    }

    private async void Move_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var plan = FileActionPlan.Create(FileAction.Move, SourcePath.Text, DestinationPath.Text);
        if (await ConfirmAsync("移动此项目？", $"从：{plan.Source}\n\n到：{plan.Destination}", "移动")) await ExecuteAsync(plan);
    });

    private async void Delete_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var plan = FileActionPlan.Create(FileAction.Recycle, SourcePath.Text);
        if (await ConfirmAsync("移入 Windows 回收站？", $"{plan.Source}\n\n文件夹中的内容也会被移入回收站。不支持回收站的位置，Windows 会另行提示。", "移入回收站")) await ExecuteAsync(plan);
    });

    private async void PermanentDelete_Click(object sender, RoutedEventArgs e) => await RunAsync(() =>
        ExecuteAsync(FileActionPlan.Create(FileAction.PermanentDelete, SourcePath.Text)));

    private async void Retry_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { if (_pending is { } plan) await ExecuteAsync(plan); });
    private void Cancel_Click(object sender, RoutedEventArgs e) => RequestCancel();

    private async void Terminate_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var selected = ProcessList.SelectedItems.Cast<LockingProcess>().ToArray();
        if (selected.Length == 0) return;
        if (!await ConfirmAsync("结束选中的进程？", string.Join("\n", selected.Select(p => p.Label)) +
            "\n\n这些程序未保存的工作可能丢失。不会结束未勾选的进程或子进程。" + (_pending is null ? "" :
                _pending.Action == FileAction.PermanentDelete ? $"\n结束成功后将重试彻底删除：\n{_pending.Source}\n不进入回收站。" : "\n结束成功后将重试刚才的文件操作。"), "结束进程")) return;
        var permanentDeleteConfirmed = false;
        if (_pending is { Action: FileAction.PermanentDelete } permanent)
        {
            if (!await ConfirmPermanentDeleteAsync(permanent)) return;
            permanentDeleteConfirmed = true;
        }
        ProgressText.Text = "正在结束选中的进程…";
        var failures = new List<string>();
        foreach (var process in selected)
        {
            _action?.Token.ThrowIfCancellationRequested();
            try { await LockInspector.TerminateAsync(process); }
            catch (Exception error) { failures.Add($"{process.Label}：{error.Message}"); }
        }
        if (failures.Count > 0)
        {
            Notify("部分进程未能结束", string.Join("\n", failures) + "\n未重试文件操作。", InfoBarSeverity.Warning);
            await InspectAsync(SourcePath.Text);
        }
        else if (_pending is { } plan) await ExecuteAsync(plan, permanentDeleteConfirmed);
        else
        {
            Notify("所选进程已结束", "已重新检查占用。", InfoBarSeverity.Success);
            await InspectAsync(SourcePath.Text);
        }
    });

    private async void PickFile_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _owner);
        var file = await picker.PickSingleFileAsync().AsTask(_action?.Token ?? CancellationToken.None);
        if (file is not null) SourcePath.Text = file.Path;
    });

    private async Task PickFolderAsync(TextBox target)
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _owner);
        var folder = await picker.PickSingleFolderAsync().AsTask(_action?.Token ?? CancellationToken.None);
        if (folder is not null) target.Text = folder.Path;
    }
    private async void PickFolder_Click(object sender, RoutedEventArgs e) => await RunAsync(() => PickFolderAsync(SourcePath));
    private async void PickDestination_Click(object sender, RoutedEventArgs e) => await RunAsync(() => PickFolderAsync(DestinationPath));
    private void Process_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_ready) UpdateActions(); }
    private void Path_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _pending = null;
        ProcessList.ItemsSource = null;
        ScanWarnings.Visibility = Visibility.Collapsed;
        LockSummary.Text = "移动或删除失败时，会自动检查并在这里显示占用进程。";
        UpdateActions();
    }

    internal async Task RenderFixtureAsync(string path)
    {
        SourcePath.Text = path;
        await InspectAsync(path);
        if (ProcessList.SelectedItems.Count != 0 || TerminateButton.IsEnabled)
            throw new InvalidOperationException("Lock results must never preselect processes for termination.");
    }
}

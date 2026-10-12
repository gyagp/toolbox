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
    internal bool IsBusy { get; private set; }
    internal event Action<bool>? BusyChanged;

    public FileToolsView() { InitializeComponent(); _ready = true; }
    internal void Initialize(nint owner) => _owner = owner;

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        SourcePath.IsEnabled = DestinationPath.IsEnabled = PickSourceButton.IsEnabled =
            PickDestinationButton.IsEnabled = MoveButton.IsEnabled = DeleteButton.IsEnabled = InspectButton.IsEnabled = ProcessList.IsEnabled = !busy;
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyRing.IsActive = busy;
        UpdateActions();
        BusyChanged?.Invoke(busy);
    }

    private void UpdateActions()
    {
        TerminateButton.IsEnabled = !IsBusy && ProcessList.SelectedItems.Count > 0;
        RetryButton.IsEnabled = !IsBusy && _pending is not null;
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
        SetBusy(true);
        try { await action(); }
        catch (OperationCanceledException) { Notify("查询已取消", "占用查询已停止，可重新查询。", InfoBarSeverity.Informational); }
        catch (Exception error) { Notify("操作未完成", error.Message, InfoBarSeverity.Error); }
        finally { SetBusy(false); }
    }

    private async Task<bool> ConfirmAsync(string title, string detail, string action)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = action, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ExecuteAsync(FileActionPlan plan)
    {
        ProgressText.Text = plan.Action == FileAction.Move ? "正在移动，请稍候…" : "正在移入回收站，请稍候…";
        _pending = plan;
        try
        {
            // A retry revalidates the original intent; no fresh destructive action is inferred.
            await ShellFileOperation.ExecuteAsync(plan, _owner);
            _pending = null;
            ProcessList.ItemsSource = null;
            ScanWarnings.Visibility = Visibility.Collapsed;
            LockSummary.Text = "操作已完成。可选择下一个文件或文件夹。";
            Notify(plan.Action == FileAction.Move ? "移动完成" : "删除操作完成",
                plan.Action == FileAction.Move ? plan.Destination! : "默认移入 Windows 回收站。若 Windows 另行询问了永久删除，以你的选择为准。", InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            Notify("操作未完成", error.Message, InfoBarSeverity.Error);
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
        using var scan = new CancellationTokenSource();
        _scan = scan;
        CancelButton.Visibility = Visibility.Visible;
        try
        {
            var progress = new Progress<int>(count => { if (_scan == scan) ProgressText.Text = $"已检查 {count} 个文件…"; });
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

    private async void Inspect_Click(object sender, RoutedEventArgs e) => await RunAsync(() => InspectAsync(SourcePath.Text));
    private async void Retry_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { if (_pending is { } plan) await ExecuteAsync(plan); });
    private void Cancel_Click(object sender, RoutedEventArgs e) => _scan?.Cancel();

    private async void Terminate_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var selected = ProcessList.SelectedItems.Cast<LockingProcess>().ToArray();
        if (selected.Length == 0) return;
        if (!await ConfirmAsync("结束选中的进程？", string.Join("\n", selected.Select(p => p.Label)) +
            "\n\n这些程序未保存的工作可能丢失。不会结束未勾选的进程或子进程。" + (_pending is null ? "" : "\n结束成功后将重试刚才的文件操作。"), "结束进程")) return;
        ProgressText.Text = "正在结束选中的进程…";
        var failures = new List<string>();
        foreach (var process in selected)
        {
            try { await LockInspector.TerminateAsync(process); }
            catch (Exception error) { failures.Add($"{process.Label}：{error.Message}"); }
        }
        if (failures.Count > 0)
        {
            Notify("部分进程未能结束", string.Join("\n", failures) + "\n未重试文件操作。", InfoBarSeverity.Warning);
            await InspectAsync(SourcePath.Text);
        }
        else if (_pending is { } plan) await ExecuteAsync(plan);
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
        var file = await picker.PickSingleFileAsync();
        if (file is not null) SourcePath.Text = file.Path;
    });

    private async Task PickFolderAsync(TextBox target)
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _owner);
        var folder = await picker.PickSingleFolderAsync();
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
        LockSummary.Text = "路径已更改，请重新查找占用。";
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

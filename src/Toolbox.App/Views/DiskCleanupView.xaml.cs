using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Toolbox.Platform;

namespace Toolbox.App.Views;

public sealed partial class DiskCleanupView : UserControl
{
    private CancellationTokenSource? _scan;
    private bool _ready;
    private bool _loadedDrives;

    public bool IsBusy => CodexPage.IsBusy;
    public event Action<bool>? BusyChanged;
    public DiskCleanupView()
    {
        InitializeComponent(); _ready = true;
        CodexPage.BusyChanged += busy =>
        {
            CodexCancelTray.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            UpdateActions(); BusyChanged?.Invoke(busy);
        };
    }

    internal void Activate()
    {
        if (!_loadedDrives) RefreshDrives();
    }

    internal void Deactivate() { _scan?.Cancel(); CodexPage.Cancel(); }
    internal void RequestCancel() => Deactivate();
    private void CodexCancel_Click(object sender, RoutedEventArgs e) => CodexPage.Cancel();

    private void RefreshDrives()
    {
        if (_scan is not null) return;
        try
        {
            var previous = (DrivePicker.SelectedItem as DriveRow)?.Drive.Root;
            var drives = DiskCleanupInspector.GetDrives().Select(drive => new DriveRow(drive)).ToArray();
            DrivePicker.ItemsSource = drives;
            var preferred = previous ?? System.IO.Path.GetPathRoot(Environment.SystemDirectory);
            DrivePicker.SelectedItem = drives.FirstOrDefault(row => row.Drive.Root.Equals(preferred, StringComparison.OrdinalIgnoreCase)) ?? drives.FirstOrDefault();
            _loadedDrives = true;
            if (drives.Length == 0) CapacityText.Text = "未找到可用的本地固定磁盘。可使用 Windows 存储设置。";
            UpdateActions();
        }
        catch (Exception error) { Notify("无法读取磁盘", error.Message, InfoBarSeverity.Error); }
    }

    private void UpdateActions()
    {
        var busy = _scan is not null || IsBusy;
        DrivePicker.IsEnabled = RefreshButton.IsEnabled = StorageButton.IsEnabled = !busy;
        ScanButton.IsEnabled = CleanupButton.IsEnabled = !busy && DrivePicker.SelectedItem is DriveRow;
        CancelButton.Visibility = ScanRing.Visibility = _scan is not null ? Visibility.Visible : Visibility.Collapsed;
        ScanRing.IsActive = _scan is not null;
    }

    private void Drive_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ResultsList.ItemsSource = null;
        ResultsCaption.Text = "尚未扫描。大小估算不代表可释放空间；压缩、硬链接及扫描期间的变化会影响数值。";
        ScanStatus.Text = "只读检查已知缓存与回收站，不扫描个人文件夹。";
        OperationStatus.IsOpen = false;
        if (DrivePicker.SelectedItem is DriveRow row)
        {
            var drive = row.Drive;
            DiskUsage.Value = drive.TotalBytes > 0 ? 100.0 * (drive.TotalBytes - drive.AvailableBytes) / drive.TotalBytes : 0;
            CapacityText.Text = $"可用 {FormatBytes(drive.AvailableBytes)} / 总计 {FormatBytes(drive.TotalBytes)} · {DateTime.Now:HH:mm} 读取";
        }
        else { DiskUsage.Value = 0; CapacityText.Text = "未选择磁盘。"; }
        UpdateActions();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) =>
        await ScanAsync((drive, token, progress) => Task.Run(() => DiskCleanupInspector.Scan(drive, token, progress)));

    private async Task ScanAsync(Func<string, CancellationToken, IProgress<string>, Task<CleanupReport>> inspect)
    {
        if (_scan is not null || DrivePicker.SelectedItem is not DriveRow row) return;
        using var scan = new CancellationTokenSource();
        _scan = scan;
        UpdateActions();
        OperationStatus.IsOpen = false;
        ResultsList.ItemsSource = null;
        ResultsCaption.Text = "正在扫描；此过程不会删除或移动文件。";
        try
        {
            var progress = new Progress<string>(message => { if (_scan == scan) ScanStatus.Text = message; });
            var report = await inspect(row.Drive.Root, scan.Token, progress);
            scan.Token.ThrowIfCancellationRequested();
            ShowReport(report);
            var current = DiskCleanupInspector.GetDrives().FirstOrDefault(drive => drive.Root == report.DriveRoot);
            if (current is not null)
            {
                DiskUsage.Value = current.TotalBytes > 0 ? 100.0 * (current.TotalBytes - current.AvailableBytes) / current.TotalBytes : 0;
                CapacityText.Text = $"可用 {FormatBytes(current.AvailableBytes)} / 总计 {FormatBytes(current.TotalBytes)} · {report.ScannedAt:HH:mm} 读取";
            }
        }
        catch (OperationCanceledException)
        {
            ScanStatus.Text = "扫描已取消，没有删除或移动文件。";
            ResultsCaption.Text = "本次结果已丢弃，可重新扫描。";
        }
        catch (Exception error)
        {
            ScanStatus.Text = "扫描未完成，没有执行清理。";
            ResultsCaption.Text = "未取得完整结果，可重新扫描。";
            Notify("扫描未完成", error.Message, InfoBarSeverity.Error);
        }
        finally { _scan = null; UpdateActions(); }
    }

    private void ShowReport(CleanupReport report)
    {
        ResultsList.ItemsSource = report.Items.Select(item => new ResultRow(item)).ToArray();
        var incomplete = report.Items.Count(item => !item.Estimate.Complete);
        ScanStatus.Text = incomplete == 0 ? "检查完成，没有删除或移动文件。" : $"检查结束，{incomplete} 个位置结果不完整或不可读。没有执行清理。";
        ResultsCaption.Text = $"{report.DriveRoot} · {report.ScannedAt:yyyy-MM-dd HH:mm} · {report.Items.Count} 个位置。仅为占用参考，不是可释放空间；未扫描的个人文件和应用不包含在此列表中。";
    }

    private void Notify(string title, string message, InfoBarSeverity severity)
    {
        OperationStatus.Title = title; OperationStatus.Message = message;
        OperationStatus.Severity = severity; OperationStatus.IsOpen = true;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDrives();
    private void Cancel_Click(object sender, RoutedEventArgs e) => _scan?.Cancel();
    private void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        if (_scan is not null || DrivePicker.SelectedItem is not DriveRow row) return;
        try
        {
            WindowsCleanupLauncher.OpenDiskCleanup(row.Drive.Root);
            Notify("已打开 Windows 磁盘清理", "请审查 Windows 中已勾选的类别，取消不确定的项目；Toolbox 未执行清理。完成后请重新扫描。", InfoBarSeverity.Informational);
        }
        catch (Exception error) { Notify("未能打开磁盘清理", error.Message, InfoBarSeverity.Error); }
    }

    private void Storage_Click(object sender, RoutedEventArgs e)
    {
        if (_scan is not null) return;
        try
        {
            WindowsCleanupLauncher.OpenStorageSettings();
            Notify("已打开 Windows 存储设置", "这是系统设置入口，可能显示系统磁盘；请在 Windows 中核对磁盘和清理类别。Toolbox 不会启用存储感知。", InfoBarSeverity.Informational);
        }
        catch (Exception error) { Notify("未能打开存储设置", error.Message, InfoBarSeverity.Error); }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    private sealed record DriveRow(CleanupDrive Drive)
    {
        public override string ToString() => Drive.Root;
    }

    private sealed class ResultRow(CleanupItem item)
    {
        public string Title => item.Location.Title;
        public string Path => item.Location.Path;
        public string Guidance => item.Location.Guidance;
        public string SizeText => !item.Estimate.HasData ? "未能统计" :
            FormatBytes(item.Estimate.Bytes) + (item.Estimate.Complete ? "" : " · 部分结果");
        public string Details => $"{item.Estimate.Files:N0} 项 · {item.Estimate.Note}" +
            (item.Estimate.Skipped > 0 ? $" 跳过 {item.Estimate.Skipped:N0} 项。" : "");
    }

    internal void ShowDiagnosticReport(CleanupReport report)
    {
        ShowReport(report);
        if (_scan is not null || ScanRing.IsActive || CancelButton.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Cleanup preview must be idle and read-only.");
    }

    internal void ScrollDiagnosticResultsIntoView()
    {
        UpdateLayout();
        PageScroller.ChangeView(null, PageScroller.ScrollableHeight, null, true);
    }

    internal async Task VerifyCodexUiAsync(Func<FrameworkElement, string, Task> render)
    {
        CodexPage.StartBringIntoView(); await Task.Delay(150);
        await CodexPage.VerifyConfirmationAsync(render);
        CodexPage.StartBringIntoView();
    }

    internal Task BeginCodexCloseFixtureAsync()
    {
        CodexPage.ShowReport(new(CodexReleaseCleaner.DefaultRoot, 2,
            [new("fixture", "fixture", "0.1.0", "x64", 1, "fixture", null)], []));
        return CodexPage.DeleteAsync();
    }

    internal async Task VerifyDiagnosticScanControlsAsync(CleanupReport fixture)
    {
        // Exercise the production state transitions with a cancellable synthetic reader.
        // Tests never launch cleanmgr or clean actual caches.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = ScanAsync(async (_, token, _) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return fixture;
        });
        await started.Task;
        if (DrivePicker.IsEnabled || CleanupButton.IsEnabled || StorageButton.IsEnabled || !ScanRing.IsActive)
            throw new InvalidOperationException("Disk selection and cleanup launch must be disabled during a scan.");
        Deactivate();
        await running;
        if (!DrivePicker.IsEnabled || ScanRing.IsActive || ResultsList.ItemsSource is not null || !ScanStatus.Text.Contains("取消"))
            throw new InvalidOperationException("Leaving the tool must cancel its scan and discard results.");
        await ScanAsync((_, _, _) => Task.FromException<CleanupReport>(new IOException("Isolated diagnostic read failure")));
        if (!OperationStatus.IsOpen || OperationStatus.Severity != InfoBarSeverity.Error || !ScanButton.IsEnabled)
            throw new InvalidOperationException("Read errors must remain visible and allow retry.");
        await ScanAsync((_, _, _) => Task.FromResult(fixture));
        if (OperationStatus.IsOpen || ResultsList.ItemsSource is null || !CleanupButton.IsEnabled)
            throw new InvalidOperationException("Read-only retry must clear stale errors and render its results.");
    }
}

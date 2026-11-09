using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Toolbox.App.Services;
using Toolbox.Core;
using Toolbox.Platform;

namespace Toolbox.App.Views;

public sealed partial class WindowCloseView : UserControl
{
    private Window? _owner;
    private WindowPickerWindow? _picker;
    private CancellationTokenSource? _cancellation;
    private ContentDialog? _dialog;
    private DesktopWindow? _selected;
    public bool IsBusy { get; private set; }
    public event Action<bool>? BusyChanged;
    public WindowCloseView() => InitializeComponent();
    internal void Initialize(Window owner) => _owner = owner;

    internal void Cancel()
    {
        _cancellation?.Cancel(); _picker?.Cancel(); _dialog?.Hide();
    }
    internal void Deactivate() { if (!IsBusy) ClearSelection(); }

    private void ClearSelection()
    {
        _selected = null; WindowPreview.Source = null; SelectionCard.Visibility = Visibility.Collapsed;
        PickButton.Content = "选择窗口";
        ControlTray.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SetBusy(bool busy)
    {
        IsBusy = busy; PickButton.IsEnabled = CloseButton.IsEnabled = !busy;
        CancelButton.Visibility = BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Visibility = _selected is null ? Visibility.Collapsed : Visibility.Visible;
        ControlTray.Visibility = busy || _selected is not null ? Visibility.Visible : Visibility.Collapsed;
        ActionHint.Text = busy ? "正在选择或等待窗口响应" : "确认后请求正常关闭";
        BusyRing.IsActive = busy; BusyChanged?.Invoke(busy);
    }
    private void Status(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        OperationStatus.Title = title; OperationStatus.Message = message; OperationStatus.Severity = severity; OperationStatus.IsOpen = true;
    }

    private async void Pick_Click(object sender, RoutedEventArgs e) => await PickAsync();
    internal async Task PickAsync()
    {
        if (IsBusy || _owner is null) return;
        ClearSelection(); OperationStatus.IsOpen = false;
        _cancellation = new(); SetBusy(true);
        try
        {
            var token = _cancellation.Token;
            var toolbarDisplay = NativeMethods.GetDisplays().FirstOrDefault(display => display.Bounds.Contains(CursorPoint())) ?? NativeMethods.GetDisplays().First();
            _owner.AppWindow.Hide();
            await Task.Delay(250, token);
            var bounds = CaptureRect.Union(NativeMethods.GetDisplays().Select(display => display.Bounds));
            var windows = await Task.Run(WindowCloser.Enumerate, token);
            var frame = await Task.Run(() => NativeMethods.Capture(bounds), token);
            token.ThrowIfCancellationRequested();
            _picker = new(bounds, frame, toolbarDisplay.Bounds, windows);
            _picker.Activate();
            var selected = await _picker.Result;
            _picker = null;
            if (selected is null || token.IsCancellationRequested) { Status("已取消选择", "没有发送关闭请求。"); return; }
            WindowCloser.ValidateSelection(selected);
            var visible = new CaptureRect(selected.X, selected.Y, selected.Width, selected.Height).Intersect(bounds);
            ShowSelection(selected, visible.Width > 0 && visible.Height > 0 ? frame.Crop(visible.X - bounds.X, visible.Y - bounds.Y, visible.Width, visible.Height) : null);
        }
        catch (OperationCanceledException) { Status("已取消选择", "没有发送关闭请求。"); }
        catch (Exception error) { Status("无法选择窗口", error.Message, InfoBarSeverity.Error); }
        finally
        {
            _picker?.Cancel(); _picker = null;
            _owner.AppWindow.Show(); NativeMethods.ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(_owner), 9); _owner.Activate();
            _cancellation.Dispose(); _cancellation = null; SetBusy(false);
        }
    }

    private static PixelPoint CursorPoint() => NativeMethods.GetCursorPos(out var point) ? new(point.X, point.Y) : new(0, 0);
    internal void ShowSelection(DesktopWindow selected, PixelFrame? preview)
    {
        _selected = selected; SelectedTitle.Text = selected.Title; SelectedProcess.Text = selected.Label;
        WindowPreview.Source = preview is null ? null : ImageStore.ToBitmap(preview);
        SelectionCard.Visibility = Visibility.Visible;
        ControlTray.Visibility = CloseButton.Visibility = Visibility.Visible;
        PickButton.Content = "重新选择";
    }

    private async void Close_Click(object sender, RoutedEventArgs e) => await CloseSelectedAsync();
    internal async Task CloseSelectedAsync()
    {
        if (IsBusy || _selected is not { } selected) return;
        _cancellation = new(); SetBusy(true);
        try
        {
            _dialog = CreateConfirmation(selected);
            if (await _dialog.ShowAsync() != ContentDialogResult.Primary || _cancellation.IsCancellationRequested)
            { Status("已取消关闭", "没有发送关闭请求。"); return; }
            _dialog = null;
            var outcome = await Task.Run(() => WindowCloser.RequestCloseAsync(selected, _cancellation.Token));
            ShowOutcome(outcome); ClearSelection();
        }
        catch (OperationCanceledException) { Status("已取消关闭", "没有发送关闭请求。"); }
        catch (Exception error) { ClearSelection(); Status("未能关闭", error.Message, InfoBarSeverity.Error); }
        finally { _dialog = null; _cancellation.Dispose(); _cancellation = null; SetBusy(false); }
    }

    internal ContentDialog CreateConfirmation(DesktopWindow selected) => new()
    {
        XamlRoot = XamlRoot, Title = "关闭这个窗口？", PrimaryButtonText = "请求关闭", CloseButtonText = "取消",
        DefaultButton = ContentDialogButton.Close,
        Content = new TextBlock { Text = $"{selected.Title}\n{selected.Label}\n\n只发送正常关闭请求。若有未保存的工作，请在原程序中选择保存或取消。", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 480 }
    };

    internal void ShowOutcome(WindowCloseOutcome outcome)
    {
        switch (outcome)
        {
            case WindowCloseOutcome.Closed: Status("所选窗口已关闭", "只处理所选窗口，未结束进程。", InfoBarSeverity.Success); break;
            case WindowCloseOutcome.StillOpen: Status("已发送关闭请求，窗口仍在", "请检查原程序的保存提示。程序也可能拒绝关闭或没有响应；不会强制结束。", InfoBarSeverity.Warning); break;
            default: Status("已停止等待", "关闭请求已发送，无法撤回。请检查原程序的窗口和保存提示。", InfoBarSeverity.Warning); break;
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();
}

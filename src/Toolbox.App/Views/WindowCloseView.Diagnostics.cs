using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Toolbox.App.Views;

public sealed partial class WindowCloseView
{
    internal async Task VerifyCancelConfirmationAsync(Func<FrameworkElement, string, Task> render)
    {
        var trayPosition = ControlTray.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
        if (trayPosition.Y < 0 || trayPosition.Y + ControlTray.ActualHeight > ActualHeight + 1)
            throw new Exception("Sticky close controls are outside the viewport.");
        var running = CloseSelectedAsync();
        await Task.Delay(150);
        if (!IsBusy || PickButton.IsEnabled || CloseButton.IsEnabled || CancelButton.Visibility != Visibility.Visible ||
            _dialog is null || _dialog.DefaultButton != ContentDialogButton.Close)
            throw new Exception("Close confirmation/cancel controls are incorrect.");
        await render(_dialog, "window-close-confirmation.png");
        Cancel(); await running;
        if (IsBusy || !PickButton.IsEnabled || !CloseButton.IsEnabled || OperationStatus.Title != "已取消关闭")
            throw new Exception("Cancel confirmation did not restore controls without sending a request.");
    }

    internal void VerifyClearedSelection()
    {
        if (_selected is not null || WindowPreview.Source is not null || SelectionCard.Visibility != Visibility.Collapsed)
            throw new Exception("Navigating away retained a stale window selection or screenshot.");
    }
}

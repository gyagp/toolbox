using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Toolbox.App.Services;

namespace Toolbox.App.Views;

internal sealed class SessionWindow : Window
{
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
    private bool _finishing;
    internal SessionWindow(DisplayInfo display, string message, Action stop, CaptureRect? avoid = null)
    {
        Title = "工具箱 · 捕获中";
        _status.Text = message;
        var panel = new StackPanel { Padding = new Thickness(14), Spacing = 8 };
        panel.Children.Add(_status);
        var button = new Button { Content = "结束并保存  (Ctrl + Shift + F)", HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += (_, _) => { button.IsEnabled = false; stop(); };
        panel.Children.Add(button);
        Content = panel;
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        AppWindow.SetPresenter(presenter);
        AppWindow.Move(new(display.Bounds.X + Math.Max(0, display.Bounds.Width - 520), display.Bounds.Y + 32));
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = Math.Max(1, NativeMethods.GetDpiForWindow(handle) / 96.0);
        var width = (int)(370 * scale);
        var height = (int)(160 * scale);
        var x = display.Bounds.X + Math.Max(0, display.Bounds.Width - width - 24);
        var y = display.Bounds.Y + 32;
        if (avoid is { } region)
        {
            // Keep the stop button out of the selected scroll content whenever possible.
            var candidates = new[] {
                new CaptureRect(x, y, width, height),
                new CaptureRect(display.Bounds.X + 24, y, width, height),
                new CaptureRect(x, display.Bounds.Bottom - height - 24, width, height),
                new CaptureRect(display.Bounds.X + 24, display.Bounds.Bottom - height - 24, width, height)
            };
            var placement = candidates.OrderBy(r => { var overlap = r.Intersect(region); return (long)overlap.Width * overlap.Height; }).First();
            x = placement.X; y = placement.Y;
        }
        AppWindow.MoveAndResize(new(x, y, width, height));
        NativeMethods.SetWindowDisplayAffinity(handle, 0x11);
        AppWindow.Closing += (_, e) => { if (!_finishing) { e.Cancel = true; stop(); } };
    }
    internal void SetStatus(string text) => _status.Text = text;
    internal void Finish() { _finishing = true; Close(); }
}

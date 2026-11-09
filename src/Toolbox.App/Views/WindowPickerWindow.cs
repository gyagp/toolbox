using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Toolbox.App.Services;
using Toolbox.Core;
using Toolbox.Platform;
using Windows.Foundation;
using Windows.System;

namespace Toolbox.App.Views;

// A frozen desktop snapshot prevents the click from reaching any underlying application.
internal sealed class WindowPickerWindow : Window
{
    private readonly TaskCompletionSource<DesktopWindow?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Rectangle _outline = new() { StrokeThickness = 3, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock _title = new() { Text = "移动鼠标高亮窗口，单击选择", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 560 };
    private readonly TextBlock _details = new() { Text = "只选择，不会立即关闭 · Esc / 右键取消", FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
    private readonly Border _toolbar;
    private readonly CaptureRect _bounds;
    private readonly CaptureRect _toolbarDisplay;
    private readonly IReadOnlyList<DesktopWindow> _windows;
    private DesktopWindow? _pressedWindow;
    private bool _rightDown;
    internal DesktopWindow? Hovered { get; private set; }
    internal Task<DesktopWindow?> Result => _result.Task;

    internal WindowPickerWindow(CaptureRect bounds, PixelFrame frame, CaptureRect toolbarDisplay, IReadOnlyList<DesktopWindow> windows)
    {
        _bounds = bounds; _toolbarDisplay = toolbarDisplay; _windows = windows;
        Title = "Toolbox · 选择要关闭的窗口";
        var root = new Grid { RequestedTheme = ElementTheme.Dark };
        root.Children.Add(new Image { Source = ImageStore.ToBitmap(frame), Stretch = Stretch.Fill });
        root.Children.Add(new Rectangle { Fill = new SolidColorBrush(Colors.Black), Opacity = .28 });
        root.Children.Add(_canvas); _canvas.Children.Add(_outline);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(_title); panel.Children.Add(_details);
        var cancel = new Button { Content = "取消 · Esc" };
        cancel.Click += (_, _) => Cancel(); panel.Children.Add(cancel);
        _toolbar = new Border { Background = new SolidColorBrush(ColorHelper.FromArgb(250, 24, 41, 35)),
            CornerRadius = new(14), Padding = new(18, 14, 18, 14), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Child = panel };
        root.Children.Add(_toolbar); Content = root;
        root.SizeChanged += (_, _) => { PositionToolbar(); if (Hovered is { } window) DrawOutline(window); };
        _toolbar.SizeChanged += (_, _) => PositionToolbar();
        _canvas.PointerMoved += (_, e) => MoveSelection(ToDesktop(e.GetCurrentPoint(_canvas).Position));
        _canvas.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(_canvas);
            if (!point.Properties.IsRightButtonPressed && !point.Properties.IsLeftButtonPressed) return;
            BeginClick(ToDesktop(point.Position), point.Properties.IsRightButtonPressed);
            _canvas.CapturePointer(e.Pointer);
        };
        _canvas.PointerReleased += (_, e) =>
        {
            CompleteClick(ToDesktop(e.GetCurrentPoint(_canvas).Position), () => _canvas.ReleasePointerCapture(e.Pointer));
        };
        _canvas.PointerCanceled += (_, _) => { _pressedWindow = null; _rightDown = false; };
        _canvas.PointerCaptureLost += (_, _) => { _pressedWindow = null; _rightDown = false; };
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) => { e.Handled = true; Cancel(); }; root.KeyboardAccelerators.Add(escape);
        Closed += (_, _) => _result.TrySetResult(null);
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = false; presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter); AppWindow.MoveAndResize(new(bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }

    private PixelPoint ToDesktop(Point point) => new(_bounds.X + point.X * _bounds.Width / Math.Max(1, _canvas.ActualWidth),
        _bounds.Y + point.Y * _bounds.Height / Math.Max(1, _canvas.ActualHeight));

    internal void BeginClick(PixelPoint point, bool right = false)
    {
        MoveSelection(point); _rightDown = right; _pressedWindow = Hovered;
    }
    internal void CompleteClick(PixelPoint point, Action? releaseCapture = null)
    {
        MoveSelection(point);
        var cancel = _rightDown;
        var select = _pressedWindow is { CanClose: true } && _pressedWindow == Hovered;
        _pressedWindow = null; _rightDown = false;
        // Consume the entire click, including button-up, before removing the overlay.
        releaseCapture?.Invoke();
        if (cancel) Cancel();
        else if (select) SelectAt(point);
    }

    internal void MoveSelection(PixelPoint point)
    {
        Hovered = WindowCloser.HitTest(_windows, point.X, point.Y);
        if (Hovered is not { } window)
        {
            _outline.Visibility = Visibility.Collapsed;
            _title.Text = "此处没有可选择的窗口"; _details.Text = "移动鼠标到窗口内 · Esc / 右键取消"; return;
        }
        DrawOutline(window);
        _title.Text = string.IsNullOrWhiteSpace(window.Title) ? "不可选择的窗口" : window.Title;
        _details.Text = window.CanClose ? $"{window.Label} · 单击后确认，不会立即关闭" : window.BlockReason;
    }

    private void DrawOutline(DesktopWindow window)
    {
        var clipped = new CaptureRect(window.X, window.Y, window.Width, window.Height).Intersect(_bounds);
        var sx = _canvas.ActualWidth / _bounds.Width; var sy = _canvas.ActualHeight / _bounds.Height;
        Canvas.SetLeft(_outline, (clipped.X - _bounds.X) * sx); Canvas.SetTop(_outline, (clipped.Y - _bounds.Y) * sy);
        _outline.Width = Math.Max(0, clipped.Width * sx); _outline.Height = Math.Max(0, clipped.Height * sy);
        _outline.Stroke = new SolidColorBrush(window.CanClose ? ColorHelper.FromArgb(255, 90, 225, 195) : ColorHelper.FromArgb(255, 242, 183, 111));
        _outline.Visibility = Visibility.Visible;
    }

    internal void SelectAt(PixelPoint point)
    {
        MoveSelection(point);
        if (Hovered is not { CanClose: true } window) return;
        _result.TrySetResult(window); Close();
    }
    internal void Cancel() { _result.TrySetResult(null); Close(); }

    private void PositionToolbar()
    {
        if (_canvas.ActualWidth <= 0) return;
        var sx = _canvas.ActualWidth / _bounds.Width; var sy = _canvas.ActualHeight / _bounds.Height;
        var x = (_toolbarDisplay.X - _bounds.X + _toolbarDisplay.Width / 2d) * sx - _toolbar.ActualWidth / 2;
        var y = (_toolbarDisplay.Y - _bounds.Y + 20) * sy;
        _toolbar.Margin = new(Math.Clamp(x, 0, Math.Max(0, _canvas.ActualWidth - _toolbar.ActualWidth)), Math.Max(0, y), 0, 0);
    }
}

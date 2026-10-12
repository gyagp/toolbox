using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Toolbox.App.Services;
using Toolbox.Core;
using Windows.Foundation;
using Windows.System;

namespace Toolbox.App.Views;

internal enum ScreenshotMode { Rectangle, Window, FullScreen, Freeform }
internal sealed record Selection(CaptureRect Region, PixelFrame? Frame, ScreenshotMode Mode = ScreenshotMode.Rectangle, CaptureWindow? Window = null);

internal sealed class SelectionWindow : Window
{
    private readonly TaskCompletionSource<Selection?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Rectangle _outline = new() { Stroke = new SolidColorBrush(ColorHelper.FromArgb(255, 90, 225, 195)), StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Polyline _lasso = new() { Stroke = new SolidColorBrush(ColorHelper.FromArgb(255, 90, 225, 195)), StrokeThickness = 2, IsHitTestVisible = false };
    private readonly TextBlock _hint = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
    private readonly Border _toolbar;
    private readonly Dictionary<ScreenshotMode, ToggleButton> _buttons = [];
    private readonly List<PixelPoint> _points = [];
    private readonly CaptureRect _bounds;
    private readonly CaptureRect _toolbarDisplay;
    private readonly PixelFrame _frame;
    private readonly IReadOnlyList<CaptureWindow> _windows;
    private readonly bool _allowModes;
    private PixelPoint? _start;
    private CaptureWindow? _hoverWindow;
    internal ScreenshotMode Mode { get; private set; }
    internal Task<Selection?> Result => _result.Task;

    internal SelectionWindow(CaptureRect bounds, PixelFrame frame, DisplayInfo toolbarDisplay,
        IReadOnlyList<CaptureWindow>? windows = null, bool allowModes = false)
    {
        _bounds = bounds;
        _frame = frame;
        _toolbarDisplay = toolbarDisplay.Bounds;
        _windows = windows ?? [];
        _allowModes = allowModes;
        Title = allowModes ? "工具箱 · 选择截图模式" : "工具箱 · 选择自动滚动区域";
        var root = new Grid { RequestedTheme = ElementTheme.Dark };
        root.Children.Add(new Image { Source = ImageStore.ToBitmap(frame), Stretch = Stretch.Fill });
        root.Children.Add(new Rectangle { Fill = new SolidColorBrush(Colors.Black), Opacity = 0.28 });
        root.Children.Add(_canvas);
        _canvas.Children.Add(_outline);
        _canvas.Children.Add(_lasso);
        var panel = new StackPanel { Spacing = 8 };
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (allowModes)
        {
            foreach (var (mode, text) in new[] { (ScreenshotMode.Rectangle, "矩形 (1)"), (ScreenshotMode.Window, "窗口 (2)"), (ScreenshotMode.FullScreen, "全屏 (3)"), (ScreenshotMode.Freeform, "任意形状 (4)") })
            {
                var button = new ToggleButton { Content = text, MinHeight = 36 };
                button.Click += (_, _) => SetMode(mode);
                _buttons.Add(mode, button);
                commands.Children.Add(button);
            }
        }
        var cancel = new Button { Content = "取消 · Esc", MinHeight = 36 };
        cancel.Click += (_, _) => Cancel();
        commands.Children.Add(cancel);
        panel.Children.Add(commands);
        panel.Children.Add(_hint);
        _toolbar = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(250, 25, 31, 38)),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12, 16, 12),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Child = panel
        };
        root.Children.Add(_toolbar);
        Content = root;
        root.SizeChanged += (_, _) => PositionToolbar();
        _toolbar.SizeChanged += (_, _) => PositionToolbar();
        _canvas.PointerPressed += Pressed;
        _canvas.PointerMoved += Moved;
        _canvas.PointerReleased += Released;
        _canvas.PointerCanceled += (_, _) => Cancel();
        AddKey(root, VirtualKey.Escape, Cancel);
        if (allowModes)
        {
            AddKey(root, VirtualKey.Number1, () => SetMode(ScreenshotMode.Rectangle));
            AddKey(root, VirtualKey.Number2, () => SetMode(ScreenshotMode.Window));
            AddKey(root, VirtualKey.Number3, () => SetMode(ScreenshotMode.FullScreen));
            AddKey(root, VirtualKey.Number4, () => SetMode(ScreenshotMode.Freeform));
        }
        Closed += (_, _) => _result.TrySetResult(null);
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.MoveAndResize(new(_bounds.X, _bounds.Y, _bounds.Width, _bounds.Height));
        SetMode(ScreenshotMode.Rectangle);
    }

    private static void AddKey(UIElement element, VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key };
        accelerator.Invoked += (_, e) => { e.Handled = true; action(); };
        element.KeyboardAccelerators.Add(accelerator);
    }

    private void PositionToolbar()
    {
        if (_canvas.ActualWidth == 0) return;
        var scale = _canvas.ActualWidth / _frame.Width;
        var x = (_toolbarDisplay.X - _bounds.X + _toolbarDisplay.Width / 2.0) * scale - _toolbar.ActualWidth / 2;
        var y = (_toolbarDisplay.Y - _bounds.Y + 20) * (_canvas.ActualHeight / _frame.Height);
        _toolbar.Margin = new Thickness(Math.Max(0, x), Math.Max(0, y), 0, 0);
    }

    internal void SetMode(ScreenshotMode mode)
    {
        if (!_allowModes && mode != ScreenshotMode.Rectangle) return;
        Mode = mode;
        _start = null;
        _points.Clear();
        _lasso.Points.Clear();
        _outline.Visibility = Visibility.Collapsed;
        _hoverWindow = null;
        foreach (var pair in _buttons) pair.Value.IsChecked = pair.Key == mode;
        _hint.Text = mode switch
        {
            ScreenshotMode.Window => "移动鼠标高亮窗口，单击截取整个窗口 · Esc 取消",
            ScreenshotMode.Freeform => "按住鼠标描绘区域，松开完成 · 区域外透明 · Esc 取消",
            _ => _allowModes ? "拖动框选矩形，可跨显示器 · Esc 取消" : "框选可滚动内容，自动向下滚动；你只需停止 · 避开固定标题"
        };
        if (mode == ScreenshotMode.FullScreen) Complete(new(_bounds, _frame, mode));
    }

    internal void Cancel() { _result.TrySetResult(null); Close(); }
    private PixelPoint ToPixel(Point point) => new(
        Math.Clamp(point.X * _frame.Width / Math.Max(1, _canvas.ActualWidth), 0, _frame.Width),
        Math.Clamp(point.Y * _frame.Height / Math.Max(1, _canvas.ActualHeight), 0, _frame.Height));
    private Point ToLogical(PixelPoint point) => new(point.X * _canvas.ActualWidth / _frame.Width, point.Y * _canvas.ActualHeight / _frame.Height);

    // Shared by pointer handlers and the in-process integration diagnostic.
    internal void BeginSelection(PixelPoint point)
    {
        if (Mode == ScreenshotMode.Window)
        {
            MoveSelection(point);
            if (_hoverWindow is { } window) Complete(new(window.Bounds, null, Mode, window));
            return;
        }
        _start = point;
        if (Mode == ScreenshotMode.Freeform) { _points.Clear(); _lasso.Points.Clear(); AddLassoPoint(point); }
    }

    internal void MoveSelection(PixelPoint point)
    {
        if (Mode == ScreenshotMode.Window)
        {
            var desktopPoint = new PixelPoint(_bounds.X + point.X, _bounds.Y + point.Y);
            _hoverWindow = _windows.FirstOrDefault(w => w.Bounds.Contains(desktopPoint));
            if (_hoverWindow is { } window)
            {
                var visible = window.Bounds.Intersect(_bounds);
                ShowOutline(new(visible.X - _bounds.X, visible.Y - _bounds.Y, visible.Width, visible.Height));
                _hint.Text = $"单击截取：{window.Title}";
            }
            else { _outline.Visibility = Visibility.Collapsed; _hint.Text = "此处没有可捕获的窗口 · Esc 取消"; }
            return;
        }
        if (_start is not { } start) return;
        if (Mode == ScreenshotMode.Freeform) AddLassoPoint(point);
        else ShowOutline(CaptureGeometry.FromDrag(start, point, _frame.Width, _frame.Height));
    }

    private void AddLassoPoint(PixelPoint point)
    {
        if (_points.Count > 0 && Math.Abs(_points[^1].X - point.X) + Math.Abs(_points[^1].Y - point.Y) < 2) return;
        _points.Add(point);
        _lasso.Points.Add(ToLogical(point));
    }

    private void ShowOutline(CaptureRect rectangle)
    {
        var topLeft = ToLogical(new(rectangle.X, rectangle.Y));
        var bottomRight = ToLogical(new(rectangle.Right, rectangle.Bottom));
        Canvas.SetLeft(_outline, topLeft.X);
        Canvas.SetTop(_outline, topLeft.Y);
        _outline.Width = bottomRight.X - topLeft.X;
        _outline.Height = bottomRight.Y - topLeft.Y;
        _outline.Visibility = Visibility.Visible;
    }

    internal void EndSelection(PixelPoint point)
    {
        if (_start is not { } start) return;
        _start = null;
        if (Mode == ScreenshotMode.Freeform)
        {
            AddLassoPoint(point);
            var cut = CaptureGeometry.CutPolygon(_frame, _points);
            if (cut is { } polygon) Complete(new(ToDesktop(polygon.Bounds), polygon.Frame, Mode));
            else { _lasso.Points.Clear(); _hint.Text = "区域太小，请重新描绘。"; }
            return;
        }
        var rectangle = CaptureGeometry.FromDrag(start, point, _frame.Width, _frame.Height);
        if (rectangle.Width < 8 || rectangle.Height < 8) return;
        Complete(new(ToDesktop(rectangle), _frame.Crop(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height)));
    }

    private CaptureRect ToDesktop(CaptureRect rectangle) => rectangle with { X = _bounds.X + rectangle.X, Y = _bounds.Y + rectangle.Y };
    private void Complete(Selection selection) { _result.TrySetResult(selection); Close(); }
    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        var current = e.GetCurrentPoint(_canvas);
        if (!current.Properties.IsLeftButtonPressed) return;
        BeginSelection(ToPixel(current.Position));
        if (_start.HasValue) _canvas.CapturePointer(e.Pointer);
    }
    private void Moved(object sender, PointerRoutedEventArgs e) => MoveSelection(ToPixel(e.GetCurrentPoint(_canvas).Position));
    private void Released(object sender, PointerRoutedEventArgs e)
    {
        var point = ToPixel(e.GetCurrentPoint(_canvas).Position);
        _canvas.ReleasePointerCapture(e.Pointer);
        EndSelection(point);
    }
}

using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScreenRecorderLib;
using Toolbox.App.Services;
using Toolbox.App.Views;
using Toolbox.Core;
using Toolbox.App.Tools;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Toolbox.App;

public sealed partial class MainWindow : Window
{
    private enum CaptureMode { Idle, Selecting, Screenshot, Scrolling, Recording, Finalizing }
    private CaptureMode _mode;
    private readonly nint _handle;
    private readonly NativeMethods.SubclassProc _windowProc;
    private readonly List<int> _hotkeys = [];
    private RecordingSession? _recording;
    private CancellationTokenSource? _scrollCancellation;
    private SessionWindow? _sessionWindow;
    private SelectionWindow? _selectionWindow;
    private bool _paused, _recordingStarted, _closeWhenDone;
    private readonly CaptureLibrary _library;
    private readonly Dictionary<ToolId, ToggleButton> _navigation = [];
    private readonly Stopwatch _elapsed = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow(CaptureLibrary? library = null)
    {
        InitializeComponent();
        _library = library ?? new CaptureLibrary(ImageStore.ImageDirectory, ImageStore.VideoDirectory);
        LibraryPage.Initialize(_library);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Toolbox.ico"));
        var workArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new(Math.Min(1240, workArea.Width - 32), Math.Min(860, workArea.Height - 32)));
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        FileToolsPage.Initialize(_handle);
        ImageViewerPage.Initialize(_handle);
        RootGrid.SizeChanged += (_, e) => ShortcutHints.Visibility = e.NewSize.Height < 740 ? Visibility.Collapsed : Visibility.Visible;
        FileToolsPage.BusyChanged += _ => UpdateNavigationAvailability();
        BuildNavigation();
        HomePage.ToolSelected += async id => await NavigateAsync(id);
        _windowProc = WindowProc;
        NativeMethods.SetWindowSubclass(_handle, _windowProc, 1, 0);
        var failed = new List<string>();
        foreach (var (id, key, name) in new[] { (1, 0x53u, "S"), (2, 0x4Cu, "L"), (3, 0x52u, "R"), (4, 0x46u, "F") })
        {
            if (NativeMethods.RegisterHotKey(_handle, id, 0x4006, key)) _hotkeys.Add(id);
            else failed.Add($"Ctrl + Shift + {name}");
        }
        if (failed.Count > 0) ShortcutText.Text = $"快捷键被占用：{string.Join("、", failed)}。仍可使用界面按钮。";
        RefreshDisplays();
        _timer.Tick += (_, _) =>
        {
            if (_mode != CaptureMode.Recording) return;
            var text = $"{(_paused ? "已暂停" : "正在录屏")} · {_elapsed.Elapsed:hh\\:mm\\:ss}";
            StatusBar.Title = text;
            _sessionWindow?.SetStatus(text + "\n录制所选显示器 · MP4");
        };
        AppWindow.Closing += (_, e) =>
        {
            if (FileToolsPage.IsBusy)
            {
                e.Cancel = true;
                Status("文件工具正在处理", "请先取消查询或等待当前文件操作完成。");
                return;
            }
            if (_mode == CaptureMode.Idle) return;
            e.Cancel = true;
            _closeWhenDone = true;
            StopActive();
        };
        Closed += (_, _) =>
        {
            LibraryPage.Dispose();
            ImageViewerPage.Deactivate();
            _timer.Stop();
            foreach (var id in _hotkeys) NativeMethods.UnregisterHotKey(_handle, id);
            NativeMethods.RemoveWindowSubclass(_handle, _windowProc, 1);
        };
    }

    private nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x0312)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                switch ((int)wParam)
                {
                    case 1: _ = RunAsync(CaptureScreenshotAsync); break;
                    case 2: _ = RunAsync(CaptureScrollAsync); break;
                    case 3:
                        if (_mode == CaptureMode.Recording) StopActive();
                        else _ = RunAsync(RecordAsync);
                        break;
                    case 4: StopActive(); break;
                }
            });
            return 0;
        }
        return NativeMethods.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void RefreshDisplays()
    {
        var selected = (DisplayPicker.SelectedItem as DisplayInfo)?.DeviceName;
        var displays = NativeMethods.GetDisplays();
        DisplayPicker.ItemsSource = displays;
        DisplayPicker.SelectedItem = displays.Find(d => d.DeviceName == selected) ?? displays.FirstOrDefault();
    }
    private DisplayInfo SelectedDisplay => DisplayPicker.SelectedItem as DisplayInfo ?? throw new InvalidOperationException("未找到可捕获的显示器，请刷新。");

    private void SetMode(CaptureMode mode)
    {
        _mode = mode;
        var idle = mode == CaptureMode.Idle;
        ScreenshotButton.IsEnabled = ScrollButton.IsEnabled = RecordButton.IsEnabled = idle;
        DisplayPicker.IsEnabled = RefreshButton.IsEnabled = SystemAudioCheck.IsEnabled = MicrophoneCheck.IsEnabled = idle;
        SessionControls.Visibility = mode is CaptureMode.Recording or CaptureMode.Scrolling or CaptureMode.Finalizing ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Visibility = mode == CaptureMode.Recording ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = mode is CaptureMode.Recording or CaptureMode.Scrolling;
        UpdateNavigationAvailability();
    }
    private void Status(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        StatusBar.Title = title; StatusBar.Message = message; StatusBar.Severity = severity; StatusBar.IsOpen = true;
    }
    private void RestoreWindow()
    {
        AppWindow.Show();
        NativeMethods.ShowWindow(_handle, 9);
        Activate();
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_mode != CaptureMode.Idle || LibraryPage.IsBusy || FileToolsPage.IsBusy) return;
        LibraryPage.Deactivate();
        ImageViewerPage.Deactivate();
        SetMode(CaptureMode.Selecting);
        try { await action(); }
        catch (Exception error) { Status("捕获未完成", error.Message, InfoBarSeverity.Error); }
        finally
        {
            _timer.Stop();
            _sessionWindow?.Finish(); _sessionWindow = null;
            _recording?.Dispose(); _recording = null;
            _scrollCancellation?.Dispose(); _scrollCancellation = null;
            SetMode(CaptureMode.Idle);
            RestoreWindow();
            if (_closeWhenDone) Close();
            else if (LibraryPage.Visibility == Visibility.Visible && !LibraryPage.IsActive) await LibraryPage.ActivateAsync();
        }
    }

    private async Task<Selection?> SelectAsync(DisplayInfo display, bool screenshotModes = false)
    {
        AppWindow.Hide();
        await Task.Delay(250);
        var bounds = screenshotModes ? CaptureRect.Union(NativeMethods.GetDisplays().Select(d => d.Bounds)) : display.Bounds;
        var frame = await Task.Run(() => NativeMethods.Capture(bounds));
        var windows = screenshotModes ? NativeMethods.GetCaptureWindows(_handle) : null;
        _selectionWindow = new(bounds, frame, display, windows, screenshotModes);
        _selectionWindow.Activate();
        var selection = await _selectionWindow.Result;
        _selectionWindow = null;
        if (selection is null) Status("已取消", "没有保存文件。");
        return selection;
    }
    private async Task CaptureScreenshotAsync()
    {
        var selection = await SelectAsync(SelectedDisplay, screenshotModes: true);
        if (selection is null) return;
        SetMode(CaptureMode.Screenshot);
        var frame = selection.Window is { } window ? await WindowCapture.CaptureAsync(window.Handle) : selection.Frame!;
        var path = await ImageStore.SaveAsync(frame);
        await ShowLibraryAsync(path);
        var name = selection.Mode switch { ScreenshotMode.Window => "窗口", ScreenshotMode.FullScreen => "全屏", ScreenshotMode.Freeform => "任意形状", _ => "矩形" };
        Status($"{name}截图已保存", $"{frame.Width} × {frame.Height} 像素", InfoBarSeverity.Success);
    }

    private async Task CaptureScrollAsync()
    {
        await NavigateAsync(ToolId.ScrollCapture);
        var display = SelectedDisplay;
        var selection = await SelectAsync(display);
        if (selection is null) return;
        await Task.Delay(250); // let the selection overlay leave the desktop before hit testing
        using var driver = new AutoScrollDriver(selection.Region);
        SetMode(CaptureMode.Scrolling);
        _scrollCancellation = new();
        var token = _scrollCancellation.Token;
        var stitcher = new ScrollStitcher();
        _sessionWindow = new(display, "正在准备自动滚动…\n你只需点击停止。", StopActive, avoid: selection.Region);
        _sessionWindow.Activate();
        Status("自动滚动截图中", "正在自动滚动和拼接，点击停止或按 Ctrl + Shift + F 保存。请保持目标窗口和鼠标位置不变。");
        var reason = "已停止";
        var unchanged = 0;
        try
        {
            await driver.PrepareAsync(token);
            var baseline = await driver.ReadStableFrameAsync(token)
                ?? throw new InvalidOperationException("画面持续变化，请关闭动画或选择稳定的内容区域。");
            if (stitcher.Add(baseline).Status == StitchStatus.LimitReached)
                throw new InvalidOperationException("所选区域过大，请选择较小的区域。");
            while (!token.IsCancellationRequested)
            {
                driver.Step(token);
                var frame = await driver.ReadStableFrameAsync(token);
                if (frame is null) { reason = "画面未稳定，已停止"; break; }
                var result = await Task.Run(() => stitcher.Add(frame), token);
                unchanged = result.Status == StitchStatus.Unchanged ? unchanged + 1 : 0;
                _sessionWindow.SetStatus($"自动滚动中 · 长图高度 {result.TotalHeight:N0} px\n点击停止即可保存。");
                if (unchanged >= 3) { reason = "已到达底部或页面无法继续滚动"; break; }
                if (result.Status == StitchStatus.NoMatch) { reason = "无法可靠拼接，已停止；请检查固定元素或页面跳动"; break; }
                if (result.Status == StitchStatus.LimitReached) { reason = "已达到长图大小上限"; break; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (stitcher.Height > 0) { reason = error.Message; }
        if (stitcher.Height == 0) { Status("已取消", "没有保存文件。"); return; }
        SetMode(CaptureMode.Finalizing);
        var image = await Task.Run(stitcher.Build);
        var path = await ImageStore.SaveAsync(image);
        await ShowLibraryAsync(path);
        Status("长图已保存", $"{image.Width} × {image.Height} 像素。{reason}。请检查拼接结果。", InfoBarSeverity.Success);
    }

    private async Task RecordAsync()
    {
        await NavigateAsync(ToolId.Recording);
        var display = SelectedDisplay;
        var systemAudio = SystemAudioCheck.IsChecked == true;
        var microphone = MicrophoneCheck.IsChecked == true;
        _paused = _recordingStarted = false;
        PauseButton.Content = "暂停录制";
        PauseButton.IsEnabled = false;
        _elapsed.Reset();
        _recording = new();
        _recording.StatusChanged += status => DispatcherQueue.TryEnqueue(() =>
        {
            if (_mode != CaptureMode.Recording) return;
            if (status == RecorderStatus.Recording) { _recordingStarted = true; _elapsed.Start(); PauseButton.IsEnabled = true; }
            else if (status == RecorderStatus.Paused) _elapsed.Stop();
        });
        NativeMethods.ShowWindow(_handle, 6);
        await Task.Delay(300);
        SetMode(CaptureMode.Recording);
        Status("正在启动录屏", "点击浮动条中的“结束并保存”或按 Ctrl + Shift + F 结束。可回到主窗口暂停。", InfoBarSeverity.Warning);
        _recording.Start(display, systemAudio, microphone);
        _sessionWindow = new(display, "正在启动录屏…", StopActive);
        _sessionWindow.Activate();
        _timer.Start();
        var path = await _recording.Completion;
        _elapsed.Stop();
        await ShowLibraryAsync(path);
        Status("录屏已保存", $"MP4 · {_elapsed.Elapsed:hh\\:mm\\:ss}", InfoBarSeverity.Success);
    }

    private void StopActive()
    {
        _selectionWindow?.Close();
        if (_mode == CaptureMode.Scrolling) _scrollCancellation?.Cancel();
        if (_mode == CaptureMode.Recording)
        {
            SetMode(CaptureMode.Finalizing);
            _elapsed.Stop();
            Status("正在保存录屏", "正在完成 MP4 文件，请稍候。");
            _sessionWindow?.SetStatus("正在完成 MP4 文件，请稍候…");
            _recording?.Stop();
        }
    }
    private Task ShowLibraryAsync(string? path = null) => NavigateAsync(ToolId.Library, path);

    private void BuildNavigation()
    {
        void Add(ToolId id, string name, string glyph)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 17 });
            content.Children.Add(new TextBlock { Text = name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var button = new ToggleButton { Content = content, Style = (Style)Application.Current.Resources["SidebarButtonStyle"], IsChecked = id == ToolId.Overview };
            button.Click += async (_, _) => await NavigateAsync(id);
            _navigation.Add(id, button); ToolNavigation.Children.Add(button);
        }
        Add(ToolId.Overview, "工具总览", "\uE80F");
        foreach (var category in ToolCatalog.All.GroupBy(tool => tool.Category))
        {
            ToolNavigation.Children.Add(new TextBlock { Text = category.Key, FontSize = 11, Margin = new(16, 12, 0, 6),
                Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 132, 158, 145)) });
            foreach (var tool in category) Add(tool.Id, tool.Title, tool.Glyph);
        }
    }

    private void UpdateNavigationAvailability()
    {
        foreach (var button in _navigation.Values) button.IsEnabled = _mode == CaptureMode.Idle && !FileToolsPage.IsBusy;
    }

    private async Task NavigateAsync(ToolId id, string? path = null)
    {
        if (LibraryPage.IsBusy || FileToolsPage.IsBusy) return;
        LibraryPage.Deactivate();
        if (id != ToolId.ImageViewer) ImageViewerPage.Deactivate();
        foreach (var (key, button) in _navigation) button.IsChecked = key == id;
        HomePage.Visibility = id == ToolId.Overview ? Visibility.Visible : Visibility.Collapsed;
        LibraryPage.Visibility = id == ToolId.Library ? Visibility.Visible : Visibility.Collapsed;
        FileToolsPage.Visibility = id == ToolId.FileManager ? Visibility.Visible : Visibility.Collapsed;
        ImageViewerPage.Visibility = id == ToolId.ImageViewer ? Visibility.Visible : Visibility.Collapsed;
        var capture = id is ToolId.Screenshot or ToolId.ScrollCapture or ToolId.Recording;
        CapturePage.Visibility = capture ? Visibility.Visible : Visibility.Collapsed;
        if (capture)
        {
            var tool = ToolCatalog.All.Single(tool => tool.Id == id);
            CaptureTitle.Text = tool.Title; CaptureDescription.Text = tool.Description;
            ScreenshotTool.Visibility = id == ToolId.Screenshot ? Visibility.Visible : Visibility.Collapsed;
            ScrollTool.Visibility = id == ToolId.ScrollCapture ? Visibility.Visible : Visibility.Collapsed;
            RecordingTool.Visibility = id == ToolId.Recording ? Visibility.Visible : Visibility.Collapsed;
            CaptureSettings.Visibility = id == ToolId.Screenshot ? Visibility.Collapsed : Visibility.Visible;
            AudioSettings.Visibility = id == ToolId.Recording ? Visibility.Visible : Visibility.Collapsed;
        }
        if (id == ToolId.Library) await LibraryPage.ActivateAsync(path);
        if (id == ToolId.ImageViewer) await ImageViewerPage.ActivateAsync();
    }
    private void SafeAction(Action action)
    {
        try { action(); } catch (Exception error) { Status("操作未完成", error.Message, InfoBarSeverity.Error); }
    }
    private async void Screenshot_Click(object sender, RoutedEventArgs e) => await RunAsync(CaptureScreenshotAsync);
    private async void Scroll_Click(object sender, RoutedEventArgs e) => await RunAsync(CaptureScrollAsync);
    private async void Record_Click(object sender, RoutedEventArgs e) => await RunAsync(RecordAsync);
    private void Stop_Click(object sender, RoutedEventArgs e) => StopActive();
    private void Refresh_Click(object sender, RoutedEventArgs e) => SafeAction(RefreshDisplays);
    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != CaptureMode.Recording || !_recordingStarted) return;
        SafeAction(() =>
        {
            if (_paused) _recording?.Resume(); else _recording?.Pause();
            _paused = !_paused;
            PauseButton.Content = _paused ? "继续录制" : "暂停录制";
        });
    }
}

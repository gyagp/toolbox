using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using ScreenRecorderLib;
using Toolbox.App.Services;
using Toolbox.App.Views;
using Toolbox.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Toolbox.App;

public sealed partial class MainWindow
{
    internal async Task RunLibraryTestAsync(string output)
    {
        // Render only our own visual tree; this does not require desktop focus and
        // cannot accidentally include another app when the user switches windows.
        await Task.Delay(600);
        await RenderUiAsync(output, "ui.png");
        foreach (var (tool, name) in new[] { (Tools.ToolId.Screenshot, "screenshot-tool.png"),
            (Tools.ToolId.ScrollCapture, "scroll-tool.png"), (Tools.ToolId.Recording, "recording-tool.png"),
            (Tools.ToolId.FileManager, "file-tool.png") })
        {
            await NavigateAsync(tool);
            await Task.Delay(200);
            await RenderUiAsync(output, name);
        }
        var lockedPreview = Path.Combine(output, "locked-preview.txt");
        File.WriteAllText(lockedPreview, "Isolated UI fixture; no user file is used.");
        using (var held = new FileStream(lockedPreview, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await FileToolsPage.RenderFixtureAsync(lockedPreview);
            await Task.Delay(200);
            await RenderUiAsync(output, "file-locks.png");
        }
        await NavigateAsync(Tools.ToolId.Overview);
        var originalSize = AppWindow.Size;
        AppWindow.Resize(new(980, 760));
        await Task.Delay(250);
        await RenderUiAsync(output, "overview-narrow.png");
        AppWindow.Resize(originalSize);
        await Task.Delay(250);
        var file = await StorageFile.GetFileFromPathAsync(Path.Combine(output, "ui.png"));
        var composition = new Windows.Media.Editing.MediaComposition();
        composition.Clips.Add(await Windows.Media.Editing.MediaClip.CreateFromImageFileAsync(file, TimeSpan.FromSeconds(3)));
        var folder = await StorageFolder.GetFolderFromPathAsync(output);
        var movie = await folder.CreateFileAsync("library-fixture.mp4", CreationCollisionOption.ReplaceExisting);
        var render = await composition.RenderToFileAsync(movie, Windows.Media.Editing.MediaTrimmingPreference.Precise,
            Windows.Media.MediaProperties.MediaEncodingProfile.CreateMp4(Windows.Media.MediaProperties.VideoEncodingQuality.Wvga));
        if (render != Windows.Media.Transcoding.TranscodeFailureReason.None) throw new Exception($"Video fixture rendering failed: {render}");
        await VerifyLibraryAsync(output, movie.Path);
    }

    private async Task RenderUiAsync(string output, string name)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync((FrameworkElement)Content);
        var data = (await bitmap.GetPixelsAsync()).ToArray();
        var frame = new PixelFrame(bitmap.PixelWidth, bitmap.PixelHeight, data);
        var path = await ImageStore.SaveAsync(frame, output);
        File.Move(path, Path.Combine(output, name), true);
    }

    /// <summary>Opt-in integration diagnostic. Never runs on ordinary launch.
    /// Captures only this test window's interior, disables both audio sources.</summary>
    internal async Task RunSmokeTestAsync(string output)
    {
        var originalContent = Content;
        var display = NativeMethods.GetDisplays().First();
        AppWindow.Move(new(display.Bounds.X + 30, display.Bounds.Y + 30));
        ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).IsAlwaysOnTop = true;
        AppWindow.Show();
        NativeMethods.ShowWindow(_handle, 9);
        Activate();
        NativeMethods.SetForegroundWindow(_handle);
        await Task.Delay(1200);
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        var ui = NativeMethods.Capture(new(pos.X, pos.Y, size.Width, size.Height));
        var uiPath = await ImageStore.SaveAsync(ui, output);
        File.Move(uiPath, Path.Combine(output, "ui.png"), true);
        await VerifyScreenshotModesAsync(output, display, ui);

        var lines = new StackPanel();
        var random = new Random(7412);
        for (var i = 0; i < 80; i++)
            lines.Children.Add(new Border
            {
                Height = 40,
                Background = new SolidColorBrush(ColorHelper.FromArgb(255, (byte)random.Next(45, 125), (byte)random.Next(75, 170), (byte)random.Next(90, 180))),
                Child = new TextBlock { Text = $"工具箱 · 渲染测试 {i:D3}   /   {random.Next():X8}", FontSize = 19, Foreground = new SolidColorBrush(Colors.White), Margin = new Thickness(16, 6, 0, 0) }
            });
        var scroll = new ScrollViewer
        {
            Width = 600, Height = 400, Content = lines,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden
        };
        Content = new Grid { Padding = new Thickness(40), Children = { scroll } };
        await Task.Delay(500);
        var scale = scroll.XamlRoot.RasterizationScale;
        var origin = new NativeMethods.Point();
        NativeMethods.ClientToScreen(_handle, ref origin);
        var region = new CaptureRect(origin.X + (int)(40 * scale), origin.Y + (int)(40 * scale), (int)(600 * scale), (int)(400 * scale));
        var stitcher = new ScrollStitcher();
        stitcher.Add(NativeMethods.Capture(region));
        scroll.ChangeView(null, 120, null, true);
        await Task.Delay(400);
        var result = stitcher.Add(NativeMethods.Capture(region));
        if (result.Status != StitchStatus.Added || Math.Abs(result.AddedRows - 120 * scale) > 1)
            throw new InvalidOperationException($"Rendered scroll did not match: {result}, scale={scale}, offset={scroll.VerticalOffset}");
        var image = stitcher.Build();
        var imagePath = await ImageStore.SaveAsync(image, output);
        var file = await StorageFile.GetFileFromPathAsync(imagePath);
        using (var stream = await file.OpenReadAsync())
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if (decoder.PixelWidth != image.Width || decoder.PixelHeight != image.Height) throw new Exception("PNG dimensions mismatch");
        }

        // Exercise real wheel input through the same driver used by automatic long capture.
        scroll.ChangeView(null, 0, null, true);
        Activate();
        NativeMethods.SetForegroundWindow(_handle);
        await Task.Delay(300);
        using (var automatic = new AutoScrollDriver(region))
        {
            await automatic.PrepareAsync(CancellationToken.None);
            var first = await automatic.ReadStableFrameAsync(CancellationToken.None) ?? throw new Exception("Auto scroll baseline not stable");
            var autoStitcher = new ScrollStitcher();
            autoStitcher.Add(first);
            for (var step = 0; step < 3; step++)
            {
                automatic.Step(CancellationToken.None);
                var next = await automatic.ReadStableFrameAsync(CancellationToken.None) ?? throw new Exception("Auto scroll frame not stable");
                if (autoStitcher.Add(next).Status != StitchStatus.Added) throw new Exception("Automatic wheel scroll did not stitch");
            }
            if (scroll.VerticalOffset <= 0 || autoStitcher.Height <= first.Height) throw new Exception("Automatic scrolling did not move content");
            await ImageStore.SaveAsync(autoStitcher.Build(), output);
            scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
            await Task.Delay(300);
            var end = await automatic.ReadStableFrameAsync(CancellationToken.None) ?? throw new Exception("End of page not stable");
            automatic.Step(CancellationToken.None);
            var afterEnd = await automatic.ReadStableFrameAsync(CancellationToken.None) ?? throw new Exception("End of page not stable after wheel");
            if (!end.IsSimilarTo(afterEnd)) throw new Exception("Bottom-of-page frame changed unexpectedly");
            using var stopped = new CancellationTokenSource();
            stopped.Cancel();
            var canceled = false;
            try { automatic.Step(stopped.Token); } catch (OperationCanceledException) { canceled = true; }
            if (!canceled) throw new Exception("Canceled auto-scroll accepted input");
            var focusWindow = new Window { Title = "工具箱 · 焦点变化测试", Content = new TextBlock { Text = "自动滚动必须停止" } };
            try
            {
                focusWindow.Activate();
                NativeMethods.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(focusWindow));
                await Task.Delay(100);
                var rejected = false;
                try { automatic.Step(CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new Exception("Auto scroll injected input after focus changed");
            }
            finally { focusWindow.Close(); Activate(); NativeMethods.SetForegroundWindow(_handle); }
        }
        using var recording = new RecordingSession();
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hasStarted = false;
        recording.StatusChanged += status =>
        {
            if (status == RecorderStatus.Recording) { if (hasStarted) resumed.TrySetResult(); else { hasStarted = true; began.TrySetResult(); } }
            if (status == RecorderStatus.Paused) paused.TrySetResult();
        };
        var crop = new CaptureRect(region.X - display.Bounds.X, region.Y - display.Bounds.Y, region.Width, region.Height);
        recording.Start(display, false, false, output, crop);
        if (await Task.WhenAny(began.Task, recording.Completion, Task.Delay(15000)) != began.Task)
        {
            if (recording.Completion.IsCompleted) await recording.Completion;
            throw new TimeoutException("Recorder did not start");
        }
        await Task.Delay(1200);
        recording.Pause();
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        recording.Resume();
        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        scroll.ChangeView(null, 220, null, true);
        await Task.Delay(1300);
        recording.Stop();
        var videoPath = await recording.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        var video = await StorageFile.GetFileFromPathAsync(videoPath);
        var properties = await video.Properties.GetVideoPropertiesAsync();
        if (properties.Duration.TotalSeconds < 1 || properties.Width == 0 || properties.Height == 0)
            throw new Exception("MP4 metadata invalid");
        var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(video);
        var composition = new Windows.Media.Editing.MediaComposition();
        composition.Clips.Add(clip);
        using var thumbnail = await composition.GetThumbnailAsync(TimeSpan.FromMilliseconds(500), 160, 100, Windows.Media.Editing.VideoFramePrecision.NearestFrame);
        var thumbnailDecoder = await BitmapDecoder.CreateAsync(thumbnail);
        if (thumbnailDecoder.PixelWidth == 0) throw new Exception("MP4 frame decode failed");
        File.WriteAllText(Path.Combine(output, "video-metadata.txt"), $"{properties.Width}x{properties.Height}, {properties.Duration}, {properties.Bitrate} bps\n");
        Content = originalContent;
        await VerifyLibraryAsync(output, videoPath);
    }

    private async Task VerifyLibraryAsync(string output, string videoPath)
    {
        var images = _library.Roots[0];
        var videos = _library.Roots[1];
        Directory.CreateDirectory(images); Directory.CreateDirectory(videos);
        var imagePath = Path.Combine(images, "工作记录.png");
        var moviePath = Path.Combine(videos, "操作演示.mp4");
        File.Copy(Path.Combine(output, "ui.png"), imagePath, true);
        File.Copy(videoPath, moviePath, true);
        File.WriteAllText(Path.Combine(videos, "still.partial.mp4"), "incomplete");
        await ShowLibraryAsync(imagePath);
        await LibraryPage.LoadPreviewAsync(LibraryPage.SelectedEntry);
        if (LibraryPage.VisibleCount != 2 || !LibraryPage.HasImagePreview) throw new Exception("Library did not discover and preview existing captures");
        await Task.Delay(400);
        await RenderUiAsync(output, "library.png");

        LibraryPage.SetQuery(LibraryFilter.Videos, "演示");
        await LibraryPage.LoadPreviewAsync(LibraryPage.SelectedEntry);
        for (var attempt = 0; attempt < 30 && LibraryPage.PreviewPlayer is null; attempt++) await Task.Delay(100);
        if (LibraryPage.VisibleCount != 1 || LibraryPage.PreviewPlayer is null)
            throw new Exception($"Video library filter/player not initialized: rows={LibraryPage.VisibleCount}, selection={LibraryPage.SelectedEntry?.Name}, status={LibraryPage.DiagnosticStatus}");
        var player = LibraryPage.PreviewPlayer;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaOpened += (_, _) => opened.TrySetResult();
        if (player.PlaybackSession.NaturalDuration > TimeSpan.Zero) opened.TrySetResult();
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (player.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing) throw new Exception("Library video autoplayed");
        player.Play();
        await Task.Delay(400);
        if (player.PlaybackSession.Position <= TimeSpan.Zero) throw new Exception("Embedded video did not play");
        LibraryPage.Deactivate();
        var renamed = _library.Rename(moviePath, "录屏重命名"); // verifies the player released its file handle
        await ShowLibraryAsync(renamed);
        if (LibraryPage.SelectedEntry?.FullPath != renamed) throw new Exception("Library did not select renamed video");
        LibraryPage.Deactivate();
        var deleted = _library.MoveToDeleted(renamed);
        await LibraryPage.ActivateAsync();
        LibraryPage.SetQuery(LibraryFilter.Deleted, "");
        if (LibraryPage.VisibleCount != 1 || LibraryPage.SelectedEntry?.FullPath != deleted) throw new Exception("Recently deleted item not shown");
        LibraryPage.Deactivate();
        var restored = _library.Restore(deleted);
        await ShowLibraryAsync(restored);
        if (LibraryPage.SelectedEntry?.FullPath != restored) throw new Exception("Restored video not selected");
        LibraryPage.Deactivate();
        await ShowLibraryAsync(imagePath);
        var added = Path.Combine(images, "外部新增.png");
        File.Copy(imagePath, added, true);
        for (var attempt = 0; attempt < 15 && LibraryPage.VisibleCount != 3; attempt++) await Task.Delay(200);
        if (LibraryPage.VisibleCount != 3) throw new Exception("Directory watcher did not refresh new content");
        LibraryPage.SetQuery(LibraryFilter.All, "不存在的内容");
        if (LibraryPage.VisibleCount != 0 || LibraryPage.SelectedEntry is not null) throw new Exception("Empty search retained a stale selection");
        await ShowLibraryAsync(imagePath);
        await LibraryPage.LoadPreviewAsync(LibraryPage.SelectedEntry);
    }

    private async Task VerifyScreenshotModesAsync(string output, DisplayInfo display, PixelFrame ui)
    {
        var bounds = new CaptureRect(AppWindow.Position.X, AppWindow.Position.Y, ui.Width, ui.Height);
        var toolbarDisplay = display with { Bounds = bounds };
        var windows = NativeMethods.GetCaptureWindows();
        if (!windows.Any(w => w.Handle == _handle)) throw new Exception("Native enumeration did not find test window");
        SelectionWindow Create() => new(bounds, ui, toolbarDisplay, windows, allowModes: true);

        var rectangle = Create();
        rectangle.Activate();
        await Task.Delay(150);
        rectangle.BeginSelection(new(330, 310));
        rectangle.MoveSelection(new(30, 110));
        await Task.Delay(150);
        var toolbarImage = await ImageStore.SaveAsync(NativeMethods.Capture(bounds), output);
        File.Move(toolbarImage, Path.Combine(output, "selection-toolbar.png"), true);
        rectangle.EndSelection(new(30, 110));
        var crop = await rectangle.Result;
        if (crop?.Region != new CaptureRect(bounds.X + 30, bounds.Y + 110, 300, 200) ||
            crop.Frame is null || !crop.Frame.Pixels.SequenceEqual(ui.Crop(30, 110, 300, 200).Pixels))
            throw new Exception("Rectangle mode did not return the exact selected pixels");

        var full = Create();
        full.SetMode(ScreenshotMode.FullScreen);
        var all = await full.Result;
        if (all?.Region != bounds || !ReferenceEquals(all.Frame, ui)) throw new Exception("Full screen mode did not return entire desktop frame");

        var lasso = Create();
        lasso.SetMode(ScreenshotMode.Freeform);
        lasso.BeginSelection(new(20, 110));
        lasso.MoveSelection(new(220, 110));
        lasso.EndSelection(new(20, 310));
        var free = await lasso.Result;
        if (free?.Frame is not { } cut || cut.Pixels[^1] != 0) throw new Exception("Freeform mode lost transparency");
        var freePath = await ImageStore.SaveAsync(cut, output);
        using (var stream = await (await StorageFile.GetFileFromPathAsync(freePath)).OpenReadAsync())
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            if (pixels.DetachPixelData()[^1] != 0) throw new Exception("PNG encoder discarded freeform alpha");
        }

        var canceled = Create();
        canceled.Cancel();
        if (await canceled.Result is not null) throw new Exception("Canceled screenshot produced output");

        var windowSelection = Create();
        windowSelection.SetMode(ScreenshotMode.Window);
        windowSelection.BeginSelection(new(300, 250));
        var selected = await windowSelection.Result;
        if (selected?.Window?.Handle != _handle) throw new Exception("Window hit test selected the wrong HWND");

        // A real overlapping window must not appear in an HWND screenshot.
        var blocker = new Window { Title = "工具箱 · 窗口截图遮挡测试", Content = new Grid { Background = new SolidColorBrush(Colors.Magenta) } };
        try
        {
            blocker.AppWindow.MoveAndResize(new(bounds.X + 100, bounds.Y + 160, 420, 280));
            ((Microsoft.UI.Windowing.OverlappedPresenter)blocker.AppWindow.Presenter).IsAlwaysOnTop = true;
            blocker.Activate();
            await Task.Delay(300);
            var windowImage = await WindowCapture.CaptureAsync(_handle);
            if (windowImage.Width < 100 || windowImage.Height < 100) throw new Exception("Window capture is empty");
            var magentaPixels = 0;
            for (var i = 0; i < windowImage.Pixels.Length; i += 4)
                if (windowImage.Pixels[i] > 245 && windowImage.Pixels[i + 1] < 10 && windowImage.Pixels[i + 2] > 245) magentaPixels++;
            if (magentaPixels > 100) throw new Exception("Window screenshot included the occluding window");
            await ImageStore.SaveAsync(windowImage, output);
            NativeMethods.ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(blocker), 6);
            if (NativeMethods.GetCaptureWindows().Any(w => w.Handle == WinRT.Interop.WindowNative.GetWindowHandle(blocker)))
                throw new Exception("Minimized windows must not be selectable");
        }
        finally { blocker.Close(); Activate(); NativeMethods.SetForegroundWindow(_handle); }
    }
}

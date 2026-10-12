using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Toolbox.App.Tools;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.System;

namespace Toolbox.App;

public sealed partial class MainWindow
{
    internal async Task RunImageTestAsync(string output, string fixtures)
    {
        await Task.Delay(400);
        await NavigateAsync(ToolId.ImageViewer);
        var passed = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message + ": " + ImageViewerPage.DiagnosticMessage);
            passed.Add(message);
        }
        foreach (var name in new[] { "01-sample.png", "02-sample.jpg", "03-sample.bmp", "05-sample.tiff", "06-sample.webp" })
        {
            await ImageViewerPage.OpenPathAsync(Path.Combine(fixtures, name));
            Check(ImageViewerPage.HasImage && !ImageViewerPage.IsAnimated, "Decode " + Path.GetExtension(name));
        }
        var animated = Path.Combine(fixtures, "04-animation.gif");
        await ImageViewerPage.OpenPathAsync(animated);
        await Task.Delay(200);
        Check(ImageViewerPage.IsAnimated && ImageViewerPage.IsPlaying, "GIF is recognized and plays automatically");
        Check(ImageViewerPage.HasKeyboardFocus, "An opened image receives focus for keyboard navigation and fullscreen");
        async Task<byte[]> PixelsAsync()
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(ImageViewerPage.DisplayedImage);
            return (await bitmap.GetPixelsAsync()).ToArray();
        }
        var pixels = await PixelsAsync();
        Check(pixels.Length > 0, "GIF has rendered pixels");
        var changed = false;
        for (var attempt = 0; attempt < 8 && !changed; attempt++)
        {
            await Task.Delay(90);
            var nextPixels = await PixelsAsync();
            changed = !pixels.SequenceEqual(nextPixels);
        }
        Check(changed, "GIF advances between visibly different frames");
        ImageViewerPage.ToggleAnimation();
        await Task.Delay(80);
        Check(!ImageViewerPage.IsPlaying, "GIF playback pauses");
        pixels = await PixelsAsync();
        await Task.Delay(250);
        var pausedPixels = await PixelsAsync();
        Check(pixels.SequenceEqual(pausedPixels), "Paused GIF pixels remain unchanged");
        ImageViewerPage.ToggleAnimation();
        Check(ImageViewerPage.IsPlaying, "GIF playback resumes");
        await RenderUiAsync(output, "image-viewer-gif.png");
        await ImageViewerPage.NavigateImageAsync(1);
        Check(ImageViewerPage.HasImage && ImageViewerPage.CurrentPath != animated, "Next image uses the current directory");
        await ImageViewerPage.NavigateImageAsync(-1);
        Check(ImageViewerPage.CurrentPath == animated && ImageViewerPage.IsAnimated, "Previous image returns to the GIF");
        await Task.WhenAll(ImageViewerPage.OpenPathAsync(Path.Combine(fixtures, "01-sample.png")), ImageViewerPage.OpenPathAsync(animated));
        Check(ImageViewerPage.CurrentPath == animated && ImageViewerPage.IsAnimated, "A stale load cannot replace a newer selection");
        var invalid = Path.Combine(output, "invalid.png");
        File.WriteAllText(invalid, "not an image");
        await ImageViewerPage.OpenPathAsync(invalid);
        Check(!ImageViewerPage.HasImage && ImageViewerPage.DiagnosticMessage.Length > 0, "Corrupt files display a recoverable error");
        await ImageViewerPage.OpenPathAsync(animated);
        await NavigateAsync(ToolId.FileManager);
        Check(!ImageViewerPage.IsPlaying && !ImageViewerPage.HasImage, "Leaving the viewer stops animation and releases the image");
        using (var exclusive = new FileStream(animated, FileMode.Open, FileAccess.Read, FileShare.None))
            Check(exclusive.Length > 0, "Leaving the viewer releases the source file handle");
        await NavigateAsync(ToolId.ImageViewer);
        Check(ImageViewerPage.CurrentPath == animated && ImageViewerPage.IsAnimated, "Returning to the viewer reloads the previous selection");
        await RenderUiAsync(output, "image-viewer.png");
        var normalSize = AppWindow.Size;
        var normalPosition = AppWindow.Position;
        await ImageViewerPage.HandleViewerKeyAsync(VirtualKey.F11);
        await Task.Delay(250);
        Check(ImageViewerPage.IsFullScreen && AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen && Sidebar.Visibility == Visibility.Collapsed,
            "F11 uses native fullscreen and hides the sidebar");
        Check(ImageViewerPage.IsAnimated && ImageViewerPage.IsPlaying, "GIF playback continues in fullscreen");
        await RenderUiAsync(output, "image-fullscreen.png");
        await ImageViewerPage.HandleViewerKeyAsync(VirtualKey.Right);
        Check(ImageViewerPage.CurrentPath != animated && ImageViewerPage.HasImage && ImageViewerPage.IsFullScreen, "Right arrow advances within fullscreen");
        await ImageViewerPage.HandleViewerKeyAsync(VirtualKey.Left);
        Check(ImageViewerPage.CurrentPath == animated && ImageViewerPage.IsAnimated, "Left arrow returns to the animated image");
        await ImageViewerPage.HandleViewerKeyAsync(VirtualKey.Escape);
        await Task.Delay(200);
        Check(!ImageViewerPage.IsFullScreen && AppWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped && Sidebar.Visibility == Visibility.Visible,
            "Escape restores the normal viewer and sidebar");
        Check(AppWindow.Size == normalSize && AppWindow.Position == normalPosition, "Leaving fullscreen restores the original window bounds");

        var series = Path.Combine(output, "相册 空格");
        Directory.CreateDirectory(series);
        var first = Path.Combine(series, "1 图片.png");
        var second = Path.Combine(series, "2 动画.gif");
        var broken = Path.Combine(series, "3 损坏.png");
        var last = Path.Combine(series, "10 图片.png");
        File.Copy(Path.Combine(fixtures, "01-sample.png"), first, true);
        File.Copy(animated, second, true);
        File.WriteAllText(broken, "corrupt image fixture");
        File.Copy(first, last, true);
        await OpenImageArgumentsAsync([second, "--fullscreen"]);
        Check(ImageViewerPage.CurrentPath == second && ImageViewerPage.IsFullScreen && Title.Contains("2 动画.gif"),
            "File activation opens a Unicode/spaced path directly in fullscreen");
        Check(ImageViewerPage.HasKeyboardFocus, "File activation places keyboard focus in the viewer");
        await ImageViewerPage.HandleViewerKeyAsync(VirtualKey.Home);
        Check(ImageViewerPage.CurrentPath == first, "Home selects the first image");
        await ImageViewerPage.HandleViewerKeyAsync(VirtualKey.End);
        Check(ImageViewerPage.CurrentPath == last, "End selects the numeric-order last image");
        await ImageViewerPage.HandleViewerKeyAsync(VirtualKey.Home);
        await ImageViewerPage.HandleViewerKeyAsync(VirtualKey.PageDown);
        Check(ImageViewerPage.CurrentPath == second && ImageViewerPage.IsAnimated, "PageDown follows the numeric image series");
        await ImageViewerPage.HandleViewerKeyAsync(VirtualKey.Right);
        Check(!ImageViewerPage.HasImage && ImageViewerPage.CurrentPath == broken && ImageViewerPage.IsFullScreen,
            "A corrupt series entry preserves its navigation position");
        await ImageViewerPage.HandleViewerKeyAsync(VirtualKey.Right);
        Check(ImageViewerPage.CurrentPath == last && ImageViewerPage.HasImage, "Navigation can advance beyond a corrupt image");
        await NavigateAsync(ToolId.FileManager);
        Check(!ImageViewerPage.IsFullScreen && AppWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped,
            "Changing tools exits fullscreen and restores the window");
        File.WriteAllLines(Path.Combine(output, "checks.txt"), passed);
    }
}

using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Toolbox.App.Tools;

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
        File.WriteAllLines(Path.Combine(output, "checks.txt"), passed);
    }
}

using System.Runtime.InteropServices.WindowsRuntime;
using Toolbox.Platform;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;

namespace Toolbox.App.Services;

internal static class PdfPreviewRenderer
{
    internal static async Task<byte[]> RenderAsync(PdfPagePreview preview, double pointsToPixels, CancellationToken token)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(preview.Data.AsBuffer()).AsTask(token); input.Seek(0);
        var document = await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(input).AsTask(token);
        using var page = document.GetPage(0);
        var scale = Math.Min(pointsToPixels, 4096 / Math.Max(preview.Width, preview.Height));
        using var output = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(output, new Windows.Data.Pdf.PdfPageRenderOptions
        {
            DestinationWidth = Math.Max(1, (uint)Math.Ceiling(preview.Width * scale)),
            DestinationHeight = Math.Max(1, (uint)Math.Ceiling(preview.Height * scale)),
            BackgroundColor = Microsoft.UI.Colors.White
        }).AsTask(token);
        output.Seek(0);
        if (preview.Masks is { Count: > 0 } masks)
        {
            // Paint AFTER Windows has rendered all page annotations. An annotation
            // may sit above page content; a PDF rectangle alone cannot safely hide it.
            var decoder = await BitmapDecoder.CreateAsync(output).AsTask(token);
            var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(token)).DetachPixelData();
            var width = (int)decoder.PixelWidth; var height = (int)decoder.PixelHeight;
            foreach (var mask in masks)
            {
                var left = Math.Clamp((int)Math.Floor(mask.X * width), 0, width);
                var top = Math.Clamp((int)Math.Floor(mask.Y * height), 0, height);
                var right = Math.Clamp((int)Math.Ceiling((mask.X + mask.Width) * width), 0, width);
                var bottom = Math.Clamp((int)Math.Ceiling((mask.Y + mask.Height) * height), 0, height);
                for (var y = top; y < bottom; y++)
                {
                    token.ThrowIfCancellationRequested();
                    for (var x = left; x < right; x++) { var offset = (y * width + x) * 4; pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 0; pixels[offset + 3] = 255; }
                }
            }
            using var clean = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, clean).AsTask(token);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)width, (uint)height, 96, 96, pixels);
            await encoder.FlushAsync().AsTask(token); clean.Seek(0);
            using var masked = new MemoryStream(); await clean.AsStreamForRead().CopyToAsync(masked, token); return masked.ToArray();
        }
        using var buffer = new MemoryStream();
        await output.AsStreamForRead().CopyToAsync(buffer, token);
        return buffer.ToArray();
    }
}

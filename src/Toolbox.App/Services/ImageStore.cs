using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Toolbox.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Toolbox.App.Services;

internal static class ImageStore
{
    // Keep Moment's on-disk locations so existing captures and .shike-trash entries
    // remain available without moving or duplicating the user's files.
    internal static string ImageDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Shike");
    internal static string VideoDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Shike");
    internal static string NewPath(string directory, string extension)
    {
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"Toolbox_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N")[..4]}.{extension}");
    }

    internal static async Task<string> SaveAsync(PixelFrame frame, string? directory = null)
    {
        var path = NewPath(directory ?? ImageDirectory, "png");
        var file = await StorageFile.GetFileFromPathAsync(CreateEmpty(path));
        try
        {
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                (uint)frame.Width, (uint)frame.Height, 96, 96, frame.Pixels);
            await encoder.FlushAsync();
        }
        catch { File.Delete(path); throw; }
        return path;
    }

    private static string CreateEmpty(string path) { using var stream = File.Create(path); return path; }

    internal static async Task CopyAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var package = new DataPackage();
        package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    internal static WriteableBitmap ToBitmap(PixelFrame frame)
    {
        var bitmap = new WriteableBitmap(frame.Width, frame.Height);
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(frame.Pixels);
        bitmap.Invalidate();
        return bitmap;
    }
}

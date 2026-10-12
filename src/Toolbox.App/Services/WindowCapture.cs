using ScreenRecorderLib;
using Toolbox.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Toolbox.App.Services;

internal static class WindowCapture
{
    // Capture the actual HWND through Windows Graphics Capture, including the title
    // bar. Never fall back to a desktop crop containing unrelated overlapping apps.
    internal static async Task<PixelFrame> CaptureAsync(nint window)
    {
        if (!NativeMethods.IsWindow(window) || !NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window))
            throw new InvalidOperationException("目标窗口已关闭、隐藏或最小化，请重新选择。");
        var options = RecorderOptions.Default;
        options.OutputOptions.RecorderMode = RecorderMode.Screenshot;
        options.SnapshotOptions.SnapshotFormat = ScreenRecorderLib.ImageFormat.PNG;
        options.SourceOptions.RecordingSources = [new WindowRecordingSource(window) { IsCursorCaptureEnabled = false }];
        options.MouseOptions.IsMousePointerEnabled = false;
        options.AudioOptions.IsAudioEnabled = false;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var path = Path.Combine(Path.GetTempPath(), $"Toolbox-window-{Guid.NewGuid():N}.png");
        var recorder = Recorder.CreateRecorder(options);
        recorder.OnRecordingComplete += (_, _) => completion.TrySetResult();
        recorder.OnRecordingFailed += (_, e) => completion.TrySetException(new InvalidOperationException($"窗口截图失败：{e.Error}"));
        try
        {
            recorder.Record(path);
            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { recorder.Stop(); throw new TimeoutException("窗口没有提供可捕获画面，请恢复窗口后重试。"); }
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if ((long)decoder.PixelWidth * decoder.PixelHeight * 4 > ScrollStitcher.MaxBytes)
                throw new InvalidOperationException("目标窗口尺寸过大，请缩小窗口后重试。");
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            return new((int)decoder.PixelWidth, (int)decoder.PixelHeight, data.DetachPixelData());
        }
        finally
        {
            recorder.Dispose(); // close the native file handle before deleting the temporary PNG
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}

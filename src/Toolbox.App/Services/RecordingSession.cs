using ScreenRecorderLib;

namespace Toolbox.App.Services;

internal sealed class RecordingSession : IDisposable
{
    private Recorder? _recorder;
    private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopping;
    internal Task<string> Completion => _completion.Task;
    internal event Action<RecorderStatus>? StatusChanged;

    internal void Start(DisplayInfo display, bool systemAudio, bool microphone, string? outputDirectory = null, CaptureRect? crop = null)
    {
        var options = RecorderOptions.Default;
        var source = new DisplayRecordingSource(display.DeviceName);
        if (crop is { } rect) source.SourceRect = new ScreenRect(rect.X, rect.Y, rect.Width, rect.Height);
        options.SourceOptions.RecordingSources = [source];
        options.VideoEncoderOptions.Framerate = 30;
        options.VideoEncoderOptions.Quality = 75;
        options.VideoEncoderOptions.IsHardwareEncodingEnabled = true;
        options.AudioOptions.IsAudioEnabled = systemAudio || microphone;
        if (systemAudio) options.AudioOptions.AudioSources.Add(LoopbackAudioSource.Default);
        if (microphone) options.AudioOptions.AudioSources.Add(CaptureAudioSource.Default);
        options.MouseOptions.IsMousePointerEnabled = true;
        var path = ImageStore.NewPath(outputDirectory ?? ImageStore.VideoDirectory, "mp4");
        var partialPath = Path.ChangeExtension(path, "partial.mp4");
        _recorder = Recorder.CreateRecorder(options);
        _recorder.OnRecordingComplete += (_, e) =>
        {
            try { File.Move(e.FilePath, path); _completion.TrySetResult(path); }
            catch (Exception error) { _completion.TrySetException(new IOException($"视频已编码，但重命名失败。文件位于 {e.FilePath}", error)); }
        };
        _recorder.OnRecordingFailed += (_, e) =>
        {
            try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch (IOException) { }
            _completion.TrySetException(new InvalidOperationException(e.Error));
        };
        _recorder.OnStatusChanged += (_, e) => StatusChanged?.Invoke(e.Status);
        _recorder.Record(partialPath);
    }

    internal void Pause() => _recorder?.Pause();
    internal void Resume() => _recorder?.Resume();
    internal void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        _recorder?.Stop();
    }
    public void Dispose() { _recorder?.Dispose(); _recorder = null; }
}

using Microsoft.UI.Dispatching;

namespace Toolbox.App.Services;

/// <summary>Keep the newest snapshot without queuing one UI task for every small file.</summary>
internal sealed class LatestUiProgress<T>(DispatcherQueue dispatcher, Action<T> apply) : IProgress<T> where T : class
{
    private T? _latest;
    private int _queued;

    public void Report(T value)
    {
        Interlocked.Exchange(ref _latest, value);
        if (Interlocked.Exchange(ref _queued, 1) != 0) return;
        if (!dispatcher.TryEnqueue(() =>
        {
            // A producer arriving after this reset may enqueue the next callback.
            // Read after reset so the final update is never stranded without a callback.
            Interlocked.Exchange(ref _queued, 0);
            if (Interlocked.Exchange(ref _latest, null) is { } latest) apply(latest);
        }))
        {
            Interlocked.Exchange(ref _queued, 0);
            Interlocked.Exchange(ref _latest, null);
        }
    }
}

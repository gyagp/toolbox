using Toolbox.Core;

namespace Toolbox.App.Services;

/// <summary>Routes real wheel input only to a stationary, foreground target.
/// Losing focus, moving the pointer out of the region or moving the window stops input.</summary>
internal sealed class AutoScrollDriver : IDisposable
{
    private readonly CaptureRect _region;
    private readonly CaptureWindow _target;
    private readonly NativeMethods.Point _originalCursor;
    private readonly NativeMethods.Point _anchor;

    internal AutoScrollDriver(CaptureRect region)
    {
        if (region.Width < 80 || region.Height < 160)
            throw new InvalidOperationException("自动滚动区域至少需要 80 × 160 像素，请框选较大的内容区域。");
        _region = region;
        _anchor = new() { X = region.X + region.Width / 2, Y = region.Y + region.Height / 2 };
        var handle = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(_anchor), 2);
        _target = NativeMethods.GetCaptureWindows().FirstOrDefault(w => w.Handle == handle)
            ?? throw new InvalidOperationException("此处没有可滚动的应用窗口，请重新选择窗口内的内容。");
        if (_target.Bounds.Intersect(region) != region)
            throw new InvalidOperationException("请只框选同一个窗口内部的滚动内容。");
        NativeMethods.GetCursorPos(out _originalCursor);
    }

    internal async Task PrepareAsync(CancellationToken cancellationToken)
    {
        NativeMethods.SetForegroundWindow(_target.Handle);
        NativeMethods.SetCursorPos(_anchor.X, _anchor.Y);
        await Task.Delay(250, cancellationToken);
        EnsureTarget();
    }

    private void EnsureTarget()
    {
        if (NativeMethods.GetForegroundWindow() != _target.Handle)
            throw new InvalidOperationException("焦点已切换，自动滚动已停止。");
        var current = NativeMethods.GetCaptureWindows().FirstOrDefault(w => w.Handle == _target.Handle);
        if (current is null || current.Bounds != _target.Bounds)
            throw new InvalidOperationException("目标窗口已移动、关闭或最小化，自动滚动已停止。");
        NativeMethods.GetCursorPos(out var cursor);
        if (!_region.Contains(new PixelPoint(cursor.X, cursor.Y)) ||
            NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(cursor), 2) != _target.Handle)
            throw new InvalidOperationException("鼠标已离开捕获内容，自动滚动已停止。");
    }

    internal void Step(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureTarget();
        NativeMethods.ScrollDown();
    }

    internal async Task<PixelFrame?> ReadStableFrameAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(350, cancellationToken);
        var previous = await Task.Run(() => NativeMethods.Capture(_region), cancellationToken);
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(180, cancellationToken);
            EnsureTarget();
            var next = await Task.Run(() => NativeMethods.Capture(_region), cancellationToken);
            if (previous.IsSimilarTo(next)) return next;
            previous = next;
        }
        return null;
    }

    public void Dispose()
    {
        if (NativeMethods.GetCursorPos(out var current) && current.X == _anchor.X && current.Y == _anchor.Y)
            NativeMethods.SetCursorPos(_originalCursor.X, _originalCursor.Y);
    }
}

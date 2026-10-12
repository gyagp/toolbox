namespace Toolbox.Core;

public enum StitchStatus { Added, Unchanged, NoMatch, LimitReached }
public readonly record struct StitchResult(StitchStatus Status, int AddedRows, int TotalHeight);

/// <summary>Matches vertical translation between overlapping, stationary viewports.
/// Ambiguous, non-overlapping and upward frames are deliberately rejected.</summary>
public sealed class ScrollStitcher
{
    private readonly List<PixelFrame> _strips = [];
    private PixelFrame? _previous;
    public int Height { get; private set; }
    public const long MaxBytes = 256L * 1024 * 1024;
    public const int MaxHeight = 30000;

    public StitchResult Add(PixelFrame frame)
    {
        if (_previous is null)
        {
            if ((long)frame.Width * frame.Height * 4 > MaxBytes || frame.Height > MaxHeight)
                return new(StitchStatus.LimitReached, 0, 0);
            _strips.Add(frame);
            _previous = frame;
            Height = frame.Height;
            return new(StitchStatus.Added, frame.Height, Height);
        }
        if (_previous.Width != frame.Width || _previous.Height != frame.Height)
            throw new ArgumentException("Capture dimensions changed.", nameof(frame));

        // Ignore a narrow border (including most scrollbar tracks). No fixed-header removal.
        if (Difference(_previous, frame, 0) < 0.65)
            return new(StitchStatus.Unchanged, 0, Height);

        var maxShift = frame.Height * 3 / 4;
        var scores = new double[maxShift + 1];
        var best = double.MaxValue;
        var shift = 0;
        for (var candidate = 2; candidate <= maxShift; candidate++)
        {
            var score = Difference(_previous, frame, candidate);
            scores[candidate] = score;
            if (score < best) { best = score; shift = candidate; }
        }
        if (shift == 0 || best > 5.0)
            return new(StitchStatus.NoMatch, 0, Height);

        // Repeating rows / empty pages do not contain enough evidence for safe stitching.
        for (var candidate = 2; candidate <= maxShift; candidate++)
            if (Math.Abs(candidate - shift) > 3 && scores[candidate] < best + 0.8)
                return new(StitchStatus.NoMatch, 0, Height);

        if (Height + shift > MaxHeight || (long)frame.Width * (Height + shift) * 4 > MaxBytes)
            return new(StitchStatus.LimitReached, 0, Height);
        _strips.Add(frame.Crop(0, frame.Height - shift, frame.Width, shift));
        Height += shift;
        _previous = frame;
        return new(StitchStatus.Added, shift, Height);
    }

    private static double Difference(PixelFrame previous, PixelFrame next, int shift)
    {
        long total = 0;
        var samples = 0;
        var border = Math.Min(24, previous.Width / 10);
        var xStep = Math.Max(1, (previous.Width - 2 * border) / 64);
        var overlap = previous.Height - shift;
        var yStep = Math.Max(1, overlap / 72);
        for (var y = 0; y < overlap; y += yStep)
            for (var x = border; x < previous.Width - border; x += xStep)
            {
                var a = (y + shift) * previous.Stride + x * 4;
                var b = y * next.Stride + x * 4;
                for (var c = 0; c < 3; c++) total += Math.Abs(previous.Pixels[a + c] - next.Pixels[b + c]);
                samples += 3;
            }
        return samples == 0 ? double.MaxValue : (double)total / samples;
    }

    public PixelFrame Build()
    {
        if (_previous is null) throw new InvalidOperationException("No frames captured.");
        var pixels = new byte[checked(_previous.Stride * Height)];
        var offset = 0;
        foreach (var strip in _strips)
        {
            Buffer.BlockCopy(strip.Pixels, 0, pixels, offset, strip.Pixels.Length);
            offset += strip.Pixels.Length;
        }
        return new(_previous.Width, Height, pixels);
    }
}

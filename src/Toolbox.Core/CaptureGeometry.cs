namespace Toolbox.Core;

public readonly record struct PixelPoint(double X, double Y);

/// <summary>Physical-pixel bounds; desktop coordinates may be negative.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);
    public int Bottom => checked(Y + Height);
    public bool Contains(PixelPoint point) => point.X >= X && point.X < Right && point.Y >= Y && point.Y < Bottom;

    public PixelRect Intersect(PixelRect other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        return new(x, y, Math.Max(0, Math.Min(Right, other.Right) - x), Math.Max(0, Math.Min(Bottom, other.Bottom) - y));
    }

    public static PixelRect Union(IEnumerable<PixelRect> rectangles)
    {
        var items = rectangles.Where(r => r.Width > 0 && r.Height > 0).ToArray();
        if (items.Length == 0) throw new ArgumentException("No screen bounds available.", nameof(rectangles));
        var x = items.Min(r => r.X);
        var y = items.Min(r => r.Y);
        return new(x, y, items.Max(r => r.Right) - x, items.Max(r => r.Bottom) - y);
    }
}

public static class CaptureGeometry
{
    public static PixelRect FromDrag(PixelPoint start, PixelPoint end, int width, int height)
    {
        var x = (int)Math.Clamp(Math.Floor(Math.Min(start.X, end.X)), 0, width);
        var y = (int)Math.Clamp(Math.Floor(Math.Min(start.Y, end.Y)), 0, height);
        var right = (int)Math.Clamp(Math.Ceiling(Math.Max(start.X, end.X)), 0, width);
        var bottom = (int)Math.Clamp(Math.Ceiling(Math.Max(start.Y, end.Y)), 0, height);
        return new(x, y, right - x, bottom - y);
    }

    /// <summary>Even-odd polygon fill, with transparent pixels outside the lasso.
    /// The returned bounds are relative to the source image.</summary>
    public static (PixelRect Bounds, PixelFrame Frame)? CutPolygon(PixelFrame source, IReadOnlyList<PixelPoint> points)
    {
        if (points.Count < 3) return null;
        var bounds = FromDrag(new(points.Min(p => p.X), points.Min(p => p.Y)),
            new(points.Max(p => p.X), points.Max(p => p.Y)), source.Width, source.Height);
        if (bounds.Width < 8 || bounds.Height < 8) return null;
        var pixels = new byte[checked(bounds.Width * bounds.Height * 4)];
        var intersections = new List<double>(points.Count);
        var filled = false;
        for (var row = 0; row < bounds.Height; row++)
        {
            var scanY = bounds.Y + row + 0.5;
            intersections.Clear();
            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                if ((a.Y <= scanY && b.Y > scanY) || (b.Y <= scanY && a.Y > scanY))
                    intersections.Add(a.X + (scanY - a.Y) * (b.X - a.X) / (b.Y - a.Y));
            }
            intersections.Sort();
            for (var i = 0; i + 1 < intersections.Count; i += 2)
            {
                var left = Math.Clamp((int)Math.Ceiling(intersections[i] - 0.5), bounds.X, bounds.Right);
                var right = Math.Clamp((int)Math.Ceiling(intersections[i + 1] - 0.5), bounds.X, bounds.Right);
                if (right <= left) continue;
                Buffer.BlockCopy(source.Pixels, (bounds.Y + row) * source.Stride + left * 4,
                    pixels, (row * bounds.Width + left - bounds.X) * 4, (right - left) * 4);
                filled = true;
            }
        }
        return filled ? (bounds, new(bounds.Width, bounds.Height, pixels)) : null;
    }
}

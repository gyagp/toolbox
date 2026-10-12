namespace Toolbox.Core;

/// <summary>A top-down, tightly packed BGRA32 image in physical pixels.</summary>
public sealed record PixelFrame
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public int Stride => checked(Width * 4);

    public PixelFrame(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels.Length != checked(width * height * 4))
            throw new ArgumentException("Pixel buffer does not match the image dimensions.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public PixelFrame Crop(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > Width || y + height > Height)
            throw new ArgumentOutOfRangeException(nameof(width));
        var pixels = new byte[checked(width * height * 4)];
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(Pixels, (y + row) * Stride + x * 4, pixels, row * width * 4, width * 4);
        return new(width, height, pixels);
    }

    public bool IsSimilarTo(PixelFrame other, double tolerance = 0.65)
    {
        if (Width != other.Width || Height != other.Height) return false;
        long difference = 0;
        var samples = 0;
        for (var y = 0; y < Height; y += Math.Max(1, Height / 100))
            for (var x = 0; x < Width; x += Math.Max(1, Width / 100))
            {
                var index = y * Stride + x * 4;
                for (var c = 0; c < 3; c++) difference += Math.Abs(Pixels[index + c] - other.Pixels[index + c]);
                samples += 3;
            }
        return (double)difference / samples <= tolerance;
    }
}

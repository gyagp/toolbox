namespace Toolbox.Core;

public static class ImageFileTypes
{
    public static IReadOnlyList<string> Extensions { get; } = Array.AsReadOnly(new[]
        { ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".jxr" });
    public static bool Supports(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

public sealed record ImageLaunchRequest(string Path, bool FullScreen)
{
    // Receives already-tokenized arguments; spaces and Unicode are part of the path.
    public static ImageLaunchRequest? Parse(IEnumerable<string> arguments)
    {
        string? path = null;
        var fullScreen = false;
        var explicitViewer = false;
        foreach (var argument in arguments)
        {
            if (argument == "--fullscreen") { fullScreen = true; continue; }
            if (argument == "--view") { explicitViewer = true; continue; }
            if (argument.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"未知启动参数：{argument}");
            if (path is not null) throw new ArgumentException("请指定一张图片；打开后可连续浏览同目录图片。");
            path = argument;
        }
        if (path is null)
        {
            if (fullScreen || explicitViewer) throw new ArgumentException("请指定要打开的图片路径。");
            return null;
        }
        if (!ImageFileTypes.Supports(path)) throw new ArgumentException("不支持此图片格式。支持 PNG、JPEG、GIF、BMP、TIFF、WebP 和 JXR。");
        return new(System.IO.Path.GetFullPath(path), fullScreen);
    }
}

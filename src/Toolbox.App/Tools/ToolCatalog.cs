namespace Toolbox.App.Tools;

internal enum ToolId { Overview, Screenshot, ScrollCapture, Recording, Library, FileManager, ImageViewer }
internal sealed record ToolDefinition(ToolId Id, string Title, string Description, string Glyph, string Category);

internal static class ToolCatalog
{
    // Shared by the overview and sidebar; add new tools here, then map their view in MainWindow.
    internal static readonly ToolDefinition[] All =
    [
        new(ToolId.Screenshot, "屏幕截图", "矩形、窗口、全屏与任意形状，保存或复制图片。", "\uE722", "屏幕与媒体"),
        new(ToolId.ScrollCapture, "长截图", "自动向下滚动并拼接长页面。实验功能。", "\uE8A5", "屏幕与媒体"),
        new(ToolId.Recording, "屏幕录制", "录制显示器，支持系统声音、麦克风与暂停。", "\uE714", "屏幕与媒体"),
        new(ToolId.ImageViewer, "图片查看器", "浏览常用图片格式，播放 GIF、缩放与连续看图。", "\uE91B", "屏幕与媒体"),
        new(ToolId.Library, "捕获内容库", "预览、搜索和整理截图与录屏，恢复最近删除。", "\uE8B7", "屏幕与媒体"),
        new(ToolId.FileManager, "文件处理", "移动、删除文件或文件夹，查找并处理占用进程。", "\uE8B7", "文件与存储")
    ];
}

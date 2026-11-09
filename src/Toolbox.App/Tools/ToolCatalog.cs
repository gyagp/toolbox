namespace Toolbox.App.Tools;

internal enum ToolId { Screenshot, ScrollCapture, Recording, Library, FileManager, ImageViewer, DiskCleanup, CaptureMenu, Pdf, WindowClose }
internal sealed record ToolDefinition(ToolId Id, string Title, string Description, string Glyph, string Category, ToolId? Parent = null);

internal static class ToolCatalog
{
    // Shared by the sidebar and capture tabs; map each tool's view in MainWindow.
    internal static readonly ToolDefinition[] All =
    [
        new(ToolId.CaptureMenu, "屏幕捕获", "截图、长截图、录屏与捕获内容库，通过页面顶部切换。", "\uE722", "屏幕与媒体"),
        new(ToolId.Screenshot, "屏幕截图", "矩形、窗口、全屏与任意形状，保存或复制图片。", "\uE722", "屏幕与媒体", ToolId.CaptureMenu),
        new(ToolId.ScrollCapture, "长截图", "自动向下滚动并拼接长页面。实验功能。", "\uE8A5", "屏幕与媒体", ToolId.CaptureMenu),
        new(ToolId.Recording, "屏幕录制", "录制显示器，支持系统声音、麦克风与暂停。", "\uE714", "屏幕与媒体", ToolId.CaptureMenu),
        new(ToolId.ImageViewer, "图片查看器", "浏览常用图片格式，播放 GIF、缩放与连续看图。", "\uE91B", "屏幕与媒体"),
        new(ToolId.Library, "捕获内容库", "预览、搜索和整理截图与录屏，恢复最近删除。", "\uE8B7", "屏幕与媒体", ToolId.CaptureMenu),
        new(ToolId.FileManager, "文件处理", "移动、删除文件或文件夹，查找并处理占用进程。", "\uE8B7", "文件与存储"),
        new(ToolId.DiskCleanup, "磁盘清理", "只读检查磁盘占用，交给 Windows 审查清理。", "\uEDA2", "文件与存储"),
        new(ToolId.Pdf, "PDF 工具", "预览、合并、拆分、整理页面，添加文字、批注与标记。", "\uEA90", "文档"),
        new(ToolId.WindowClose, "关闭窗口", "在屏幕上高亮选择窗口，确认后请求正常关闭。", "\uE8BB", "窗口与系统")
    ];
    internal static IEnumerable<ToolDefinition> TopLevel => All.Where(tool => tool.Parent is null);
    internal static ToolId NavigationId(ToolId id) => All.FirstOrDefault(tool => tool.Id == id)?.Parent ?? id;
}

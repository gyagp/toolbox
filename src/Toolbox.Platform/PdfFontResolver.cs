using PdfSharp.Fonts;

namespace Toolbox.Platform;

internal sealed class PdfFontResolver : IFontResolver
{
    private static readonly object Gate = new();
    private static bool _initialized;
    private static readonly Lazy<byte[]> Font = new(() =>
    {
        using var input = typeof(PdfFontResolver).Assembly.GetManifestResourceStream("Toolbox.PdfFont.ttf")
            ?? throw new InvalidOperationException("PDF 字体资源缺失，请重新解压完整发布包。");
        using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
    });
    internal static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            GlobalFontSettings.FontResolver = new PdfFontResolver(); _initialized = true;
        }
    }
    public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) => new("ToolboxSansSC", bold, italic);
    public byte[] GetFont(string faceName) => Font.Value;
}

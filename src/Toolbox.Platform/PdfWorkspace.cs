using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Toolbox.Core;

namespace Toolbox.Platform;

public enum PdfEditKind { Text, Comment, Highlight, Cover }
public sealed record PdfEdit(Guid Id, PdfEditKind Kind, double X, double Y, double Width, double Height,
    string Text, double FontSize, int ViewRotation);
public sealed record PdfPageItem(Guid Id, Guid SourceId, int SourcePage, string SourceName, int Rotation, IReadOnlyList<PdfEdit> Edits);
public sealed record PdfMask(double X, double Y, double Width, double Height);
public sealed record PdfPagePreview(byte[] Data, double Width, double Height, IReadOnlyList<PdfMask>? Masks = null);
public sealed record PdfRasterPage(byte[] Png, double Width, double Height);

/// <summary>In-memory page plan. Sources are immutable byte snapshots; exports never overwrite.</summary>
public sealed class PdfWorkspace
{
    public PdfWorkspace() => PdfFontResolver.Initialize();
    private sealed record Source(string Path, byte[] Bytes, int PageCount);
    private readonly Dictionary<Guid, Source> _sources = [];
    private readonly Stack<PdfPageItem[]> _undo = [];
    private readonly Stack<PdfPageItem[]> _redo = [];
    private PdfPageItem[] _pages = [];
    private PdfPageItem[] _saved = [];
    public IReadOnlyList<PdfPageItem> Pages => Array.AsReadOnly(_pages);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsDirty => !_pages.SequenceEqual(_saved);
    public bool RequiresRasterization => _pages.Any(page => page.Edits.Any(edit => edit.Kind == PdfEditKind.Cover));
    public string SuggestedName => _pages.Length == 0 ? "文档" : Path.GetFileNameWithoutExtension(_pages[0].SourceName);

    public void AddFiles(IEnumerable<string> paths, CancellationToken token = default)
    {
        var additions = new List<(Guid Id, Source Source)>();
        long total = _sources.Values.Sum(source => (long)source.Bytes.Length);
        foreach (var input in paths)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(input);
            if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择 PDF 文件。");
            using var inputFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = inputFile.Length;
            total += length;
            if (length > 128L * 1024 * 1024 || total > 256L * 1024 * 1024) throw new IOException("单个 PDF 上限 128 MiB，工作区合计上限 256 MiB。");
            var bytes = new byte[(int)length]; inputFile.ReadExactly(bytes);
            using var stream = new MemoryStream(bytes, writable: false);
            using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            if (!document.SecuritySettings.PermitModifyDocument || !document.SecuritySettings.PermitExtractContent)
                throw new InvalidOperationException("此 PDF 有编辑权限限制，请使用拥有编辑权限的原件。");
            if (document.Internals.Catalog.Elements.ContainsKey("/AcroForm") || document.Internals.Catalog.Elements.ContainsKey("/Perms"))
                throw new InvalidOperationException("当前版本不编辑带交互表单或数字签名的 PDF，以免破坏字段或签名。原文件未改变。");
            if (document.PageCount < 1 || document.PageCount + additions.Sum(item => item.Source.PageCount) + _pages.Length > 2000)
                throw new InvalidOperationException("PDF 不能为空，工作区最多支持 2000 页。");
            additions.Add((Guid.NewGuid(), new(path, bytes, document.PageCount)));
        }
        token.ThrowIfCancellationRequested();
        if (additions.Count == 0) return;
        var wasEmpty = _pages.Length == 0;
        var next = _pages.Concat(additions.SelectMany(item => Enumerable.Range(0, item.Source.PageCount)
            .Select(page => new PdfPageItem(Guid.NewGuid(), item.Id, page, Path.GetFileName(item.Source.Path), 0, [])))).ToArray();
        foreach (var item in additions) _sources.Add(item.Id, item.Source);
        Commit(next);
        if (wasEmpty && additions.Count == 1) _saved = _pages;
    }

    private void Commit(PdfPageItem[] pages)
    {
        if (pages.Length == 0) throw new InvalidOperationException("至少需要保留一页。");
        _undo.Push(_pages);
        if (_undo.Count > 30)
        {
            var recent = _undo.Take(30).Reverse().ToArray(); _undo.Clear();
            foreach (var snapshot in recent) _undo.Push(snapshot);
        }
        _redo.Clear(); _pages = pages;
    }
    public void MarkSaved() => _saved = _pages;
    public void Undo() { if (_undo.TryPop(out var pages)) { _redo.Push(_pages); _pages = pages; } }
    public void Redo() { if (_redo.TryPop(out var pages)) { _undo.Push(_pages); _pages = pages; } }
    public void RemovePages(IEnumerable<Guid> ids)
    {
        var selected = ids.ToHashSet();
        Commit(_pages.Where(page => !selected.Contains(page.Id)).ToArray());
    }
    public void MovePages(IEnumerable<Guid> ids, int direction)
    {
        var selected = ids.ToHashSet(); var pages = _pages.ToArray();
        if (direction < 0)
        {
            for (var index = 1; index < pages.Length; index++)
                if (selected.Contains(pages[index].Id) && !selected.Contains(pages[index - 1].Id)) (pages[index - 1], pages[index]) = (pages[index], pages[index - 1]);
        }
        else for (var index = pages.Length - 2; index >= 0; index--)
            if (selected.Contains(pages[index].Id) && !selected.Contains(pages[index + 1].Id)) (pages[index], pages[index + 1]) = (pages[index + 1], pages[index]);
        if (!pages.SequenceEqual(_pages)) Commit(pages);
    }
    public void RotatePages(IEnumerable<Guid> ids)
    {
        var selected = ids.ToHashSet();
        Commit(_pages.Select(page => selected.Contains(page.Id) ? page with { Rotation = (page.Rotation + 90) % 360 } : page).ToArray());
    }
    public void AddEdit(Guid pageId, PdfEditKind kind, double x, double y, double width, double height, string text, double size)
    {
        if (kind is PdfEditKind.Highlight or PdfEditKind.Cover) { text = ""; size = 14; }
        if (!Enum.IsDefined(kind) || !double.IsFinite(x + y + width + height + size) || x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > 1.001 || y + height > 1.001)
            throw new ArgumentException("请在页面范围内框选编辑区域。");
        if (size < 6 || size > 144) throw new ArgumentException("字号应在 6–144 pt 之间。");
        if (kind is PdfEditKind.Text or PdfEditKind.Comment && string.IsNullOrWhiteSpace(text)) throw new ArgumentException("请先输入文字或批注。");
        if (text.Length > 4000) throw new ArgumentException("单条文字最多 4000 字符。");
        Commit(_pages.Select(page => page.Id == pageId ? page with { Edits = page.Edits.Append(new PdfEdit(Guid.NewGuid(), kind, x, y, width, height, text, size, page.Rotation)).ToArray() } : page).ToArray());
    }
    public void RemoveEdit(Guid pageId, Guid editId) => Commit(_pages.Select(page => page.Id == pageId
        ? page with { Edits = page.Edits.Where(edit => edit.Id != editId).ToArray() } : page).ToArray());
    public void UpdateEdit(Guid pageId, Guid editId, string text, double size)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4000 || !double.IsFinite(size) || size < 6 || size > 144)
            throw new ArgumentException("请提供有效文字和 6–144 pt 字号。");
        Commit(_pages.Select(page => page.Id == pageId ? page with { Edits = page.Edits.Select(edit => edit.Id == editId
            ? edit with { Text = text, FontSize = size } : edit).ToArray() } : page).ToArray());
    }

    public PdfPagePreview Preview(Guid pageId)
    {
        var item = _pages.Single(page => page.Id == pageId);
        using var output = CreateDocument([item], default);
        var size = DisplaySize(output.Pages[0]);
        using var buffer = new MemoryStream(); output.Save(buffer, closeStream: false);
        var masks = item.Edits.Where(edit => edit.Kind == PdfEditKind.Cover).Select(edit =>
        {
            // Edits remember the viewing orientation in which they were created.
            return ((item.Rotation - edit.ViewRotation + 360) % 360) switch
            {
                90 => new PdfMask(1 - edit.Y - edit.Height, edit.X, edit.Height, edit.Width),
                180 => new PdfMask(1 - edit.X - edit.Width, 1 - edit.Y - edit.Height, edit.Width, edit.Height),
                270 => new PdfMask(edit.Y, 1 - edit.X - edit.Width, edit.Height, edit.Width),
                _ => new PdfMask(edit.X, edit.Y, edit.Width, edit.Height)
            };
        }).ToArray();
        return new(buffer.ToArray(), size.Width, size.Height, masks);
    }

    public byte[] ExportVector(IReadOnlyList<PdfPageItem> pages, CancellationToken token = default)
    {
        if (pages.Any(page => page.Edits.Any(edit => edit.Kind == PdfEditKind.Cover)))
            throw new InvalidOperationException("含遮盖的文件必须使用安全图像化导出，不能保留原始内容流。");
        using var document = CreateDocument(pages, token);
        using var buffer = new MemoryStream(); document.Save(buffer, closeStream: false);
        token.ThrowIfCancellationRequested(); return buffer.ToArray();
    }

    private PdfDocument CreateDocument(IReadOnlyList<PdfPageItem> pages, CancellationToken token)
    {
        if (pages.Count == 0) throw new InvalidOperationException("至少需要一页。");
        var output = new PdfDocument();
        var opened = new Dictionary<Guid, (MemoryStream Stream, PdfDocument Document)>();
        try
        {
            output.Info.Creator = "Toolbox";
            foreach (var item in pages)
            {
                token.ThrowIfCancellationRequested();
                if (!opened.TryGetValue(item.SourceId, out var input))
                {
                    var stream = new MemoryStream(_sources[item.SourceId].Bytes, writable: false);
                    try { input = (stream, PdfReader.Open(stream, PdfDocumentOpenMode.Import)); }
                    catch { stream.Dispose(); throw; }
                    opened.Add(item.SourceId, input);
                }
                var page = output.AddPage(input.Document.Pages[item.SourcePage]);
                var originalRotation = ((page.Rotate % 360) + 360) % 360;
                page.Rotate = (originalRotation + item.Rotation) % 360;
                foreach (var edit in item.Edits) DrawEdit(page, edit, (originalRotation + edit.ViewRotation) % 360);
            }
            return output;
        }
        catch { output.Dispose(); throw; }
        finally { foreach (var source in opened.Values) { source.Document.Dispose(); source.Stream.Dispose(); } }
    }

    private static (double Width, double Height) DisplaySize(PdfPage page)
    {
        var box = page.EffectiveCropBoxReadOnly;
        var width = box.Width; var height = box.Height;
        if (width <= 0 || height <= 0 || !double.IsFinite(width + height)) throw new InvalidOperationException("无效的页面尺寸。");
        return page.Rotate is 90 or 270 ? (height, width) : (width, height);
    }

    private static void DrawEdit(PdfPage page, PdfEdit edit, int rotation)
    {
        var box = page.EffectiveCropBoxReadOnly;
        var width = box.Width; var height = box.Height;
        var left = box.X1; var top = page.Height.Point - box.Y2;
        using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        var matrix = rotation switch
        {
            90 => new XMatrix(0, -1, 1, 0, left, top + height),
            180 => new XMatrix(-1, 0, 0, -1, left + width, top + height),
            270 => new XMatrix(0, 1, -1, 0, left + width, top),
            _ => new XMatrix(1, 0, 0, 1, left, top)
        };
        graphics.MultiplyTransform(matrix);
        var displayWidth = rotation is 90 or 270 ? height : width;
        var displayHeight = rotation is 90 or 270 ? width : height;
        var rect = new XRect(edit.X * displayWidth, edit.Y * displayHeight, edit.Width * displayWidth, edit.Height * displayHeight);
        if (edit.Kind == PdfEditKind.Cover) { graphics.DrawRectangle(XBrushes.Black, rect); return; }
        if (edit.Kind == PdfEditKind.Highlight) { graphics.DrawRectangle(new XSolidBrush(XColor.FromArgb(90, 255, 220, 40)), rect); return; }
        if (edit.Kind == PdfEditKind.Comment) graphics.DrawRectangle(new XSolidBrush(XColor.FromArgb(255, 255, 245, 190)), rect);
        graphics.IntersectClip(rect);
        var font = new XFont("Toolbox Sans SC", edit.FontSize, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
        new XTextFormatter(graphics).DrawString(edit.Text, font, XBrushes.Black, rect, XStringFormats.TopLeft);
    }

    public static byte[] CreateRasterDocument(IEnumerable<PdfRasterPage> pages, CancellationToken token = default)
    {
        using var document = new PdfDocument();
        document.Info.Creator = "Toolbox - flattened PDF";
        foreach (var raster in pages)
        {
            token.ThrowIfCancellationRequested();
            var page = document.AddPage(); page.Width = XUnit.FromPoint(raster.Width); page.Height = XUnit.FromPoint(raster.Height);
            using var stream = new MemoryStream(raster.Png);
            using var image = XImage.FromStream(stream);
            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawImage(image, 0, 0, raster.Width, raster.Height);
        }
        if (document.PageCount == 0) throw new InvalidOperationException("至少需要一页。");
        using var buffer = new MemoryStream(); document.Save(buffer, closeStream: false);
        return buffer.ToArray();
    }

    public void WriteNewFile(string path, byte[] bytes, CancellationToken token = default)
    {
        path = Path.GetFullPath(path);
        if (_sources.Values.Any(source => source.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) throw new IOException("不能覆盖原始 PDF，请另选文件名。");
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("目标已存在，请使用新的文件名。不会覆盖任何文件。");
        var folder = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("输出目录不存在。");
        var temporary = Path.Combine(folder, ".toolbox-pdf-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            token.ThrowIfCancellationRequested();
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            using (var stream = File.OpenRead(temporary))
            using (var check = PdfReader.Open(stream, PdfDocumentOpenMode.Import))
                if (check.PageCount == 0) throw new IOException("输出 PDF 校验失败。");
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

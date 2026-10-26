using Microsoft.UI.Xaml;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Toolbox.App.Services;
using Toolbox.Core;
using Toolbox.Platform;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Toolbox.App.Views;

public sealed partial class PdfToolsView
{
    internal async Task VerifyPdfAsync(string source, string output, Func<FrameworkElement, string, Task> capture)
    {
        await LoadFilesAsync([source], true, default);
        if (_workspace.Pages.Count != 3 || PageImage.Source is null) throw new Exception("PDF page list or initial preview missing.");
        await capture(this, "pdf-preview.png");
        var original = File.ReadAllBytes(source);
        var ids = _workspace.Pages.Select(page => page.Id).ToArray();
        _workspace.AddEdit(ids[0], PdfEditKind.Text, .1, .18, .8, .15, "中文测试 Toolbox", 18);
        _workspace.AddEdit(ids[0], PdfEditKind.Comment, .1, .75, .8, .15, "这是一条批注 / note", 14);
        _workspace.AddEdit(ids[0], PdfEditKind.Highlight, .1, .075, .6, .06, "", 14);
        _previewId = ids[0]; RefreshPages([ids[0]]); await RefreshPreviewAsync(default);
        await capture(this, "pdf-annotations.png");
        var vector = _workspace.ExportVector(_workspace.Pages);
        _workspace.WriteNewFile(Path.Combine(output, "annotated.pdf"), vector);
        var split = await SplitToFolderAsync(PdfPageRanges.SplitGroups("1-2;3", 3), output, default);
        using (var doc = PdfReader.Open(Path.Combine(split, "part_001.pdf"), PdfDocumentOpenMode.Import))
            if (doc.PageCount != 2) throw new Exception("First split group has the wrong pages.");
        using (var doc = PdfReader.Open(Path.Combine(split, "part_002.pdf"), PdfDocumentOpenMode.Import))
            if (doc.PageCount != 1) throw new Exception("Second split group has the wrong pages.");

        // Test ordinary, rotated, and cropped+rotated pages with edits made before/after rotation.
        _workspace.RotatePages([ids[1]]);
        foreach (var id in ids) _workspace.AddEdit(id, PdfEditKind.Cover, .2, .4, .3, .2, "", 14);
        _workspace.RotatePages([ids[2]]);
        for (var index = 0; index < ids.Length; index++)
        {
            var preview = _workspace.Preview(ids[index]);
            var png = await PdfPreviewRenderer.RenderAsync(preview, 2, default);
            File.WriteAllBytes(Path.Combine(output, $"mask-page-{index + 1}.png"), png);
            await AssertBlackAsync(png, index == 2 ? .5 : .35, index == 2 ? .35 : .5);
        }
        var raster = await BuildExportAsync(_workspace.Pages.ToArray(), default);
        var maskedPath = Path.Combine(output, "safe-masked.pdf");
        _workspace.WriteNewFile(maskedPath, raster);
        using (var stream = new MemoryStream(raster))
        using (var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import))
        {
            if (doc.PageCount != 3) throw new Exception("Raster export changed the page count.");
            foreach (var page in doc.Pages)
            {
                var operators = ContentReader.ReadContent(page).OfType<COperator>().Select(op => op.Name).ToArray();
                if (operators.Contains("BT") || operators.Contains("Tj") || operators.Contains("TJ") || !operators.Contains("Do"))
                    throw new Exception("Safe cover export retained text objects instead of image-only pages.");
            }
        }
        _previewId = ids[0]; RefreshPages([ids[0]]); await RefreshPreviewAsync(default);
        EditMode.SelectedIndex = 4;
        await capture(this, "pdf-safe-cover.png");
        if (!File.ReadAllBytes(source).SequenceEqual(original)) throw new Exception("PDF diagnostics changed the original file.");
    }

    private static async Task AssertBlackAsync(byte[] png, double x, double y)
    {
        using var stream = new InMemoryRandomAccessStream(); await stream.WriteAsync(png.AsBuffer()); stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var data = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
        var index = ((int)(y * decoder.PixelHeight) * (int)decoder.PixelWidth + (int)(x * decoder.PixelWidth)) * 4;
        if (data[index] > 12 || data[index + 1] > 12 || data[index + 2] > 12)
            throw new Exception($"PDF mask coordinate mismatch at {x}, {y}: {data[index]},{data[index + 1]},{data[index + 2]}");
    }
}

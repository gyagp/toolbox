using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Toolbox.Core;
using Toolbox.Platform;

internal static class PdfWorkspaceTests
{
    internal static void Run(Action<bool, string> check, string sandbox)
    {
        var root = Path.Combine(sandbox, "pdf"); Directory.CreateDirectory(root);
        var workspace = new PdfWorkspace();
        var a = Path.Combine(root, "original.pdf"); var b = Path.Combine(root, "second.pdf");
        Create(a, 3, 240); Create(b, 2, 340);
        var original = File.ReadAllBytes(a);
        workspace.AddFiles([a]);
        check(workspace.Pages.Count == 3 && !workspace.IsDirty, "PDF opens into a clean in-memory page plan");
        using (var exclusive = new FileStream(a, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            check(exclusive.Length == original.Length, "Opening PDF does not retain a source file handle");
        workspace.AddFiles([b]);
        check(workspace.Pages.Count == 5 && workspace.IsDirty, "Merge appends every source page in order");
        var lastId = workspace.Pages[^1].Id; workspace.MovePages([lastId], -1);
        check(workspace.Pages[3].Id == lastId, "Selected PDF page moves without changing other page identities");
        workspace.RemovePages([workspace.Pages[0].Id]);
        check(workspace.Pages.Count == 4, "Page deletion changes only the working plan");
        workspace.Undo(); check(workspace.Pages.Count == 5, "Undo restores removed PDF pages");
        workspace.Redo(); check(workspace.Pages.Count == 4, "Redo reapplies the working-plan change");
        var firstId = workspace.Pages[0].Id;
        workspace.RotatePages([firstId]);
        var bytes = workspace.ExportVector(workspace.Pages);
        using (var stream = new MemoryStream(bytes))
        using (var result = PdfReader.Open(stream, PdfDocumentOpenMode.Import))
            check(result.PageCount == 4 && result.Pages[0].Width.Point == 250 && result.Pages[0].Rotate == 90 && result.Pages[2].Width.Point == 350,
                "Export preserves merged order, source dimensions and rotation");
        workspace.AddEdit(firstId, PdfEditKind.Text, .1, .1, .7, .2, "中文 PDF annotation", 14);
        var edit = workspace.Pages[0].Edits.Single();
        workspace.UpdateEdit(firstId, edit.Id, "Updated 中文", 16);
        check(workspace.Pages[0].Edits.Single().Text == "Updated 中文", "PDF text and note edits remain editable before export");
        var preview = workspace.Preview(firstId);
        using (var stream = new MemoryStream(preview.Data))
        using (var result = PdfReader.Open(stream, PdfDocumentOpenMode.Import))
            check(result.PageCount == 1 && preview.Width == 360 && preview.Height == 250, "Preview uses the selected page with its effective rotation");
        workspace.AddEdit(firstId, PdfEditKind.Cover, .2, .3, .3, .1, "", 14);
        var blocked = false;
        try { workspace.ExportVector(workspace.Pages); } catch (InvalidOperationException) { blocked = true; }
        check(blocked && workspace.RequiresRasterization, "A black cover can never be exported as a recoverable vector overlay");
        workspace.Undo(); check(!workspace.RequiresRasterization, "Undo removes a cover and its rasterization requirement");
        var output = Path.Combine(root, "export.pdf");
        workspace.WriteNewFile(output, workspace.ExportVector(workspace.Pages));
        workspace.MarkSaved(); check(!workspace.IsDirty && File.Exists(output), "Successful export can mark the current plan saved");
        var overwriteRejected = false;
        try { workspace.WriteNewFile(a, bytes); } catch (IOException) { overwriteRejected = true; }
        check(overwriteRejected && File.ReadAllBytes(a).SequenceEqual(original), "PDF export rejects original paths and preserves original bytes");
        var existing = File.ReadAllBytes(output);
        try { workspace.WriteNewFile(output, bytes); } catch (IOException) { }
        check(File.ReadAllBytes(output).SequenceEqual(existing), "PDF export never overwrites an existing output file");
        var canceledPath = Path.Combine(root, "canceled.pdf");
        try { workspace.WriteNewFile(canceledPath, bytes, new CancellationToken(true)); } catch (OperationCanceledException) { }
        check(!File.Exists(canceledPath) && !Directory.EnumerateFiles(root, ".toolbox-pdf-*").Any(), "Canceled export leaves no final PDF or staging file");
        var oldCount = workspace.Pages.Count;
        var corrupt = Path.Combine(root, "corrupt.pdf"); File.WriteAllText(corrupt, "invalid PDF fixture");
        try { workspace.AddFiles([b, corrupt]); } catch (Exception) { }
        check(workspace.Pages.Count == oldCount, "Failed multi-file import is transactional");
        var removedAll = false;
        try { workspace.RemovePages(workspace.Pages.Select(page => page.Id)); } catch (InvalidOperationException) { removedAll = true; }
        check(removedAll && workspace.Pages.Count == oldCount, "Cannot remove the final page from a PDF plan");
        check(PdfPageRanges.Parse("3-1,5", 5).SequenceEqual(new[] { 2, 1, 0, 4 }) && PdfPageRanges.SplitGroups("1-2;3", 3).Length == 2,
            "PDF ranges support explicit order and split groups");
        foreach (var range in new[] { "0", "6", "1,1", "1-", "1,,3", "1-9" })
        {
            var rejected = false;
            try { PdfPageRanges.Parse(range, 5); } catch (ArgumentException) { rejected = true; }
            check(rejected, "Reject invalid PDF page range: " + range);
        }
    }

    private static void Create(string path, int count, double width)
    {
        using var document = new PdfDocument();
        for (var index = 0; index < count; index++)
        {
            var page = document.AddPage(); page.Width = XUnit.FromPoint(width + index * 10); page.Height = XUnit.FromPoint(360);
            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawString($"Original page {index + 1}", new XFont("Arial", 18), XBrushes.Black, new XPoint(20, 45));
        }
        document.Save(path);
    }
}

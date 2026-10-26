using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Toolbox.App.Tools;

namespace Toolbox.App;

public sealed partial class MainWindow
{
    internal void CloseAfterDiagnostics() { _allowPdfClose = true; Close(); }
    internal async Task RunPdfTestAsync(string output)
    {
        await Task.Delay(400);
        if (_navigation.ContainsKey(ToolId.Screenshot) || _navigation.ContainsKey(ToolId.Library) || !_navigation.ContainsKey(ToolId.CaptureMenu))
            throw new Exception("Capture tools must share one navigation menu.");
        await RenderUiAsync(output, "overview.png");
        foreach (var id in new[] { ToolId.Screenshot, ToolId.ScrollCapture, ToolId.Recording, ToolId.Library })
        {
            await NavigateAsync(id);
            if (_navigation[ToolId.CaptureMenu].IsChecked != true) throw new Exception("Capture child navigation lost parent selection.");
        }
        await NavigateAsync(ToolId.Pdf); await RenderUiAsync(output, "pdf-empty.png");
        var source = Path.Combine(output, "original-fixture.pdf");
        using (var document = new PdfDocument())
        {
            for (var index = 0; index < 3; index++)
            {
                var page = document.AddPage(); page.Width = XUnit.FromPoint(400); page.Height = XUnit.FromPoint(500);
                if (index == 1) page.Rotate = 90;
                if (index == 2) { page.CropBox = new PdfRectangle(new XPoint(20, 30), new XPoint(380, 470)); page.Rotate = 270; }
                using var graphics = XGraphics.FromPdfPage(page);
                graphics.DrawString("PDF fixture " + (index + 1), new XFont("Toolbox Sans SC", 18), XBrushes.Black, new XPoint(40, 60));
                graphics.DrawString("SECRET", new XFont("Toolbox Sans SC", 18), XBrushes.Black, new XPoint(90, 250));
                graphics.DrawRectangle(XBrushes.SteelBlue, 40, 320, 100, 40);
            }
            document.Save(source);
        }
        await PdfToolsPage.VerifyPdfAsync(source, output, (element, name) => RenderUiAsync(output, name));
        if (!PostDiagnosticClose(_handle, 0x0010, 0, 0)) throw new Exception("Could not request PDF close diagnostic.");
        await Task.Delay(200);
        if (!_reviewingPdfClose || !PdfToolsPage.IsDirty) throw new Exception("Closing must review unsaved PDF changes.");
        PdfToolsPage.Cancel();
        for (var attempt = 0; attempt < 100 && _reviewingPdfClose; attempt++) await Task.Delay(50);
        if (_reviewingPdfClose || !PdfToolsPage.IsDirty || _allowPdfClose)
            throw new Exception($"Canceling close must preserve the PDF workspace: reviewing={_reviewingPdfClose}, dirty={PdfToolsPage.IsDirty}, allowed={_allowPdfClose}");
        AppWindow.Resize(new(980, 760)); await Task.Delay(200);
        await RenderUiAsync(output, "pdf-narrow.png");
    }
}

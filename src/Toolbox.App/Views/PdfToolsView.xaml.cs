using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Toolbox.App.Services;
using Toolbox.Core;
using Toolbox.Platform;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Toolbox.App.Views;

public sealed partial class PdfToolsView : UserControl
{
    private PdfWorkspace _workspace = new();
    private nint _owner;
    private CancellationTokenSource? _operation;
    private ContentDialog? _dialog;
    private bool _ready, _refreshing, _fit = true;
    private double _zoom = 1, _pageWidth, _pageHeight;
    private Guid? _previewId;
    private Point? _dragStart;
    internal bool IsBusy => _operation is not null;
    internal bool IsDirty => _workspace.IsDirty;
    internal event Action<bool>? BusyChanged;
    private Guid[] SelectedIds => PageList.SelectedItems.Cast<PageRow>().Select(row => row.Page.Id).ToArray();

    public PdfToolsView() { InitializeComponent(); _ready = true; UpdateActions(); }
    internal void Initialize(nint owner) => _owner = owner;
    internal void Cancel() { _operation?.Cancel(); _dialog?.Hide(); }
    internal void Deactivate() { _dragStart = null; DragRectangle.Visibility = Visibility.Collapsed; }

    private void UpdateActions()
    {
        if (!_ready) return;
        var available = !IsBusy;
        OpenButton.IsEnabled = MergeButton.IsEnabled = PageList.IsEnabled = PageRange.IsEnabled = EditMode.IsEnabled =
            FontSizePicker.IsEnabled = OverlayText.IsEnabled = EditList.IsEnabled = available;
        ExportButton.IsEnabled = SplitButton.IsEnabled = available && _workspace.Pages.Count > 0;
        UndoButton.IsEnabled = available && _workspace.CanUndo; RedoButton.IsEnabled = available && _workspace.CanRedo;
        UpButton.IsEnabled = DownButton.IsEnabled = RotateButton.IsEnabled = RemoveButton.IsEnabled = available && PageList.SelectedItems.Count > 0;
        UpdateEditButton.IsEnabled = available && EditList.SelectedItem is EditRow row && row.Edit.Kind is PdfEditKind.Text or PdfEditKind.Comment;
        RemoveEditButton.IsEnabled = available && EditList.SelectedItem is not null;
        EditCanvas.IsHitTestVisible = available;
        CancelButton.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Notify(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    { Status.Title = title; Status.Message = message; Status.Severity = severity; Status.IsOpen = true; }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation;
        Status.IsOpen = false; UpdateActions(); BusyChanged?.Invoke(true);
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Notify("已取消", "未覆盖原始 PDF。已完成的导出会保留。" ); }
        catch (Exception error) { Notify("操作未完成", error.Message, InfoBarSeverity.Error); }
        finally { _operation = null; UpdateActions(); BusyChanged?.Invoke(false); }
    }

    private async Task<bool> ShowAsync(ContentDialog dialog)
    {
        dialog.XamlRoot = XamlRoot; _dialog = dialog;
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
        finally { _dialog = null; }
    }

    internal async Task<bool> ConfirmDiscardAsync()
    {
        if (!IsDirty) return true;
        return await ShowAsync(new ContentDialog { Title = "放弃尚未导出的 PDF 修改？", Content = "原始文件没有改变。选择取消可以返回导出。",
            PrimaryButtonText = "放弃修改", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close });
    }

    private async Task<string[]> PickFilesAsync(bool multiple)
    {
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".pdf");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _owner);
        if (multiple) return (await picker.PickMultipleFilesAsync().AsTask(_operation?.Token ?? default)).Select(file => file.Path).ToArray();
        var file = await picker.PickSingleFileAsync().AsTask(_operation?.Token ?? default);
        return file is null ? [] : [file.Path];
    }

    internal Task OpenPathAsync(string path) => RunAsync(async token =>
    {
        if (!await ConfirmDiscardAsync()) return;
        await LoadFilesAsync([path], replace: true, token);
    });
    private async void Open_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (!await ConfirmDiscardAsync()) return;
        var paths = await PickFilesAsync(false); if (paths.Length > 0) await LoadFilesAsync(paths, true, token);
    });
    private async void Merge_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var paths = await PickFilesAsync(true); if (paths.Length > 0) await LoadFilesAsync(paths, false, token);
    });
    private async Task LoadFilesAsync(string[] paths, bool replace, CancellationToken token)
    {
        ProgressText.Text = "正在读取 PDF…";
        var workspace = replace ? new PdfWorkspace() : _workspace;
        await Task.Run(() => workspace.AddFiles(paths, token), token);
        token.ThrowIfCancellationRequested();
        _workspace = workspace; _previewId = null; _fit = true;
        RefreshPages(); await RefreshPreviewAsync(token);
    }

    private void RefreshPages(IEnumerable<Guid>? selected = null)
    {
        _refreshing = true;
        try
        {
            var ids = (selected ?? SelectedIds).ToHashSet();
            var rows = _workspace.Pages.Select((page, index) => new PageRow(page, index)).ToArray();
            PageList.ItemsSource = rows;
            foreach (var row in rows.Where(row => ids.Contains(row.Page.Id))) PageList.SelectedItems.Add(row);
            if (PageList.SelectedItems.Count == 0 && rows.Length > 0) PageList.SelectedItem = rows[0];
            if (_previewId is null || !_workspace.Pages.Any(page => page.Id == _previewId)) _previewId = (PageList.SelectedItem as PageRow)?.Page.Id;
            DocumentSummary.Text = $"{_workspace.Pages.Count} 页 · {(_workspace.IsDirty ? "有未导出的修改" : "原件保留，导出新文件")}";
            EmptyHint.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            PageSurface.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _refreshing = false; }
        UpdateActions();
    }

    private async void Page_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _refreshing || IsBusy) return;
        _previewId = (e.AddedItems.LastOrDefault() as PageRow)?.Page.Id ?? (PageList.SelectedItem as PageRow)?.Page.Id;
        UpdateActions(); await RunAsync(RefreshPreviewAsync);
    }
    private async Task RefreshPreviewAsync(CancellationToken token)
    {
        if (_previewId is not { } id) { PageImage.Source = null; EditList.ItemsSource = null; PageSurface.Visibility = Visibility.Collapsed; return; }
        ProgressText.Text = "正在生成页面预览…";
        var preview = await Task.Run(() => _workspace.Preview(id), token);
        var png = await PdfPreviewRenderer.RenderAsync(preview, 2, token);
        using var stream = new InMemoryRandomAccessStream(); await stream.WriteAsync(png.AsBuffer()).AsTask(token); stream.Seek(0);
        var image = new BitmapImage(); await image.SetSourceAsync(stream).AsTask(token);
        token.ThrowIfCancellationRequested(); PageImage.Source = image;
        _pageWidth = preview.Width; _pageHeight = preview.Height; PageSurface.Visibility = Visibility.Visible;
        ApplyPageSize();
        _refreshing = true;
        EditList.ItemsSource = _workspace.Pages.Single(page => page.Id == id).Edits.Select(edit => new EditRow(edit)).ToArray();
        _refreshing = false;
        ProgressText.Text = $"第 {_workspace.Pages.ToList().FindIndex(page => page.Id == id) + 1} 页 · {preview.Width:0} × {preview.Height:0} pt · {_zoom:P0}";
    }

    private void ApplyPageSize()
    {
        if (_pageWidth <= 0 || _pageHeight <= 0) return;
        if (_fit) _zoom = Math.Max(.1, Math.Min(1.5, Math.Min((PreviewScroller.ActualWidth - 32) / _pageWidth, (PreviewScroller.ActualHeight - 32) / _pageHeight)));
        PageSurface.Width = _pageWidth * _zoom; PageSurface.Height = _pageHeight * _zoom;
        if (!IsBusy && _previewId is { } id)
            ProgressText.Text = $"第 {_workspace.Pages.ToList().FindIndex(page => page.Id == id) + 1} 页 · {_pageWidth:0} × {_pageHeight:0} pt · {_zoom:P0}";
    }
    private void Preview_SizeChanged(object sender, SizeChangedEventArgs e) { if (_ready) ApplyPageSize(); }
    private void Fit_Click(object sender, RoutedEventArgs e) { _fit = true; ApplyPageSize(); }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) { _fit = false; _zoom = Math.Min(4, _zoom * 1.25); ApplyPageSize(); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { _fit = false; _zoom = Math.Max(.1, _zoom / 1.25); ApplyPageSize(); }
    private void FitWidth_Click(object sender, RoutedEventArgs e) { if (_pageWidth <= 0) return; _fit = false; _zoom = Math.Clamp((PreviewScroller.ActualWidth - 32) / _pageWidth, .1, 4); ApplyPageSize(); }

    private async Task ChangePagesAsync(Action change)
    {
        var selected = SelectedIds; change(); RefreshPages(selected); await RefreshPreviewAsync(_operation!.Token);
    }
    private async void Remove_Click(object sender, RoutedEventArgs e) => await RunAsync(_ => ChangePagesAsync(() => _workspace.RemovePages(SelectedIds)));
    private async void Up_Click(object sender, RoutedEventArgs e) => await RunAsync(_ => ChangePagesAsync(() => _workspace.MovePages(SelectedIds, -1)));
    private async void Down_Click(object sender, RoutedEventArgs e) => await RunAsync(_ => ChangePagesAsync(() => _workspace.MovePages(SelectedIds, 1)));
    private async void Rotate_Click(object sender, RoutedEventArgs e) => await RunAsync(_ => ChangePagesAsync(() => _workspace.RotatePages(SelectedIds)));
    private async void Undo_Click(object sender, RoutedEventArgs e) => await RunAsync(_ => ChangePagesAsync(_workspace.Undo));
    private async void Redo_Click(object sender, RoutedEventArgs e) => await RunAsync(_ => ChangePagesAsync(_workspace.Redo));
    private async void SelectRange_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var indexes = PdfPageRanges.Parse(PageRange.Text, _workspace.Pages.Count);
        RefreshPages(indexes.Select(index => _workspace.Pages[index].Id)); _previewId = _workspace.Pages[indexes[0]].Id;
        await RefreshPreviewAsync(token);
    });

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        TextEditor.Visibility = EditMode.SelectedIndex is 1 or 2 ? Visibility.Visible : Visibility.Collapsed;
        EditHint.Text = EditMode.SelectedIndex switch
        {
            0 => "选择左侧页面进行预览；可勾选多页进行整理。",
            4 => "拖出遮盖区域。导出时全文转为图像页，移除原始内容；导出文件将不能搜索或复制文字。",
            _ => "在页面上拖出区域；修改会立即预览，可撤销。文字过多时请扩大区域。"
        };
    }
    private void Canvas_Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsBusy || EditMode.SelectedIndex == 0 || _previewId is null || !e.GetCurrentPoint(EditCanvas).Properties.IsLeftButtonPressed) return;
        _dragStart = e.GetCurrentPoint(EditCanvas).Position; EditCanvas.CapturePointer(e.Pointer);
        DragRectangle.Visibility = Visibility.Visible; DragRectangle.Width = DragRectangle.Height = 0; e.Handled = true;
    }
    private (double X, double Y, double W, double H) DragBounds(Point end)
    {
        var start = _dragStart!.Value;
        var x = Math.Clamp(Math.Min(start.X, end.X), 0, PageSurface.Width);
        var y = Math.Clamp(Math.Min(start.Y, end.Y), 0, PageSurface.Height);
        return (x, y, Math.Clamp(Math.Max(start.X, end.X), 0, PageSurface.Width) - x, Math.Clamp(Math.Max(start.Y, end.Y), 0, PageSurface.Height) - y);
    }
    private void Canvas_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is null) return;
        var box = DragBounds(e.GetCurrentPoint(EditCanvas).Position);
        Canvas.SetLeft(DragRectangle, box.X); Canvas.SetTop(DragRectangle, box.Y); DragRectangle.Width = box.W; DragRectangle.Height = box.H; e.Handled = true;
    }
    private async void Canvas_Released(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is null || _previewId is not { } pageId) return;
        var box = DragBounds(e.GetCurrentPoint(EditCanvas).Position); _dragStart = null;
        EditCanvas.ReleasePointerCapture(e.Pointer); DragRectangle.Visibility = Visibility.Collapsed; e.Handled = true;
        if (box.W < 4 || box.H < 4) return;
        var kind = (PdfEditKind)(EditMode.SelectedIndex - 1);
        var x = box.X / PageSurface.Width; var y = box.Y / PageSurface.Height;
        var w = box.W / PageSurface.Width; var h = box.H / PageSurface.Height;
        await RunAsync(_ => ChangePagesAsync(() => _workspace.AddEdit(pageId, kind, x, y, w, h, OverlayText.Text, FontSizePicker.Value)));
    }
    private void Canvas_Canceled(object sender, PointerRoutedEventArgs e) { _dragStart = null; DragRectangle.Visibility = Visibility.Collapsed; }
    private void Edit_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _refreshing) return;
        if (EditList.SelectedItem is EditRow row && row.Edit.Kind is PdfEditKind.Text or PdfEditKind.Comment)
        { EditMode.SelectedIndex = (int)row.Edit.Kind + 1; OverlayText.Text = row.Edit.Text; FontSizePicker.Value = row.Edit.FontSize; }
        UpdateActions();
    }
    private async void RemoveEdit_Click(object sender, RoutedEventArgs e) => await RunAsync(_ => ChangePagesAsync(() =>
    { if (_previewId is { } id && EditList.SelectedItem is EditRow row) _workspace.RemoveEdit(id, row.Edit.Id); }));
    private async void UpdateEdit_Click(object sender, RoutedEventArgs e) => await RunAsync(_ => ChangePagesAsync(() =>
    { if (_previewId is { } id && EditList.SelectedItem is EditRow row) _workspace.UpdateEdit(id, row.Edit.Id, OverlayText.Text, FontSizePicker.Value); }));

    private async Task<byte[]> BuildExportAsync(PdfPageItem[] pages, CancellationToken token)
    {
        if (!_workspace.RequiresRasterization) return await Task.Run(() => _workspace.ExportVector(pages, token), token);
        if (pages.Length > 200) throw new InvalidOperationException("安全遮盖导出最多支持 200 页，请先按范围拆分。");
        var rasters = new List<PdfRasterPage>(); long bytes = 0;
        foreach (var page in pages)
        {
            token.ThrowIfCancellationRequested();
            ProgressText.Text = $"正在安全图像化：{rasters.Count + 1}/{pages.Length}…";
            var preview = await Task.Run(() => _workspace.Preview(page.Id), token);
            var png = await PdfPreviewRenderer.RenderAsync(preview, 200.0 / 72, token);
            bytes += png.Length; if (bytes > 128L * 1024 * 1024) throw new IOException("图像化结果超过 128 MiB，请拆分后导出。");
            rasters.Add(new(png, preview.Width, preview.Height));
        }
        return await Task.Run(() => PdfWorkspace.CreateRasterDocument(rasters, token), token);
    }

    private async Task<string?> PickOutputFolderAsync()
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, _owner);
        return (await picker.PickSingleFolderAsync().AsTask(_operation?.Token ?? default))?.Path;
    }
    private async void Export_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var name = new TextBox { Header = "新文件名", Text = _workspace.SuggestedName + "_编辑.pdf" };
        var content = new StackPanel { Spacing = 12 }; content.Children.Add(name);
        content.Children.Add(new TextBlock { Text = _workspace.RequiresRasterization
            ? "包含安全遮盖：整个导出文件将转为图像页，不保留可复制的原文。原件不变。" : "导出当前页面顺序与编辑，保留原件；不会覆盖同名文件。", TextWrapping = TextWrapping.Wrap });
        if (!await ShowAsync(new ContentDialog { Title = "导出 PDF", Content = content, PrimaryButtonText = "选择输出文件夹", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close })) return;
        var leaf = ValidateName(name.Text);
        var folder = await PickOutputFolderAsync(); if (folder is null) return;
        ProgressText.Text = "正在生成 PDF…";
        var bytes = await BuildExportAsync(_workspace.Pages.ToArray(), token);
        var path = Path.Combine(folder, leaf); await Task.Run(() => _workspace.WriteNewFile(path, bytes, token), token);
        _workspace.MarkSaved(); RefreshPages(); Notify("已导出", path, InfoBarSeverity.Success); ProgressText.Text = "导出完成，原件未改变。";
    });
    private async void Split_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var range = new TextBox { Header = "分组页码（空白则每页一个文件）", PlaceholderText = "例如 1-3;4-6;7" };
        var content = new StackPanel { Spacing = 12 }; content.Children.Add(range);
        if (_workspace.RequiresRasterization) content.Children.Add(new TextBlock { Text = "包含安全遮盖：所有拆分结果也将转为图像页，不保留可复制的原文。", TextWrapping = TextWrapping.Wrap });
        if (!await ShowAsync(new ContentDialog { Title = "拆分当前 PDF", Content = content, PrimaryButtonText = "选择输出文件夹", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close })) return;
        var groups = PdfPageRanges.SplitGroups(range.Text, _workspace.Pages.Count);
        var folder = await PickOutputFolderAsync(); if (folder is null) return;
        var final = await SplitToFolderAsync(groups, folder, token);
        Notify("拆分完成", $"{groups.Length} 个文件：{final}", InfoBarSeverity.Success); ProgressText.Text = "拆分完成，原件未改变。";
    });
    private async Task<string> SplitToFolderAsync(int[][] groups, string folder, CancellationToken token)
    {
        var name = _workspace.SuggestedName + "_拆分_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6];
        var staging = Path.Combine(folder, ".toolbox-pdf-" + Guid.NewGuid().ToString("N"));
        var final = Path.Combine(folder, name);
        Directory.CreateDirectory(staging);
        try
        {
            for (var index = 0; index < groups.Length; index++)
            {
                ProgressText.Text = $"正在拆分 {index + 1}/{groups.Length}…";
                var bytes = await BuildExportAsync(groups[index].Select(page => _workspace.Pages[page]).ToArray(), token);
                var path = Path.Combine(staging, $"part_{index + 1:D3}.pdf");
                await Task.Run(() => _workspace.WriteNewFile(path, bytes, token), token);
            }
            token.ThrowIfCancellationRequested(); Directory.Move(staging, final);
            return final;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }
    private static string ValidateName(string value)
    {
        var name = value.Trim();
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("请输入有效的文件名，不要包含路径。");
        return name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? name : name + ".pdf";
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();

    private sealed record PageRow(PdfPageItem Page, int Index)
    {
        public string Title => $"第 {Index + 1} 页";
        public string Source => Page.SourceName;
        public string Detail => $"原第 {Page.SourcePage + 1} 页" + (Page.Rotation != 0 ? $" · 旋转 {Page.Rotation}°" : "") + (Page.Edits.Count > 0 ? $" · {Page.Edits.Count} 处编辑" : "");
    }
    private sealed record EditRow(PdfEdit Edit)
    {
        public string Label => (Edit.Kind switch { PdfEditKind.Text => "文字", PdfEditKind.Comment => "批注", PdfEditKind.Highlight => "标记", _ => "遮盖" }) + (Edit.Text.Length > 0 ? " · " + Edit.Text[..Math.Min(10, Edit.Text.Length)] : "");
    }
}

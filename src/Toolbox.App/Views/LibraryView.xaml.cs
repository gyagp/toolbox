using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Toolbox.App.Services;
using Toolbox.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Toolbox.App.Views;

public sealed class CaptureListItem(CaptureEntry entry)
{
    public CaptureEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string KindLabel => Entry.Kind == CaptureKind.Image ? "图片" : "视频";
    public string Summary => $"{(Entry.DeletedUtc ?? Entry.CreatedUtc).ToLocalTime():MM-dd HH:mm} · {FormatSize(Entry.Size)}";
    internal static string FormatSize(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1048576d:0.#} MB" : $"{bytes / 1024d:0.#} KB";
}

public sealed partial class LibraryView : UserControl, IDisposable
{
    private CaptureLibrary? _library;
    private IReadOnlyList<CaptureEntry> _entries = [];
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly SemaphoreSlim _refreshGate = new(1);
    private readonly SemaphoreSlim _thumbnailGate = new(4);
    private readonly Dictionary<string, BitmapImage> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private MediaPlayer? _player;
    private int _previewVersion;
    private bool _ready, _active, _disposed, _operation, _settingQuery;
    internal bool IsBusy => _operation;
    internal bool IsActive => _active;
    internal CaptureEntry? SelectedEntry => (CaptureList.SelectedItem as CaptureListItem)?.Entry;
    internal int VisibleCount => CaptureList.Items.Count;
    internal bool HasImagePreview => DetailImage.Source is not null;
    internal MediaPlayer? PreviewPlayer => _player;
    internal string DiagnosticStatus => $"{LibraryStatus.Title}: {LibraryStatus.Message}";

    public LibraryView()
    {
        InitializeComponent();
        _ready = true;
        _refreshTimer.Tick += async (_, _) => { _refreshTimer.Stop(); await RefreshAsync(); };
    }

    internal void Initialize(CaptureLibrary library) => _library = library;

    internal async Task ActivateAsync(string? selectPath = null)
    {
        _active = true;
        if (selectPath is not null)
        {
            _settingQuery = true;
            FilterPicker.SelectedIndex = 0;
            SearchBox.Text = "";
            _settingQuery = false;
        }
        await RefreshAsync(selectPath, forcePreview: true);
    }

    internal void Deactivate()
    {
        _active = false;
        _refreshTimer.Stop();
        ClearPreview();
    }

    internal async Task RefreshAsync(string? selectPath = null, bool forcePreview = false)
    {
        if (_library is null || _disposed) return;
        await _refreshGate.WaitAsync();
        try
        {
            if (_disposed) return;
            var snapshot = await Task.Run(_library.Scan);
            if (_disposed) return;
            var entries = snapshot.Entries.OrderBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
            var changed = !_entries.SequenceEqual(entries);
            _entries = entries;
            if (changed || selectPath is not null || CaptureList.ItemsSource is null) ApplyQuery(selectPath);
            else if (forcePreview && _active) await LoadPreviewAsync(SelectedEntry);
            RebuildWatchers();
            if (snapshot.Errors.Count > 0) Notify("部分目录无法读取", string.Join("\n", snapshot.Errors), InfoBarSeverity.Warning);
        }
        catch (Exception error) { Notify("内容库刷新失败", error.Message, InfoBarSeverity.Error); }
        finally { _refreshGate.Release(); }
    }

    private void ApplyQuery(string? selectPath = null)
    {
        if (!_ready || _settingQuery || _disposed) return;
        var previous = selectPath ?? SelectedEntry?.FullPath;
        var filter = (LibraryFilter)Math.Max(0, FilterPicker.SelectedIndex);
        var rows = CaptureLibrary.Query(_entries, filter, SearchBox.Text, (LibrarySort)Math.Max(0, SortPicker.SelectedIndex));
        var items = rows.Select(e => new CaptureListItem(e)).ToArray();
        CaptureList.ItemsSource = items;
        CaptureList.SelectedItem = items.FirstOrDefault(i => string.Equals(i.Entry.FullPath, previous, StringComparison.OrdinalIgnoreCase)) ?? items.FirstOrDefault();
        if (selectPath is not null && CaptureList.SelectedItem is { } selected) CaptureList.ScrollIntoView(selected);
        EmptyState.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyMessage.Text = SearchBox.Text.Length > 0 ? "没有找到匹配的内容。" : filter == LibraryFilter.Deleted ? "最近删除为空。" : "还没有内容，试试截图或录屏吧。";
        var live = _entries.Where(e => !e.IsDeleted).ToArray();
        CountText.Text = $"{live.Count(e => e.Kind == CaptureKind.Image)} 张图片 · {live.Count(e => e.Kind == CaptureKind.Video)} 个视频 · 当前显示 {items.Length} 项";
        if (filter == LibraryFilter.Deleted)
            Notify("最近删除", "可恢复内容，或永久删除以释放空间；不会自动清空。", InfoBarSeverity.Informational);
    }

    private void RebuildWatchers()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        if (_library is null) return;
        foreach (var root in _library.Roots.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite };
                watcher.Created += FileChanged; watcher.Changed += FileChanged; watcher.Deleted += FileChanged; watcher.Renamed += FileChanged;
                watcher.Error += (_, _) => ScheduleRefresh();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Notify("目录监控未启用", $"可点击刷新更新内容。{error.Message}", InfoBarSeverity.Warning); }
        }
    }
    private void FileChanged(object sender, FileSystemEventArgs args) => ScheduleRefresh();
    private void ScheduleRefresh() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_active || _disposed || _operation) return;
        _refreshTimer.Stop(); _refreshTimer.Start();
    });

    private async void CaptureList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SetButtons();
        await LoadPreviewAsync(SelectedEntry);
    }

    private void ClearPreview()
    {
        _previewVersion++;
        DetailImage.Source = null;
        ImageScroller.Visibility = VideoPreview.Visibility = Visibility.Collapsed;
        if (_player is not null)
        {
            _player.MediaFailed -= Player_MediaFailed;
            VideoPreview.SetMediaPlayer(null);
            _player.Dispose(); _player = null;
        }
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPlaceholder.Text = "图片可缩放浏览，视频可直接播放。";
    }

    internal async Task LoadPreviewAsync(CaptureEntry? entry)
    {
        ClearPreview();
        var request = _previewVersion;
        DetailName.Text = entry?.Name ?? "选择一项内容";
        DetailPath.Text = entry?.FullPath ?? "";
        DetailMetadata.Text = entry is null ? "" : $"{entry.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {CaptureListItem.FormatSize(entry.Size)}";
        if (entry is null || !_active || _disposed || _library is null) return;
        PreviewPlaceholder.Text = "正在加载预览…";
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(_library.ValidatePath(entry.FullPath));
            if (request != _previewVersion || !_active || _disposed) return;
            if (entry.Kind == CaptureKind.Image)
            {
                var properties = await file.Properties.GetImagePropertiesAsync();
                if (request != _previewVersion || !_active || _disposed) return;
                var scale = Math.Min(1d, Math.Min(2048d / Math.Max(1, properties.Width), 8192d / Math.Max(1, properties.Height)));
                scale = Math.Min(scale, Math.Sqrt(8_000_000d / Math.Max(1d, properties.Width * (double)properties.Height)));
                var bitmap = new BitmapImage { DecodePixelWidth = Math.Max(1, (int)(properties.Width * scale)) };
                using var stream = await file.OpenReadAsync();
                if (request != _previewVersion || !_active || _disposed) return;
                await bitmap.SetSourceAsync(stream);
                if (request != _previewVersion || !_active || _disposed) return;
                DetailImage.Source = bitmap;
                DetailImage.Width = bitmap.PixelWidth;
                DetailImage.Height = bitmap.PixelHeight;
                ImageScroller.Visibility = Visibility.Visible;
                FitImage();
                DetailMetadata.Text += $" · {properties.Width} × {properties.Height} 像素";
            }
            else
            {
                var properties = await file.Properties.GetVideoPropertiesAsync();
                if (request != _previewVersion || !_active || _disposed) return;
                _player = new MediaPlayer { AutoPlay = false, Source = MediaSource.CreateFromStorageFile(file) };
                _player.MediaFailed += Player_MediaFailed;
                VideoPreview.SetMediaPlayer(_player);
                VideoPreview.Visibility = Visibility.Visible;
                DetailMetadata.Text += $" · {properties.Width} × {properties.Height} · {properties.Duration:hh\\:mm\\:ss}";
            }
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            if (request != _previewVersion || _disposed) return;
            PreviewPlaceholder.Text = "无法预览此文件，可尝试使用系统应用打开。";
            Notify("预览未完成", error.Message, InfoBarSeverity.Warning);
        }
    }
    private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (ReferenceEquals(sender, _player) && !_disposed) Notify("视频无法播放", args.ErrorMessage, InfoBarSeverity.Warning);
    });

    private async void Thumbnail_Loaded(object sender, RoutedEventArgs e) => await LoadThumbnailAsync((Image)sender);
    private async void Thumbnail_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender.IsLoaded) await LoadThumbnailAsync((Image)sender);
    }
    private void Thumbnail_Unloaded(object sender, RoutedEventArgs e) { var image = (Image)sender; image.Tag = null; image.Source = null; }
    private async Task LoadThumbnailAsync(Image image)
    {
        image.Source = null;
        var request = new object(); image.Tag = request;
        if (image.DataContext is not CaptureListItem item || _disposed) return;
        var key = $"{item.Entry.FullPath}|{item.Entry.ModifiedUtc.Ticks}|{item.Entry.Size}";
        await _thumbnailGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(image.Tag, request) || !image.IsLoaded || _disposed) return;
            if (!_thumbnails.TryGetValue(key, out var bitmap))
            {
                var file = await StorageFile.GetFileFromPathAsync(item.Entry.FullPath);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 160);
                if (thumbnail is null) return;
                bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(thumbnail);
                if (_disposed) return;
                if (_thumbnails.Count >= 128) _thumbnails.Remove(_thumbnails.Keys.First());
                _thumbnails[key] = bitmap;
            }
            if (ReferenceEquals(image.Tag, request) && image.IsLoaded && !_disposed) image.Source = bitmap;
        }
        catch (Exception) { /* Keep the image/video placeholder for missing or corrupt thumbnails. */ }
        finally { _thumbnailGate.Release(); }
    }

    private void SetButtons()
    {
        var entry = SelectedEntry;
        var available = entry is not null && !_operation;
        OpenButton.IsEnabled = CopyButton.IsEnabled = RevealButton.IsEnabled = available;
        RenameButton.IsEnabled = DeleteButton.IsEnabled = available && entry?.IsDeleted == false;
        RestoreButton.IsEnabled = PurgeButton.IsEnabled = available;
        RestoreButton.Visibility = PurgeButton.Visibility = entry?.IsDeleted == true ? Visibility.Visible : Visibility.Collapsed;
        RenameButton.Visibility = DeleteButton.Visibility = entry?.IsDeleted == true ? Visibility.Collapsed : Visibility.Visible;
        CopyButton.Content = entry?.Kind == CaptureKind.Video ? "复制文件" : "复制图片";
        FitButton.Visibility = entry?.Kind == CaptureKind.Image ? Visibility.Visible : Visibility.Collapsed;
        CaptureList.IsEnabled = FilterPicker.IsEnabled = SortPicker.IsEnabled = SearchBox.IsEnabled = ReloadButton.IsEnabled = !_operation;
    }
    private void Notify(string title, string message, InfoBarSeverity severity)
    {
        if (_disposed) return;
        LibraryStatus.Title = title; LibraryStatus.Message = message; LibraryStatus.Severity = severity; LibraryStatus.IsOpen = true;
    }
    private async Task ExecuteAsync(Func<CaptureEntry, Task> action)
    {
        if (_operation || SelectedEntry is not { } entry || _library is null) return;
        _operation = true; SetButtons();
        try { await action(entry); }
        catch (Exception error) { Notify("操作未完成", error.Message, InfoBarSeverity.Error); await RefreshAsync(forcePreview: true); }
        finally { _operation = false; SetButtons(); }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async entry =>
    {
        var name = new TextBox { Text = entry.Stem, MaxLength = 180, MinWidth = 300, Header = $"文件名（扩展名 {Path.GetExtension(entry.Name)} 保持不变）" };
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "重命名", Content = name, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        ClearPreview();
        var stem = name.Text;
        var path = await Task.Run(() => _library!.Rename(entry.FullPath, stem));
        await RefreshAsync(path, forcePreview: true);
        Notify("已重命名", Path.GetFileName(path), InfoBarSeverity.Success);
    });
    private async void Delete_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async entry =>
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "移至最近删除？", Content = $"{entry.Name}\n\n文件会移入工具箱的“最近删除”，可随时恢复。", PrimaryButtonText = "移至最近删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        ClearPreview();
        await Task.Run(() => _library!.MoveToDeleted(entry.FullPath));
        await RefreshAsync();
        Notify("已移至最近删除", "可在上方类型筛选中选择“最近删除”并恢复。", InfoBarSeverity.Success);
    });
    private async void Restore_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async entry =>
    {
        ClearPreview();
        var path = await Task.Run(() => _library!.Restore(entry.FullPath));
        _settingQuery = true; FilterPicker.SelectedIndex = 0; SearchBox.Text = ""; _settingQuery = false;
        await RefreshAsync(path, forcePreview: true);
        Notify("已恢复", Path.GetFileName(path), InfoBarSeverity.Success);
    });
    private async void Purge_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async entry =>
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "永久删除此文件？", Content = $"{entry.Name}\n\n永久删除会释放占用的空间，此操作无法恢复。", PrimaryButtonText = "永久删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        ClearPreview();
        await Task.Run(() => _library!.PermanentlyDelete(entry.FullPath));
        await RefreshAsync();
        Notify("已永久删除", entry.Name, InfoBarSeverity.Success);
    });
    private async void Copy_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async entry =>
    {
        var path = _library!.ValidatePath(entry.FullPath);
        if (entry.Kind == CaptureKind.Image) await ImageStore.CopyAsync(path);
        else
        {
            var package = new DataPackage();
            package.SetStorageItems([await StorageFile.GetFileFromPathAsync(path)]);
            Clipboard.SetContent(package); Clipboard.Flush();
        }
        Notify("已复制", entry.Kind == CaptureKind.Image ? "可以粘贴图片。" : "可以粘贴录屏文件。", InfoBarSeverity.Success);
    });
    private void FitImage()
    {
        if (DetailImage.Source is not BitmapImage bitmap || bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0) return;
        ImageScroller.UpdateLayout();
        var zoom = ImageScroller.ViewportWidth / bitmap.PixelWidth;
        if (bitmap.PixelHeight < bitmap.PixelWidth * 3) zoom = Math.Min(zoom, ImageScroller.ViewportHeight / bitmap.PixelHeight);
        ImageScroller.ChangeView(0, 0, (float)Math.Clamp(zoom, ImageScroller.MinZoomFactor, 1), true);
    }
    private void Fit_Click(object sender, RoutedEventArgs e) => FitImage();
    private async void Open_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(entry =>
    {
        Process.Start(new ProcessStartInfo(_library!.ValidatePath(entry.FullPath)) { UseShellExecute = true });
        return Task.CompletedTask;
    });
    private async void Reveal_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(entry =>
    {
        var path = _library!.ValidatePath(entry.FullPath);
        Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"", UseShellExecute = true });
        return Task.CompletedTask;
    });
    private void OpenFolder(int index)
    {
        try
        {
            if (_library is null) return;
            var path = _library.Roots[Math.Min(index, _library.Roots.Count - 1)];
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error) { Notify("无法打开目录", error.Message, InfoBarSeverity.Error); }
    }
    private void ImagesFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(0);
    private void VideosFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(1);
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(forcePreview: true);
    private void Query_Changed(object sender, SelectionChangedEventArgs e) => ApplyQuery();
    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyQuery();

    internal void SetQuery(LibraryFilter filter, string search, LibrarySort sort = LibrarySort.Newest)
    {
        _settingQuery = true; FilterPicker.SelectedIndex = (int)filter; SearchBox.Text = search; SortPicker.SelectedIndex = (int)sort; _settingQuery = false;
        ApplyQuery();
    }

    public void Dispose()
    {
        _disposed = true; _active = false; _refreshTimer.Stop(); ClearPreview();
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear(); _thumbnails.Clear();
    }
}

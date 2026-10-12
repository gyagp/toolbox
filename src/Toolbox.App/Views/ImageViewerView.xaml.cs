using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Toolbox.App.Views;

public sealed partial class ImageViewerView : UserControl
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".jxr"];
    private nint _owner;
    private BitmapImage? _bitmap;
    private IRandomAccessStream? _stream;
    private string? _path;
    private string[] _siblings = [];
    private int _version;
    private bool _loading;
    private double _imageScale = 1;

    public ImageViewerView() => InitializeComponent();
    internal void Initialize(nint owner) => _owner = owner;
    internal bool IsAnimated => _bitmap?.IsAnimatedBitmap == true;
    internal bool IsPlaying => _bitmap?.IsPlaying == true;
    internal bool HasImage => _bitmap is not null;
    internal string? CurrentPath => _path;
    internal Image DisplayedImage => Picture;
    internal string DiagnosticMessage => ImageStatus.Message;

    internal async Task ActivateAsync()
    {
        if (_bitmap is null && _path is not null) await OpenPathAsync(_path);
    }

    internal void Deactivate()
    {
        _version++;
        ReleaseImage();
        SetLoading(false);
    }

    private void ReleaseImage()
    {
        _bitmap?.Stop();
        Picture.Source = null;
        _bitmap = null;
        _stream?.Dispose(); _stream = null;
        UpdateControls();
    }

    private void SetLoading(bool loading)
    {
        _loading = loading;
        LoadingRing.IsActive = loading;
        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = !loading && _bitmap is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateControls();
    }

    private void UpdateControls()
    {
        var ready = _bitmap is not null && !_loading;
        ZoomInButton.IsEnabled = ZoomOutButton.IsEnabled = FitButton.IsEnabled = ActualSizeButton.IsEnabled = ready;
        var index = Array.FindIndex(_siblings, path => string.Equals(path, _path, StringComparison.OrdinalIgnoreCase));
        PreviousButton.IsEnabled = !_loading && index > 0;
        NextButton.IsEnabled = !_loading && index >= 0 && index + 1 < _siblings.Length;
        AnimationButton.Visibility = ready && IsAnimated ? Visibility.Visible : Visibility.Collapsed;
        AnimationButton.Content = IsPlaying ? "暂停 GIF" : "播放 GIF";
    }

    internal async Task OpenPathAsync(string path)
    {
        var version = ++_version;
        ReleaseImage();
        SetLoading(true);
        ImageStatus.IsOpen = false;
        IRandomAccessStream? stream = null;
        BitmapImage? bitmap = null;
        try
        {
            path = Path.GetFullPath(path);
            var file = await StorageFile.GetFileFromPathAsync(path);
            stream = await file.OpenReadAsync();
            bitmap = new BitmapImage { AutoPlay = true };
            await bitmap.SetSourceAsync(stream);
            if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) throw new IOException("图片没有可显示的像素。");
            string[] siblings;
            try
            {
                siblings = await Task.Run(() => Directory.EnumerateFiles(Path.GetDirectoryName(path)!)
                    .Where(candidate => Extensions.Contains(Path.GetExtension(candidate), StringComparer.OrdinalIgnoreCase))
                    .Append(path).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { siblings = [path]; }
            if (version != _version) return;
            _path = path; _siblings = siblings; _bitmap = bitmap; _stream = stream;
            Picture.Source = bitmap;
            // At zoom 1 each image pixel occupies one physical screen pixel.
            SetImageScale(1);
            ImageName.Text = Path.GetFileName(path);
            ImageInfo.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight} · {Path.GetExtension(path).TrimStart('.').ToUpperInvariant()}" +
                (bitmap.IsAnimatedBitmap ? " 动画" : "") + $" · {Array.IndexOf(_siblings, path) + 1} / {_siblings.Length} · {path}";
            bitmap = null; stream = null; // Ownership transferred to the view.
            ImageViewport.UpdateLayout();
            Fit();
        }
        catch (Exception error)
        {
            if (version == _version)
            {
                ImageStatus.Title = "无法打开图片";
                ImageStatus.Message = $"{error.Message}\n文件可能已移动、损坏或使用当前 Windows 未安装的图像解码器。";
                ImageStatus.IsOpen = true;
                ImageName.Text = Path.GetFileName(path);
                ImageInfo.Text = path;
            }
        }
        finally
        {
            bitmap?.Stop(); stream?.Dispose();
            if (version == _version) SetLoading(false);
        }
    }

    private void Fit()
    {
        if (_bitmap is null || Picture.Width <= 0) return;
        SetImageScale(1);
        var factor = Math.Min(1, Math.Min(ImageViewport.ViewportWidth / Picture.Width, ImageViewport.ViewportHeight / Picture.Height));
        // ScrollViewer's minimum is 0.1. Resize the visual base for very tall images
        // so "fit" still shows the complete image, while 100% can restore native pixels.
        if (factor > 0 && factor < .1) { SetImageScale(factor / .1); factor = .1; }
        ImageViewport.ChangeView(0, 0, (float)Math.Clamp(factor, .1, 10), true);
        UpdateZoomLabel();
    }

    private void SetImageScale(double factor)
    {
        if (_bitmap is null) return;
        _imageScale = factor;
        var dpi = XamlRoot?.RasterizationScale ?? 1;
        Picture.Width = _bitmap.PixelWidth * factor / dpi;
        Picture.Height = _bitmap.PixelHeight * factor / dpi;
    }
    private void Zoom(double factor) => ImageViewport.ChangeView(null, null, (float)Math.Clamp(factor, .1, 10), true);
    private void UpdateZoomLabel() => ZoomLabel.Text = $"{ImageViewport.ZoomFactor * _imageScale:P0}";
    internal void ToggleAnimation()
    {
        if (_bitmap is null || !IsAnimated) return;
        if (IsPlaying) _bitmap.Stop(); else _bitmap.Play();
        UpdateControls();
    }
    internal async Task NavigateImageAsync(int offset)
    {
        var index = Array.FindIndex(_siblings, path => string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) + offset;
        if (!_loading && index >= 0 && index < _siblings.Length) await OpenPathAsync(_siblings[index]);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            foreach (var extension in Extensions) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _owner);
            var file = await picker.PickSingleFileAsync();
            if (file is not null) await OpenPathAsync(file.Path);
        }
        catch (Exception error) { ImageStatus.Title = "无法选择图片"; ImageStatus.Message = error.Message; ImageStatus.IsOpen = true; }
    }
    private async void Previous_Click(object sender, RoutedEventArgs e) => await NavigateImageAsync(-1);
    private async void Next_Click(object sender, RoutedEventArgs e) => await NavigateImageAsync(1);
    private void Fit_Click(object sender, RoutedEventArgs e) => Fit();
    private void ActualSize_Click(object sender, RoutedEventArgs e) { SetImageScale(1); Zoom(1); UpdateZoomLabel(); }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(ImageViewport.ZoomFactor * 1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(ImageViewport.ZoomFactor / 1.25);
    private void Animation_Click(object sender, RoutedEventArgs e) => ToggleAnimation();
    private void Viewport_Changed(object sender, ScrollViewerViewChangedEventArgs e) => UpdateZoomLabel();
    private void Image_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy;
    }
    private async void Image_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var file = (await e.DataView.GetStorageItemsAsync()).OfType<StorageFile>().FirstOrDefault();
            if (file is not null) await OpenPathAsync(file.Path);
        }
        catch (Exception error) { ImageStatus.Title = "无法打开拖入的图片"; ImageStatus.Message = error.Message; ImageStatus.IsOpen = true; }
    }
}

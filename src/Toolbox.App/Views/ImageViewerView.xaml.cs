using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Toolbox.Core;
using Toolbox.Platform;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Toolbox.App.Views;

public sealed partial class ImageViewerView : UserControl
{
    private nint _owner;
    private BitmapImage? _bitmap;
    private IRandomAccessStream? _stream;
    private string? _path;
    private string[] _siblings = [];
    private int _version;
    private bool _loading;
    private double _imageScale = 1;
    private bool _fitToViewport = true;
    private readonly DispatcherTimer _chromeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    internal bool IsFullScreen { get; private set; }
    internal event Action<bool>? FullScreenRequested;
    internal event Action<string>? ImageOpened;

    public ImageViewerView()
    {
        InitializeComponent();
        _chromeTimer.Tick += (_, _) =>
        {
            _chromeTimer.Stop();
            for (var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; focused is not null; focused = VisualTreeHelper.GetParent(focused))
                if (ReferenceEquals(focused, FullScreenChrome)) return;
            FullScreenChrome.Visibility = Visibility.Collapsed;
            FullScreenChrome.Opacity = 0;
            FullScreenChrome.IsHitTestVisible = false;
        };
        ImageViewport.SizeChanged += (_, _) => { if (_fitToViewport && HasImage) Fit(); };
    }
    internal void Initialize(nint owner) => _owner = owner;
    internal bool IsAnimated => _bitmap?.IsAnimatedBitmap == true;
    internal bool IsPlaying => _bitmap?.IsPlaying == true;
    internal bool HasImage => _bitmap is not null;
    internal string? CurrentPath => _path;
    internal Image DisplayedImage => Picture;
    internal string DiagnosticMessage => ImageStatus.Message;
    internal bool HasKeyboardFocus
    {
        get
        {
            for (var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; focused is not null; focused = VisualTreeHelper.GetParent(focused))
                if (ReferenceEquals(focused, ImageViewport)) return true;
            return false;
        }
    }

    internal async Task ActivateAsync()
    {
        if (_bitmap is null && _path is not null) await OpenPathAsync(_path);
        ImageViewport.Focus(FocusState.Programmatic);
    }

    internal void Deactivate()
    {
        _chromeTimer.Stop();
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
        FullScreenButton.IsEnabled = ready;
        var index = Array.FindIndex(_siblings, path => string.Equals(path, _path, StringComparison.OrdinalIgnoreCase));
        PreviousButton.IsEnabled = !_loading && index > 0;
        NextButton.IsEnabled = !_loading && index >= 0 && index + 1 < _siblings.Length;
        FullScreenPrevious.IsEnabled = PreviousButton.IsEnabled;
        FullScreenNext.IsEnabled = NextButton.IsEnabled;
        AnimationButton.Visibility = ready && IsAnimated ? Visibility.Visible : Visibility.Collapsed;
        AnimationButton.Content = IsPlaying ? "暂停 GIF" : "播放 GIF";
        FullScreenAnimation.Visibility = AnimationButton.Visibility;
        FullScreenAnimation.Content = AnimationButton.Content;
    }

    internal async Task OpenPathAsync(string path)
    {
        var version = ++_version;
        ReleaseImage();
        SetLoading(true);
        ImageStatus.IsOpen = false;
        ImageStatus.Severity = InfoBarSeverity.Error;
        IRandomAccessStream? stream = null;
        BitmapImage? bitmap = null;
        try
        {
            path = Path.GetFullPath(path);
            string[] siblings;
            try
            {
                siblings = await Task.Run(() => Directory.EnumerateFiles(Path.GetDirectoryName(path)!)
                    .Where(ImageFileTypes.Supports)
                    .Append(path).Distinct(StringComparer.OrdinalIgnoreCase).Order(ExplorerImageComparer.Instance).ToArray());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { siblings = [path]; }
            if (version != _version) return;
            // Keep the attempted position even for a corrupt/missing image, so the
            // next arrow can move beyond it instead of getting stuck on that item.
            _path = path; _siblings = siblings;
            FullScreenCaption.Text = $"{Path.GetFileName(path)} · {Array.IndexOf(siblings, path) + 1} / {siblings.Length} · ← → 翻图 · Esc 退出";
            var file = await StorageFile.GetFileFromPathAsync(path);
            stream = await file.OpenReadAsync();
            bitmap = new BitmapImage { AutoPlay = true };
            await bitmap.SetSourceAsync(stream);
            if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) throw new IOException("图片没有可显示的像素。");
            if (version != _version) return;
            _path = path; _siblings = siblings; _bitmap = bitmap; _stream = stream;
            Picture.Source = bitmap;
            // At zoom 1 each image pixel occupies one physical screen pixel.
            SetImageScale(1);
            ImageName.Text = Path.GetFileName(path);
            FullScreenCaption.Text = $"{ImageName.Text} · {Array.IndexOf(_siblings, path) + 1} / {_siblings.Length} · ← → 翻图 · Esc 退出";
            ImageInfo.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight} · {Path.GetExtension(path).TrimStart('.').ToUpperInvariant()}" +
                (bitmap.IsAnimatedBitmap ? " 动画" : "") + $" · {Array.IndexOf(_siblings, path) + 1} / {_siblings.Length} · {path}";
            bitmap = null; stream = null; // Ownership transferred to the view.
            ImageViewport.UpdateLayout();
            Fit();
            ImageOpened?.Invoke(path);
            ImageViewport.Focus(FocusState.Programmatic);
            ShowFullScreenControls();
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
        _fitToViewport = true;
        SetImageScale(1);
        var factor = Math.Min(IsFullScreen ? 10 : 1, Math.Min(ImageViewport.ViewportWidth / Picture.Width, ImageViewport.ViewportHeight / Picture.Height));
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
    private void Zoom(double factor)
    {
        _fitToViewport = false;
        ImageViewport.ChangeView(null, null, (float)Math.Clamp(factor, .1, 10), true);
    }
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
            foreach (var extension in ImageFileTypes.Extensions) picker.FileTypeFilter.Add(extension);
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

    internal void SetFullScreen(bool fullScreen)
    {
        IsFullScreen = fullScreen;
        _fitToViewport = true;
        ViewerHeader.Visibility = ViewerToolbar.Visibility = ViewerFooter.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        ViewerLayout.Padding = fullScreen ? new(0) : new(28, 28, 28, 24);
        ViewerLayout.RowSpacing = fullScreen ? 0 : 16;
        Grid.SetRow(ImageSurface, fullScreen ? 0 : 3);
        Grid.SetRowSpan(ImageSurface, fullScreen ? 5 : 1);
        ImageSurface.CornerRadius = new(fullScreen ? 0 : 14);
        ImageSurface.BorderThickness = new(fullScreen ? 0 : 1);
        ImageSurface.Background = new SolidColorBrush(fullScreen ? Microsoft.UI.ColorHelper.FromArgb(255, 16, 21, 19) : Microsoft.UI.ColorHelper.FromArgb(255, 234, 240, 236));
        FullScreenChrome.Visibility = fullScreen ? Visibility.Visible : Visibility.Collapsed;
        _chromeTimer.Stop();
        UpdateLayout();
        Fit();
        ImageViewport.Focus(FocusState.Programmatic);
        ShowFullScreenControls();
    }

    private void ShowFullScreenControls()
    {
        if (!IsFullScreen) return;
        FullScreenChrome.Visibility = Visibility.Visible;
        FullScreenChrome.Opacity = 1;
        FullScreenChrome.IsHitTestVisible = true;
        _chromeTimer.Stop(); _chromeTimer.Start();
    }

    internal async Task HandleViewerKeyAsync(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.F11: if (HasImage || IsFullScreen) FullScreenRequested?.Invoke(!IsFullScreen); break;
            case VirtualKey.Escape: if (IsFullScreen) FullScreenRequested?.Invoke(false); break;
            case VirtualKey.Left: case VirtualKey.PageUp: await NavigateImageAsync(-1); break;
            case VirtualKey.Right: case VirtualKey.PageDown: await NavigateImageAsync(1); break;
            case VirtualKey.Home: if (!_loading && _siblings.Length > 0) await OpenPathAsync(_siblings[0]); break;
            case VirtualKey.End: if (!_loading && _siblings.Length > 0) await OpenPathAsync(_siblings[^1]); break;
            case VirtualKey.Space: ToggleAnimation(); break;
        }
        ShowFullScreenControls();
    }

    private async void Viewer_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var handled = e.Key is VirtualKey.F11 or VirtualKey.Left or VirtualKey.Right or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End ||
            (e.Key == VirtualKey.Escape && IsFullScreen) || (e.Key == VirtualKey.Space && IsAnimated);
        if (!handled) return;
        e.Handled = true;
        await HandleViewerKeyAsync(e.Key);
    }
    private void FullScreen_Click(object sender, RoutedEventArgs e) => FullScreenRequested?.Invoke(!IsFullScreen);
    private void Viewport_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (HasImage || IsFullScreen) { e.Handled = true; FullScreenRequested?.Invoke(!IsFullScreen); }
    }
    private void Viewer_PointerMoved(object sender, PointerRoutedEventArgs e) => ShowFullScreenControls();

    private async void Associate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ImageFileAssociations.Register(Environment.ProcessPath!);
            ImageStatus.Severity = InfoBarSeverity.Informational;
            ImageStatus.Title = "已添加到 Windows 打开方式";
            ImageStatus.Message = "请在默认应用设置中选择 Toolbox 和要关联的格式；也可右键图片选择“打开方式”。";
            ImageStatus.IsOpen = true;
            await Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps?registeredAppUser=Toolbox"));
        }
        catch (Exception error) { ImageStatus.Severity = InfoBarSeverity.Error; ImageStatus.Title = "无法设置图片关联"; ImageStatus.Message = error.Message; ImageStatus.IsOpen = true; }
    }
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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using EhViewerReader.ViewModels;

namespace EhViewerReader.Views;

/// <summary>Hosts the local image query workflow inside the existing Frame.</summary>
public partial class ImageSearchPage : DocumentPage
{
    private Point? _cropStart;
    private ImageSearchViewModel? _hooked;
    private ImageSearchViewModel? ViewModel => DataContext as ImageSearchViewModel;

    /// <summary>Initializes the search surface without starting a window.</summary>
    public ImageSearchPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
        Unloaded += (_, _) => { ViewModel?.CancelQuery(); if (_hooked is not null) _hooked.PropertyChanged -= OnViewModelChanged; _hooked = null; };
        PreviewSurface.SizeChanged += (_, _) => UpdateCropRectangle();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
    }
    /// <summary>Initializes a document bound to a retained search session.</summary>
    public ImageSearchPage(ImageSearchViewModel viewModel) : this() { DataContext = viewModel; Title = "以图搜图"; }
    private async void OnPageLoaded(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        if (_hooked is not null) _hooked.PropertyChanged -= OnViewModelChanged;
        _hooked = vm; vm.PropertyChanged += OnViewModelChanged;
        UpdateCropRectangle(); await vm.RefreshAsync();
    }
    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ImageSearchViewModel.UseCrop) or nameof(ImageSearchViewModel.CropX) or nameof(ImageSearchViewModel.CropY) or nameof(ImageSearchViewModel.CropWidth) or nameof(ImageSearchViewModel.CropHeight) or nameof(ImageSearchViewModel.QueryPreview)) UpdateCropRectangle();
    }
    private void UpdateCropRectangle()
    {
        if (ViewModel is not { } vm) return;
        CropRectangle.IsVisible = vm.HasInput && vm.UseCrop;
        if (!CropRectangle.IsVisible) return;
        Rect display = DisplayRectangle();
        Canvas.SetLeft(CropRectangle, display.X + vm.CropX * display.Width / vm.ImageWidth);
        Canvas.SetTop(CropRectangle, display.Y + vm.CropY * display.Height / vm.ImageHeight);
        CropRectangle.Width = vm.CropWidth * display.Width / vm.ImageWidth; CropRectangle.Height = vm.CropHeight * display.Height / vm.ImageHeight;
    }
    private async void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择查询图片", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("图片") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.gif", "*.bmp"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path && ViewModel is { } vm) await vm.LoadFileAsync(path);
    }
    private async void OnPaste(object? sender, RoutedEventArgs e) => await PasteAsync();
    private async Task PasteAsync()
    {
        if (Clipboard is not { } clipboard || ViewModel is not { } vm) return;
        try
        {
            var files = await clipboard.TryGetFilesAsync();
            if (files?.OfType<IStorageFile>().FirstOrDefault()?.TryGetLocalPath() is { } path) { await vm.LoadFileAsync(path); return; }
            using var bitmap = await clipboard.TryGetBitmapAsync();
            if (bitmap is null) { vm.StatusText = "剪贴板中没有本地图片"; return; }
            if ((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height > 24_000_000) { vm.StatusText = "剪贴板图片像素过大"; return; }
            using var stream = new MemoryStream(); bitmap.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); await vm.LoadBytesAsync(stream.ToArray(), "剪贴板图片");
        }
        catch (Exception error) { vm.StatusText = "无法读取剪贴板图片：" + error.Message; }
    }
    private async void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox || e.Source is Avalonia.Visual visual && visual.FindAncestorOfType<TextBox>() is not null) return;
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; await PasteAsync(); }
    }
    private static void OnDragOver(object? sender, DragEventArgs e) => e.DragEffects = e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().Any() == true ? DragDropEffects.Copy : DragDropEffects.None;
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().FirstOrDefault()?.TryGetLocalPath() is { } path && ViewModel is { } vm)
        { e.Handled = true; await vm.LoadFileAsync(path); }
    }
    private void OnReadHit(object? sender, RoutedEventArgs e) { if (sender is Control { DataContext: ImageSearchResultItem hit }) ViewModel?.ReadHitCommand.Execute(hit); }
    private Rect DisplayRectangle()
    {
        if (ViewModel is not { ImageWidth: > 0, ImageHeight: > 0 } vm) return default;
        double scale = Math.Min(PreviewSurface.Bounds.Width / vm.ImageWidth, PreviewSurface.Bounds.Height / vm.ImageHeight);
        return new((PreviewSurface.Bounds.Width - vm.ImageWidth * scale) / 2, (PreviewSurface.Bounds.Height - vm.ImageHeight * scale) / 2, vm.ImageWidth * scale, vm.ImageHeight * scale);
    }
    private void OnCropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel?.HasInput != true || !e.GetCurrentPoint(PreviewSurface).Properties.IsLeftButtonPressed) return;
        Point point = e.GetPosition(PreviewSurface); if (!DisplayRectangle().Contains(point)) return;
        _cropStart = point; e.Pointer.Capture(PreviewSurface); e.Handled = true;
    }
    private void OnCropMoved(object? sender, PointerEventArgs e)
    {
        if (_cropStart is not { } start || ViewModel is not { } vm) return;
        Rect display = DisplayRectangle(); if (display.Width <= 0 || display.Height <= 0) return;
        Point point = e.GetPosition(PreviewSurface);
        double x = Math.Clamp(point.X, display.X, display.Right), y = Math.Clamp(point.Y, display.Y, display.Bottom);
        double left = Math.Min(start.X, x), top = Math.Min(start.Y, y), right = Math.Max(start.X, x), bottom = Math.Max(start.Y, y);
        if (right - left < 2 || bottom - top < 2) return;
        vm.CropX = Math.Max(0, (int)Math.Floor((left - display.X) * vm.ImageWidth / display.Width));
        vm.CropY = Math.Max(0, (int)Math.Floor((top - display.Y) * vm.ImageHeight / display.Height));
        vm.CropWidth = Math.Min(vm.ImageWidth, (int)Math.Ceiling((right - display.X) * vm.ImageWidth / display.Width)) - vm.CropX;
        vm.CropHeight = Math.Min(vm.ImageHeight, (int)Math.Ceiling((bottom - display.Y) * vm.ImageHeight / display.Height)) - vm.CropY;
        vm.UseCrop = true; UpdateCropRectangle();
    }
    private void OnCropReleased(object? sender, PointerReleasedEventArgs e) { _cropStart = null; e.Pointer.Capture(null); }
    /// <summary>Disposes the session only when the Frame actually releases this document.</summary>
    public override void Dispose() { ViewModel?.Dispose(); base.Dispose(); }
}

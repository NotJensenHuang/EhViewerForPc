using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EhViewerReader.Services;
using EhViewerReader.ViewModels;

namespace EhViewerReader.Views;

public partial class GalleryDetailsPage : DocumentPage
{
    private readonly GalleryItemViewModel _item;
    private readonly AppServices _services;

    public GalleryDetailsPage(GalleryItemViewModel item, AppServices services)
    {
        InitializeComponent();
        Avalonia.Media.TextOptions.SetTextRenderingMode(this, Avalonia.Media.TextRenderingMode.Antialias);
        _item = item;
        _services = services;
        DataContext = item;
        Title = "画廊详情 · " + item.Title;
    }

    public event EventHandler<GalleryItemViewModel>? ReadRequested;
    private void OnRead(object? sender, RoutedEventArgs e)
    {
        if (_item.PageCount > 0) ReadRequested?.Invoke(this, _item);
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            EhViewerReader.Services.PlatformLauncher.Open(_item.FolderPath);
        }
        catch (Exception ex)
        {
            _services.Log.Warn("Open folder failed: " + _item.FolderPath, ex);
        }
    }

    private void OnToggleFavorite(object? sender, RoutedEventArgs e)
    {
        _services.Progress.ToggleFavorite(_item.WorkId);
        _item.ApplyProgress(GalleryItemViewModel.LoadProgress(_services.Progress, _item.Info));
        _services.TrySaveProgress(out _);
    }

    private void OnClearProgress(object? sender, RoutedEventArgs e)
    {
        _services.Progress.ResetReading(_item.WorkId);
        _item.ApplyProgress(_services.Progress.Get(_item.WorkId));
        _services.TrySaveProgress(out _);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

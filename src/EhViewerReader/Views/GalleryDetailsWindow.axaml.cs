using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EhViewerReader.Services;
using EhViewerReader.ViewModels;

namespace EhViewerReader.Views;

public partial class GalleryDetailsWindow : Window
{
    private readonly GalleryItemViewModel _item;
    private readonly AppServices _services;

    public GalleryDetailsWindow(GalleryItemViewModel item, AppServices services)
    {
        InitializeComponent();
        Icon = AppIcons.Window;
        Avalonia.Media.RenderOptions.SetTextRenderingMode(this, Avalonia.Media.TextRenderingMode.Antialias);
        _item = item;
        _services = services;
        DataContext = item;
        Title = "画廊详情 · " + item.Title;
    }

    private async void OnRead(object? sender, RoutedEventArgs e)
    {
        if (_item.PageCount == 0) return;
        var viewModel = new ReaderViewModel(_services, _item.Info);
        var window = new ReaderWindow(viewModel);
        await window.ShowDialog(this);
        _item.ApplyProgress(GalleryItemViewModel.LoadProgress(_services.Progress, _item.Info));
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + _item.FolderPath + "\"",
                UseShellExecute = true,
            });
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
        _services.Progress.Save(out _);
    }

    private void OnClearProgress(object? sender, RoutedEventArgs e)
    {
        _services.Progress.Clear(_item.WorkId);
        _item.ApplyProgress(null);
        _services.Progress.Save(out _);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EhViewerForPc.Core.Models;
using EhViewerForPc.ViewModels;

namespace EhViewerForPc.Views;

public partial class VersionsPage : DocumentPage
{
    private readonly VersionsViewModel? _viewModel;

    public VersionsPage()
    {
        InitializeComponent();
        Avalonia.Media.TextOptions.SetTextRenderingMode(this, Avalonia.Media.TextRenderingMode.Antialias);
    }

    public VersionsPage(VersionsViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.CheckPagesCommand.ExecuteAsync(null);
    }

    /// <summary>Copy the user chose to read; null when the dialog was just closed.</summary>
    public GalleryInfo? ReadVersion => _viewModel?.ReadVersion;
    public override void Dispose() => _viewModel?.Dispose();

    private async void OnCheckPages(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is Button { Tag: GalleryVersionViewModel version })
            await _viewModel.CheckVersionAsync(version);
    }

    private async void OnContinueDownload(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is Button { Tag: GalleryVersionViewModel version })
            await _viewModel.ContinueDownloadCommand.ExecuteAsync(version);
    }

    /// <summary>Copy the user marked as the default for this work.</summary>
    public GalleryInfo? DefaultVersion => _viewModel?.DefaultVersion;

    private void OnRead(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { Tag: GalleryVersionViewModel version }) return;
        _viewModel.ReadVersion = version.Info;
        Close();
    }

    private void OnSetDefault(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { Tag: GalleryVersionViewModel version }) return;
        _viewModel.DefaultVersion = version.Info;
        Close();
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GalleryVersionViewModel version }) return;
        try
        {
            EhViewerForPc.Services.PlatformLauncher.Open(version.FolderPath);
        }
        catch (Exception ex)
        {
            Services.AppServices.Current?.Log.Warn("Open version folder failed", ex);
        }
    }

    private void OnDeleteCopy(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is Button { Tag: GalleryVersionViewModel version })
            _viewModel.Deletion.Request(version.Info);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

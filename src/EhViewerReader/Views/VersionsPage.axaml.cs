using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EhViewerReader.Core.Models;
using EhViewerReader.ViewModels;

namespace EhViewerReader.Views;

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
    }

    /// <summary>Copy the user chose to read; null when the dialog was just closed.</summary>
    public GalleryInfo? ReadVersion => _viewModel?.ReadVersion;

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
            EhViewerReader.Services.PlatformLauncher.Open(version.FolderPath);
        }
        catch (Exception ex)
        {
            Services.AppServices.Current?.Log.Warn("Open version folder failed", ex);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

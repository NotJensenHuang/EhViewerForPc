using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EhViewerReader.ViewModels;

namespace EhViewerReader.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    /// <summary>Raised after "保存全部设置"; the shell reloads sources and theme.</summary>
    public event EventHandler<bool>? SettingsApplied;

    private void OnOpenDataDirectory(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel) viewModel.OpenDataDirectory();
    }

    private void OnClearOnlineCache(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel) viewModel.ClearOnlineCache();
    }

    private void OnClearCache(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel) viewModel.ClearCaches();
    }

    private void OnApplyAll(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;
        bool sourcesChanged = viewModel.HasSourceChanges;
        viewModel.Apply();
        SettingsApplied?.Invoke(this, sourcesChanged);
    }

    private async void OnPickDownloadRoot(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;
        string? path = await PickFolderAsync("选择下载根目录");
        if (path is not null) viewModel.SetDownloadRoot(path);
    }

    private async void OnAddSource(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;
        string? path = await PickFolderAsync("添加 EhViewer 下载目录");
        if (path is not null) viewModel.AddSource(path);
    }

    private void OnOpenSource(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;
        if (sender is Button { Tag: SourceRowViewModel row }) viewModel.OpenSource(row);
    }

    private void OnRemoveSource(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;
        if (sender is Button { Tag: SourceRowViewModel row }) viewModel.RemoveSource(row);
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null) return null;

        IReadOnlyList<IStorageFolder> folders = await top.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}

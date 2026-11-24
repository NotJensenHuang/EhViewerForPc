using System.Runtime.InteropServices;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EhViewerForPc.ViewModels;

namespace EhViewerForPc.Views;

public sealed partial class MangaTranslationSettingsPage : DocumentPage
{
    private readonly MangaTranslationSettingsViewModel _viewModel;

    public MangaTranslationSettingsPage(MangaTranslationSettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Title = "漫画翻译";
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e) => await _viewModel.CheckResourcesAsync();

    private async void OnPickDirectory(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanEditDirectory || HostWindow is not { } window) return;
        try
        {
            IReadOnlyList<IStorageFolder> folders = await window.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "选择漫画翻译模型目录", AllowMultiple = false });
            if (folders.Count == 0) return;
            using IStorageFolder folder = folders[0];
            if (folder.TryGetLocalPath() is { } path)
                await _viewModel.PickDirectoryAsync(path);
            else
                _viewModel.ReportDirectoryPickerFailure();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or COMException)
        {
            _viewModel.ReportDirectoryPickerFailure();
        }
    }

    public override void Dispose() => Loaded -= OnLoaded;
}

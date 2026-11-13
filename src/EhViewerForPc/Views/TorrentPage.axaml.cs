using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EhViewerForPc.Services;
using EhViewerForPc.ViewModels;

namespace EhViewerForPc.Views;

public partial class TorrentPage : DocumentPage
{
    private TorrentViewModel? _viewModel;

    public TorrentPage()
    {
        InitializeComponent();
    }

    public TorrentPage(TorrentViewModel viewModel, bool initializeOnLoad = true) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        if (initializeOnLoad) Loaded += async (_, _) => await viewModel.InitializeAsync();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnSaveTorrent(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Control { Tag: TorrentItemViewModel item }) return;

        try
        {
            IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存种子",
                SuggestedFileName = TorrentViewModel.SuggestFileName(item),
                DefaultExtension = "torrent",
                FileTypeChoices = [new FilePickerFileType("BitTorrent") { Patterns = ["*.torrent"] }],
            });

            if (file is null) return;

            byte[] bytes = await _viewModel.DownloadAsync(item);
            await using Stream stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus("保存失败：" + ex.Message);
        }
    }

    private async void OnCopyLink(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: TorrentItemViewModel item }) return;
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(item.Url);
            _viewModel?.SetStatus("种子链接已复制");
        }
    }

    private void OnOpenLink(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: TorrentItemViewModel item }) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.Url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _viewModel?.SetStatus("无法打开链接：" + ex.Message);
        }
    }
}

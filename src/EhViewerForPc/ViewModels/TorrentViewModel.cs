using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Services;

namespace EhViewerForPc.ViewModels;

/// <summary>One row of the torrent list.</summary>
public sealed class TorrentItemViewModel
{
    public TorrentItemViewModel(EhTorrent torrent)
    {
        Torrent = torrent;
    }

    public EhTorrent Torrent { get; }
    public string Name => Torrent.Name;
    public string Url => Torrent.Url;
    public string Posted => Torrent.Posted.Length > 0 ? Torrent.Posted : "—";
    public string SizeText => Torrent.Size.Length > 0 ? Torrent.Size : "—";
    public string HealthText => Torrent.HealthText;
    public string UploaderText => Torrent.Uploader.Length > 0 ? Torrent.Uploader : "—";
    public string DownloadsText => Torrent.Downloads > 0 ? Torrent.Downloads + " 次" : "—";
    public bool HasSeeders => Torrent.Seeds > 0;

    public string StatsLine => $"{SizeText} · {HealthText} · {Posted}";
    public string MetaLine => $"上传者 {UploaderText} · 下载 {DownloadsText}";
}

/// <summary>
/// Torrent browser for one gallery. Files are saved by the window (it owns the
/// file picker); this view model only talks to the site.
/// </summary>
public sealed partial class TorrentViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly EhGalleryDetails _details;
    private bool _loading;
    private string _statusText = "载入种子列表…";

    public TorrentViewModel(AppServices services, EhGalleryDetails details)
    {
        _services = services;
        _details = details;
        Title = "种子 · " + details.DisplayTitle;
    }

    public ObservableCollection<TorrentItemViewModel> Items { get; } = [];

    public string Title { get; }

    /// <summary>Gallery title without the window's action prefix.</summary>
    public string GalleryTitle => _details.DisplayTitle;

    public string Subtitle => $"#{_details.Gid} · {EhUrls.SiteName(_details.Site ?? _services.Online.Site)}";

    public bool IsLoading
    {
        get => _loading;
        private set
        {
            if (SetProperty(ref _loading, value))
            {
                OnPropertyChanged(nameof(ShowEmpty));
            }
        }
    }

    public bool ShowEmpty => !_loading && Items.Count == 0;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public async Task InitializeAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusText = "载入种子列表…";
        try
        {
            EhTorrentList list = await _services.Online
                .GetTorrentsAsync(_details, CancellationToken.None)
                .ConfigureAwait(true);

            Items.Clear();
            foreach (EhTorrent torrent in list.Items) Items.Add(new TorrentItemViewModel(torrent));

            StatusText = list.Items.Count == 0
                ? list.Message
                : $"{list.Items.Count} 个种子 · 保存后可用任意 BT 客户端下载";
            OnPropertyChanged(nameof(ShowEmpty));
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            StatusText = "载入失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await InitializeAsync();

    public void SetStatus(string message) => StatusText = message;

    public async Task<byte[]> DownloadAsync(TorrentItemViewModel item)
    {
        StatusText = "正在下载种子…";
        byte[] bytes = await _services.Online.DownloadTorrentAsync(item.Url, CancellationToken.None, _details.Site)
            .ConfigureAwait(true);
        StatusText = $"已获取 {bytes.Length:N0} 字节";
        return bytes;
    }

    public static string SuggestFileName(TorrentItemViewModel item)
    {
        string name = item.Name.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        if (name.Length == 0) name = "gallery";
        if (name.Length > 80) name = name[..80];
        return name.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) ? name : name + ".torrent";
    }
}

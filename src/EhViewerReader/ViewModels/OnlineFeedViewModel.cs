using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Online;
using EhViewerReader.Services;

namespace EhViewerReader.ViewModels;

public enum OnlineFeedMode
{
    /// <summary>Front page with 首页 / 热门 / 关注 / 排行 tabs.</summary>
    Front = 0,
    Favorites = 1,
    History = 2,
}

/// <summary>
/// One browsable online listing. The same class serves the front page, the
/// favourite slots and the watch history; only the URL builder and the tab set
/// differ. Paging follows the site's own "next" link, with a visited-URL stack
/// so "上一页" never re-derives a page number the site does not use.
/// </summary>
public sealed partial class OnlineFeedViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly IOnlineHost _host;
    private readonly OnlineFeedMode _mode;
    private readonly List<string> _visitedUrls = [];
    private string _nextUrl = string.Empty;
    private int _urlIndex;
    private bool _busy;
    private bool _hasMore;
    private string _statusText = string.Empty;
    private string _pageLabel = string.Empty;
    private FeedOption? _selectedTab;
    private int _toplistIndex = 15;
    private int _favoriteSlot;
    private string _favoriteQuery = string.Empty;
    private CancellationTokenSource? _cancellation;

    public OnlineFeedViewModel(AppServices services, IOnlineHost host, OnlineFeedMode mode, string title, string subtitle)
    {
        _services = services;
        _host = host;
        _mode = mode;
        Title = title;
        Subtitle = subtitle;

        switch (mode)
        {
            case OnlineFeedMode.Front:
                Tabs.Add(new FeedOption("front", "首页"));
                Tabs.Add(new FeedOption("popular", "热门"));
                Tabs.Add(new FeedOption("watched", "关注"));
                Tabs.Add(new FeedOption("toplist", "排行"));
                break;
            case OnlineFeedMode.Favorites:
                for (int slot = 0; slot < 10; slot++)
                {
                    Tabs.Add(new FeedOption(slot.ToString(), _services.Settings.Account.FavoriteSlotName(slot)));
                }

                Tabs.Add(new FeedOption("all", "全部"));
                break;
        }

        _selectedTab = Tabs.Count > 0 ? Tabs[0] : null;
        if (_selectedTab is not null) _selectedTab.IsActive = true;
    }

    public string Title { get; }
    public string Subtitle { get; }

    public ObservableCollection<OnlineGalleryItemViewModel> Items { get; } = [];

    public ObservableCollection<FeedOption> Tabs { get; } = [];

    public bool HasTabs => Tabs.Count > 0;

    public bool IsFavorites => _mode == OnlineFeedMode.Favorites;

    public bool IsFront => _mode == OnlineFeedMode.Front;

    public bool IsHistory => _mode == OnlineFeedMode.History;

    public bool ShowToplistSelector => IsFront && _selectedTab?.Key == "toplist";

    public bool ShowFavoriteSearch => IsFavorites;

    /// <summary>Keyword searched inside the current favourite slot.</summary>
    public string FavoriteQuery
    {
        get => _favoriteQuery;
        set => SetProperty(ref _favoriteQuery, value);
    }

    public string EmptyText => _mode switch
    {
        OnlineFeedMode.Favorites => "收藏夹为空，或该分类下没有作品",
        OnlineFeedMode.History => "还没有浏览记录",
        _ => "没有可显示的作品",
    };

    public IReadOnlyList<FeedOption> ToplistOptions { get; } =
    [
        new FeedOption("15", "昨日"),
        new FeedOption("13", "本月"),
        new FeedOption("12", "今年"),
        new FeedOption("11", "全部"),
    ];

    public FeedOption? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedTab)) return;
            if (_selectedTab is not null) _selectedTab.IsActive = false;
            _selectedTab = value;
            value.IsActive = true;

            if (IsFavorites)
            {
                _favoriteSlot = int.TryParse(value.Key, out int slot) ? slot : -1;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowToplistSelector));
            _ = LoadAsync(reset: true);
        }
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(ShowEmpty));
                OnPropertyChanged(nameof(CanGoBack));
            }
        }
    }

    public bool IsIdle => !_busy;

    public bool ShowEmpty => !_busy && Items.Count == 0;

    public bool HasMore
    {
        get => _hasMore;
        private set => SetProperty(ref _hasMore, value);
    }

    public bool CanGoBack => !_busy && _urlIndex > 0;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string PageLabel
    {
        get => _pageLabel;
        private set => SetProperty(ref _pageLabel, value);
    }

    public int ToplistIndex
    {
        get => _toplistIndex;
        set
        {
            if (!SetProperty(ref _toplistIndex, value)) return;
            OnPropertyChanged(nameof(SelectedToplistOption));
            if (ShowToplistSelector) _ = LoadAsync(reset: true);
        }
    }

    public FeedOption? SelectedToplistOption
    {
        get => ToplistOptions.FirstOrDefault(o => o.Key == _toplistIndex.ToString());
        set
        {
            if (value is null || !int.TryParse(value.Key, out int index)) return;
            ToplistIndex = index;
        }
    }

    public async Task LoadAsync(bool reset)
    {
        if (_busy) return;

        string url;
        if (reset)
        {
            _visitedUrls.Clear();
            _nextUrl = string.Empty;
            _urlIndex = 0;
            url = BuildUrl(0);
        }
        else
        {
            url = _nextUrl.Length > 0 ? _nextUrl : BuildUrl(_urlIndex + 1);
            _urlIndex = _visitedUrls.Count;
        }

        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        CancellationToken token = _cancellation.Token;

        IsBusy = true;
        StatusText = "载入中…";
        int filtered = 0;

        try
        {
            EhGalleryListPage page = await _services.Online.GetListAsync(url, _urlIndex, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();

            if (reset)
            {
                Items.Clear();
                _visitedUrls.Clear();
                _visitedUrls.Add(url);
                _urlIndex = 0;
            }
            else if (_visitedUrls.Count <= _urlIndex)
            {
                _visitedUrls.Add(url);
            }

            foreach (EhGallerySummary summary in page.Items)
            {
                if (!EhFilter.IsVisible(summary, _services.Settings.Online.Filters))
                {
                    filtered++;
                    continue;
                }

                Items.Add(CreateItem(summary));
            }

            _nextUrl = page.NextUrl;
            HasMore = page.HasMore;
            PageLabel = $"第 {_urlIndex + 1} 页";
            StatusText = page.Items.Count == 0
                ? page.StatusMessage ?? EmptyText
                : page.StatusMessage ?? StatusTextFor(page.Items.Count, filtered);
            OnPropertyChanged(nameof(ShowEmpty));
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            StatusText = ex.Message;
            if (reset) Items.Clear();
            OnPropertyChanged(nameof(ShowEmpty));
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanGoBack));
        }
    }

    /// <summary>Reloads the first page; used after sign-in or a site switch.</summary>
    public Task ReloadAsync() => LoadAsync(reset: true);

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync(reset: true);

    [RelayCommand]
    private async Task SearchFavoritesAsync() => await LoadAsync(reset: true);

    [RelayCommand]
    private async Task ClearFavoriteSearchAsync()
    {
        FavoriteQuery = string.Empty;
        await LoadAsync(reset: true);
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (!HasMore || _busy) return;
        await LoadAsync(reset: false);
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (_busy) return;
        if (_urlIndex <= 0)
        {
            await LoadAsync(reset: true);
            return;
        }

        _urlIndex--;
        _nextUrl = string.Empty;
        await LoadAsyncAt(_visitedUrls[_urlIndex]);
    }

    private async Task LoadAsyncAt(string url)
    {
        // Re-fetch the URL that was already visited without touching the stack.
        IsBusy = true;
        StatusText = "载入中…";
        int filtered = 0;
        try
        {
            EhGalleryListPage page = await _services.Online
                .GetListAsync(url, _urlIndex, CancellationToken.None)
                .ConfigureAwait(true);

            Items.Clear();
            foreach (EhGallerySummary summary in page.Items)
            {
                if (!EhFilter.IsVisible(summary, _services.Settings.Online.Filters))
                {
                    filtered++;
                    continue;
                }

                Items.Add(CreateItem(summary));
            }

            _nextUrl = page.NextUrl;
            HasMore = page.HasMore;
            PageLabel = $"第 {_urlIndex + 1} 页";
            StatusText = page.Items.Count == 0
                ? page.StatusMessage ?? EmptyText
                : page.StatusMessage ?? StatusTextFor(page.Items.Count, filtered);
            OnPropertyChanged(nameof(ShowEmpty));
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanGoBack));
        }
    }

    [RelayCommand]
    private void Open(OnlineGalleryItemViewModel? item)
    {
        if (item is null) return;
        _host.OpenGallery(item.Summary);
    }

    [RelayCommand]
    private async Task DownloadAsync(OnlineGalleryItemViewModel? item)
    {
        if (item is null) return;
        await _host.DownloadAsync(item.Summary);
    }

    [RelayCommand]
    private void OpenInBrowser(OnlineGalleryItemViewModel? item)
    {
        if (item is null) return;
        OpenUrl(EhUrls.Detail(_services.Online.Site, item.Gid, item.Summary.Token));
    }

    public void RefreshProgress()
    {
        foreach (OnlineGalleryItemViewModel item in Items)
        {
            item.ApplyProgress(_services.Progress.Get(item.Gid.ToString()));
        }
    }

    /// <summary>Re-checks every card against the local library index.</summary>
    public void RefreshLocalState()
    {
        foreach (OnlineGalleryItemViewModel item in Items) item.RefreshLocalState();
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host.Notify("无法打开浏览器：" + ex.Message);
        }
    }

    private OnlineGalleryItemViewModel CreateItem(EhGallerySummary summary)
    {
        var item = new OnlineGalleryItemViewModel(
            summary,
            _services.OnlineImages.CreateThumbnailRequest(summary.ThumbUrl),
            _host);
        item.ApplyProgress(_services.Progress.Get(summary.Gid.ToString()));
        return item;
    }

    private static string StatusTextFor(int total, int filtered) =>
        filtered > 0 ? $"本页 {total} 项 · 已过滤 {filtered} 项" : $"本页 {total} 项";

    /// <summary>Re-reads custom favourite slot names after settings were saved.</summary>
    public void RefreshFavoriteNames()
    {
        if (!IsFavorites) return;
        foreach (FeedOption tab in Tabs)
        {
            if (int.TryParse(tab.Key, out int slot)) tab.Label = _services.Settings.Account.FavoriteSlotName(slot);
        }
    }

    private string BuildUrl(int page)
    {
        EhSite site = _services.Online.Site;
        return _mode switch
        {
            OnlineFeedMode.Favorites => EhUrls.Favorites(
                site,
                _favoriteSlot,
                _favoriteQuery.Trim().Length == 0 ? null : new EhSearchRequest { Query = _favoriteQuery.Trim() },
                page),
            OnlineFeedMode.History => EhUrls.History(site, page),
            _ => _selectedTab?.Key switch
            {
                "popular" => EhUrls.Popular(site, page),
                "watched" => EhUrls.Watched(site, page),
                "toplist" => EhUrls.Toplist(site, _toplistIndex, page),
                _ => page <= 0 ? EhUrls.Home(site) : $"{EhUrls.Home(site)}?page={page}",
            },
        };
    }
}

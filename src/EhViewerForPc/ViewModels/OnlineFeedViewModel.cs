using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Services;

namespace EhViewerForPc.ViewModels;

public enum OnlineFeedMode
{
    /// <summary>Front page with 首页 / 热门 / 订阅 / 排行 tabs, plus the search bar.</summary>
    Front = 0,
    Favorites = 1,
}

/// <summary>
/// The one online listing page: the front feeds (首页/热门/订阅/排行) and the
/// search results share the same virtualized card grid, continuation and status bar.
/// Searching swaps the URL builder, not the page — so there is a single place
/// to browse and to search.
/// </summary>
public sealed partial class OnlineFeedViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly IOnlineHost _host;
    private readonly OnlineFeedMode _mode;
    private readonly HashSet<string> _loadedUrls = new(StringComparer.Ordinal);
    private readonly HashSet<long> _loadedGids = [];
    private bool _loadMoreFailed;
    private int _emptyBatches;
    private int _filteredCount;
    private int _listRevision;
    private readonly List<FeedOption> _languages;

    private EhSearchRequest _searchRequest = new();
    private string _nextUrl = string.Empty;
    private int _urlIndex;
    private bool _busy;
    private bool _autoLoading;
    private int _requestGeneration;
    private bool _hasMore;
    private string _statusText = string.Empty;
    private string _pageLabel = string.Empty;
    private FeedOption? _selectedTab;
    private int _toplistIndex = 15;
    private int _favoriteSlot;
    private string _favoriteQuery = string.Empty;
    private bool _searchMode;
    private string _query = string.Empty;
    private string _minPages = string.Empty;
    private string _maxPages = string.Empty;
    private bool _searchName = true;
    private bool _searchTags = true;
    private bool _searchDescription;
    private bool _onlyTorrents;
    private FeedOption? _selectedLanguage;
    private bool _showAdvanced;
    private bool _showHistory = true;
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
                Tabs.Add(new FeedOption("watched", "订阅"));
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

        foreach (EhCategory category in EhCategories.AllCategories)
        {
            Categories.Add(new CategoryToggle(category, true));
        }

        _languages =
        [
            new FeedOption(string.Empty, "全部语言"),
            new FeedOption("chinese", "中文"),
            new FeedOption("english", "英文"),
            new FeedOption("japanese", "日文"),
            new FeedOption("korean", "韩文"),
        ];
        _selectedLanguage = _languages[0];

        _selectedSearchMode = SearchModes[IsSubscription ? 1 : 0];
        ReloadSearchHistory();
    }

    public string Title { get; }
    public string Subtitle { get; }

    public ObservableCollection<OnlineGalleryItemViewModel> Items { get; } = [];
    public ObservableCollection<FeedOption> Tabs { get; } = [];
    public ObservableCollection<CategoryToggle> Categories { get; } = [];
    public ObservableCollection<TextItem> History { get; } = [];
    public ObservableCollection<TextItem> SavedSearches { get; } = [];

    public IReadOnlyList<FeedOption> Languages => _languages;

    public bool HasTabs => Tabs.Count > 0;
    public bool HasHistory => History.Count > 0;
    public bool HasSavedSearches => SavedSearches.Count > 0;

    public bool IsFavorites => _mode == OnlineFeedMode.Favorites;

    public bool IsFront => _mode == OnlineFeedMode.Front;

    /// <summary>True while the grid shows search results instead of a feed.</summary>
    public bool IsSearchMode => _searchMode;

    /// <summary>The merged search bar only exists on the browse page.</summary>
    public bool ShowSearchBar => !IsFavorites;

    public bool ShowFavoriteSearch => IsFavorites;

    public bool ShowTabs => HasTabs && !_searchMode;

    public bool ShowSearchResultHeader => _searchMode;

    public bool ShowAdvancedPanel => _showAdvanced && ShowSearchBar;

    public bool ShowHistoryPanel => HasHistory && _showHistory && ShowSearchBar;

    public bool IsSubscription => IsFront && _selectedTab?.Key == "watched";

    public bool ShowToplistSelector => !_searchMode && IsRankings && !ShowRankingOverview;
    public int ListRevision => _listRevision;
    public bool LoadMoreFailed => _loadMoreFailed;
    public bool AutoLoadPaused => _emptyBatches >= 3;
    public bool CanAutoLoadMore => HasMore && !IsBusy && !LoadMoreFailed && !AutoLoadPaused && !ShowRankingOverview;
    public string LoadMoreButtonLabel => LoadMoreFailed ? "重试加载" : "继续加载";
    public bool ShowLoadMoreButton => HasMore && !IsBusy && (LoadMoreFailed || AutoLoadPaused);
    public string LoadMoreText
    {
        get
        {
            if (IsBusy) return "正在加载…";
            if (LoadMoreFailed) return "加载失败，可重试";
            if (AutoLoadPaused) return "暂未找到可显示的作品";
            if (HasMore) return "向下滚动加载更多";
            return Items.Count > 0 ? "已加载全部作品" : string.Empty;
        }
    }

    /// <summary>Keyword searched inside the current favourite slot.</summary>
    public string FavoriteQuery
    {
        get => _favoriteQuery;
        set => SetProperty(ref _favoriteQuery, value);
    }

    public string Query
    {
        get => _query;
        set => SetProperty(ref _query, value);
    }

    public string MinPages
    {
        get => _minPages;
        set => SetProperty(ref _minPages, value);
    }

    public string MaxPages
    {
        get => _maxPages;
        set => SetProperty(ref _maxPages, value);
    }

    public bool SearchName
    {
        get => _searchName;
        set => SetProperty(ref _searchName, value);
    }

    public bool SearchTags
    {
        get => _searchTags;
        set => SetProperty(ref _searchTags, value);
    }

    public bool SearchDescription
    {
        get => _searchDescription;
        set => SetProperty(ref _searchDescription, value);
    }

    public bool OnlyTorrents
    {
        get => _onlyTorrents;
        set => SetProperty(ref _onlyTorrents, value);
    }

    public FeedOption? SelectedLanguage
    {
        get => _selectedLanguage;
        set => SetProperty(ref _selectedLanguage, value);
    }

    public bool ShowAdvanced
    {
        get => _showAdvanced;
        set
        {
            if (!SetProperty(ref _showAdvanced, value)) return;
            OnPropertyChanged(nameof(ShowAdvancedPanel));
        }
    }

    public bool ShowHistory
    {
        get => _showHistory;
        set
        {
            if (!SetProperty(ref _showHistory, value)) return;
            OnPropertyChanged(nameof(ShowHistoryPanel));
        }
    }

    public string SearchResultText => _imageResults ? "图片搜索结果 · " + ImageName : _searchRequest.EffectiveQuery.Length > 0
        ? "搜索结果 · " + _searchRequest.EffectiveQuery
        : "搜索结果";

    public string EmptyText => _mode switch
    {
        OnlineFeedMode.Favorites => "收藏夹为空，或该分类下没有作品",
        _ => _searchMode
            ? "没有匹配结果，试试别的关键词或标签语法"
            : "没有可显示的作品",
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
        set { if (value is not null) SelectTab(value, load: true); }
    }

    private void SelectTab(FeedOption value, bool load)
    {
        if (ReferenceEquals(value, _selectedTab)) return;
        if (_selectedTab is not null) _selectedTab.IsActive = false;
        _selectedTab = value;
        value.IsActive = true;
        _showRankingOverview = true;
        SelectedSearchMode = SearchModes[IsSubscription ? 1 : 0];

        if (IsFavorites)
        {
            _favoriteSlot = int.TryParse(value.Key, out int slot) ? slot : -1;
        }

        // Picking a feed tab leaves the search results behind.
        SetSearchMode(false);

        OnPropertyChanged(nameof(SelectedTab));
        OnPropertyChanged(nameof(ShowToplistSelector));
        OnPropertyChanged(nameof(IsSubscription));
        OnPropertyChanged(nameof(IsRankings));
        OnPropertyChanged(nameof(ShowRankingOverview));
        if (load) _ = LoadAsync(reset: true);
    }

    private void RestoreFeedTab(EhSearchMode mode)
    {
        string key = mode switch { EhSearchMode.Subscription => "watched", EhSearchMode.Popular => "popular", EhSearchMode.Toplist => "toplist", _ => "front" };
        if (Tabs.FirstOrDefault(t => t.Key == key) is { } tab) SelectTab(tab, load: false);
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
                OnPropertyChanged(nameof(ShowBusyBar));
                NotifyLoadMoreState();
            }
        }
    }

    public bool IsIdle => !_busy;

    public bool ShowEmpty => !_busy && (ShowRankingOverview ? RankingSections.Count == 0 : Items.Count == 0 && !HasMore);

    public bool HasMore
    {
        get => _hasMore;
        private set { if (SetProperty(ref _hasMore, value)) NotifyLoadMoreState(); }
    }

    private void NotifyLoadMoreState()
    {
        OnPropertyChanged(nameof(LoadMoreFailed));
        OnPropertyChanged(nameof(AutoLoadPaused));
        OnPropertyChanged(nameof(CanAutoLoadMore));
        OnPropertyChanged(nameof(ShowLoadMoreButton));
        OnPropertyChanged(nameof(LoadMoreText));
        OnPropertyChanged(nameof(LoadMoreButtonLabel));
    }

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

    /// <summary>Runs a search from outside the page (tag chips, GID search).</summary>
    public void RunSearch(string query)
    {
        SelectedSearchMode = SearchModes[0];
        SelectedLanguage = _languages[0];
        Query = query;
        _ = SearchAsync();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        EhSearchRequest request;
        try { request = BuildRequest(); }
        catch (ArgumentException error) { StatusText = error.Message; return; }
        _searchRequest = request;
        _imageResults = false;
        AddSearchHistory(request.EffectiveQuery);
        SetSearchMode(true);
        OnPropertyChanged(nameof(SearchResultText));
        await LoadAsync(reset: true);
    }

    [RelayCommand]
    private async Task ClearSearchAsync()
    {
        Query = string.Empty;
        _imageResults = false;
        if (!_searchMode)
        {
            await LoadAsync(reset: true);
            return;
        }

        SetSearchMode(false);
        StatusText = string.Empty;
        await LoadAsync(reset: true);
    }

    [RelayCommand]
    private void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

    [RelayCommand]
    private void ToggleHistory() => ShowHistory = !ShowHistory;

    [RelayCommand]
    private void UseHistory(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return;
        Query = entry;
        _ = SearchAsync();
    }

    [RelayCommand]
    private void UseSavedSearch(string? name)
    {
        SavedSearchEntry? entry = _services.Settings.SavedSearches.FirstOrDefault(s => s.Name == name);
        if (entry is null) return;

        ApplyRequest(entry.ToRequest());

        _ = SearchAsync();
    }

    [RelayCommand]
    private async Task SaveSearchAsync()
    {
        EhSearchRequest request = CurrentRequest;
        if (_imageResults) { _host.Notify("图片搜索结果不能保存为书签"); return; }
        string key = request.EffectiveQuery;
        if (key.Length == 0) key = request.Mode switch
        { EhSearchMode.Subscription => "订阅", EhSearchMode.Toplist => "排行榜 · " + SelectedToplistOption?.Label,
          EhSearchMode.Popular => "热门", _ => "首页" };
        var entry = SavedSearchEntry.Create(key.Length > 80 ? key[..80] : key, request);
        if (_services.Settings.SavedSearches.Any(s => s.Name == entry.Name))
        { _host.Notify("同名书签已存在，可在书签栏编辑"); return; }

        _services.Settings.SavedSearches.Add(entry);
        _services.TrySaveSettings(out _);
        ReloadSearchHistory();
        BookmarksChanged?.Invoke(this, EventArgs.Empty);
        _host.Notify("已加入书签栏");
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void RemoveSavedSearch(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _services.Settings.SavedSearches.RemoveAll(s => s.Name == name);
        _services.TrySaveSettings(out _);
        ReloadSearchHistory();
        BookmarksChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task ResetSearchOptionsAsync()
    {
        MinPages = string.Empty;
        MaxPages = string.Empty;
        SearchName = true;
        SearchTags = true;
        SearchDescription = false;
        OnlyTorrents = false;
        SelectedSearchMode = SearchModes[IsSubscription ? 1 : 0];
        SearchTorrentNames = OnlyExpunged = SearchLowPowerTags = SearchDownvotedTags = false;
        DisableLanguageFilter = DisableUploaderFilter = DisableTagFilter = false;
        AdvancedEnabled = true; SelectedRating = RatingOptions[0];
        SelectedLanguage = _languages[0];
        foreach (CategoryToggle toggle in Categories) toggle.IsEnabled = true;
        await Task.CompletedTask;
    }

    private void ResetList()
    {
        _loadedUrls.Clear();
        _loadedGids.Clear();
        Items.Clear();
        _urlIndex = 0;
        _nextUrl = string.Empty;
        _loadMoreFailed = false;
        _emptyBatches = 0;
        _filteredCount = 0;
        HasMore = false;
        _listRevision++;
        OnPropertyChanged(nameof(ListRevision));
        NotifyLoadMoreState();
    }

    /// <summary>
    /// 启动时的自动刷新。与 <see cref="LoadAsync"/> 只差一点：它不表示「用户在
    /// 等」。网络卡住时窗口顶端那条不确定进度条不该一直挂着，否则整个程序看
    /// 起来像卡死了；页面自己的状态文字仍会显示「载入中…」。
    /// </summary>
    public async Task AutoLoadAsync()
    {
        if (_disposed || _busy) return;
        _autoLoading = true;
        OnPropertyChanged(nameof(ShowBusyBar));
        try
        {
            await LoadAsync(reset: true).ConfigureAwait(true);
        }
        finally
        {
            _autoLoading = false;
            OnPropertyChanged(nameof(ShowBusyBar));
        }
    }

    /// <summary>窗口顶端的进度条是否该显示：只有用户主动发起的加载才算。</summary>
    public bool ShowBusyBar => _busy && !_autoLoading;

    public async Task LoadAsync(bool reset)
    {
        if (_disposed || (!reset && (_busy || !HasMore))) return;
        if (reset) ResetList();
        if (ShowRankingOverview) { await LoadRankingOverviewAsync(); return; }
        if (reset && _imageResults) { await SearchImageAsync(); return; }
        int target = reset ? 0 : _urlIndex + 1;
        string url = reset ? BuildUrl(0) : _nextUrl;
        if (url.Length == 0) return;
        await FetchListAsync(url, target, reset);
    }

    private async Task FetchListAsync(string url, int target, bool reset)
    {
        int generation = ++_requestGeneration;
        _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = new CancellationTokenSource();
        CancellationToken token = _cancellation.Token;
        IsBusy = true;
        StatusText = reset ? _searchMode ? "搜索中…" : "载入中…" : "正在加载更多…";
        try
        {
            var page = await _services.Online.GetListAsync(url, target, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (generation != _requestGeneration || _disposed) return;
            _urlIndex = target;
            ApplyPage(page, url, reset: false);
        }
        catch (OperationCanceledException)
        {
            if (generation != _requestGeneration || _disposed) return;
            _loadMoreFailed = !reset;
            StatusText = token.IsCancellationRequested ? "已取消，可继续加载" : "请求超时，请重试";
        }
        catch (Exception error)
        {
            if (generation != _requestGeneration || _disposed) return;
            _loadMoreFailed = !reset;
            StatusText = error.Message;
            _services.Log.Warn("Online list: " + error.GetType().Name);
        }
        finally
        {
            if (generation == _requestGeneration && !_disposed)
            {
                IsBusy = false;
                OnPropertyChanged(nameof(ShowEmpty));
                NotifyLoadMoreState();
            }
        }
    }

    internal void ApplyPage(EhGalleryListPage page, string url, bool reset)
    {
        int filtered = 0;
        if (reset) ResetList();
        _loadedUrls.Add(url);
        int before = Items.Count;
        foreach (EhGallerySummary summary in page.Items)
        {
            if (!_loadedGids.Add(summary.Gid)) continue;
            if (!EhFilter.IsVisible(summary, _services.Settings.Online.Filters))
            {
                filtered++;
                continue;
            }
            Items.Add(CreateItem(summary));
        }
        _emptyBatches = Items.Count == before ? _emptyBatches + 1 : 0;
        _filteredCount += filtered;
        _loadMoreFailed = false;
        _nextUrl = page.NextUrl;
        HasMore = page.HasMore && _nextUrl.Length > 0 && !_loadedUrls.Contains(_nextUrl);
        PageLabel = $"已加载 {Items.Count} 项";
        StatusText = page.Items.Count == 0
            ? page.StatusMessage ?? EmptyText
            : page.StatusMessage ?? StatusTextFor(Items.Count, _filteredCount);
        OnPropertyChanged(nameof(ShowEmpty));
        IsBusy = false;
        NotifyLoadMoreState();
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
    private async Task LoadMoreAsync()
    {
        if (_disposed || _busy || !HasMore || ShowRankingOverview) return;
        if (AutoLoadPaused) _emptyBatches = 0;
        _loadMoreFailed = false;
        await LoadAsync(reset: false);
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

    /// <summary>Reloads the favourites feed after a detail window changed the
    /// membership/category, so the list and the detail agree.</summary>
    public void RefreshAfterFavoriteChange()
    {
        if (!IsFavorites || IsBusy) return;
        _ = LoadAsync(reset: true);
    }

    private void SetSearchMode(bool value)
    {
        if (_searchMode == value) return;
        _searchMode = value;
        if (!value) _imageResults = false;
        OnPropertyChanged(nameof(IsSearchMode));
        OnPropertyChanged(nameof(ShowRankingOverview));
        OnPropertyChanged(nameof(ShowTabs));
        OnPropertyChanged(nameof(ShowSearchResultHeader));
        OnPropertyChanged(nameof(ShowToplistSelector));
        OnPropertyChanged(nameof(SearchResultText));
        OnPropertyChanged(nameof(EmptyText));
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
            _services.OnlineImages.CreateThumbnailRequest(summary.ThumbUrl, "work:" + summary.Gid),
            _host);
        item.ApplyProgress(_services.Progress.Get(summary.Gid.ToString()));
        return item;
    }

    private static string StatusTextFor(int total, int filtered) =>
        filtered > 0 ? $"已加载 {total} 项 · 已过滤 {filtered} 项" : $"已加载 {total} 项";

    /// <summary>Re-reads custom favourite slot names after settings were saved.</summary>
    public void RefreshFavoriteNames()
    {
        if (!IsFavorites) return;
        foreach (FeedOption tab in Tabs)
        {
            if (int.TryParse(tab.Key, out int slot)) tab.Label = _services.Settings.Account.FavoriteSlotName(slot);
        }
    }

    /// <summary>Builds and validates all visible search settings.</summary>
    internal EhSearchRequest BuildRequest()
    {
        int ReadPage(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            if (!int.TryParse(value, out int number) || number <= 0) throw new ArgumentException("页数请输入大于零的整数。");
            return number;
        }
        var request = new EhSearchRequest
        {
            Mode = Enum.TryParse<EhSearchMode>(SelectedSearchMode?.Key, out var mode) ? mode : EhSearchMode.Normal,
            Query = Query.Trim(), AdvancedEnabled = AdvancedEnabled,
            ToplistIndex = _toplistIndex,
            CategoryMask = EhCategories.MaskOf(Categories.Where(c => c.IsEnabled).Select(c => c.Category)),
            SearchName = SearchName, SearchTags = SearchTags, SearchDescription = SearchDescription,
            SearchTorrentNames = SearchTorrentNames, OnlyTorrents = OnlyTorrents, OnlyExpunged = OnlyExpunged,
            SearchLowPowerTags = SearchLowPowerTags, SearchDownvotedTags = SearchDownvotedTags,
            DisableLanguageFilter = DisableLanguageFilter, DisableUploaderFilter = DisableUploaderFilter, DisableTagFilter = DisableTagFilter,
            MinRating = int.TryParse(SelectedRating?.Key, out int rating) ? rating : 0,
            Language = AdvancedEnabled ? SelectedLanguage?.Key ?? string.Empty : string.Empty,
        };
        if (AdvancedEnabled)
        {
            request.MinPages = ReadPage(MinPages); request.MaxPages = ReadPage(MaxPages);
            if (request.MinPages > 0 && request.MaxPages > 0 && request.MinPages > request.MaxPages)
                throw new ArgumentException("最少页数不能大于最多页数。");
        }
        if (request.Mode is EhSearchMode.Uploader or EhSearchMode.Tag && request.Query.Length == 0)
            throw new ArgumentException("请填写上传者或标签。");
        return request;
    }

    private void AddSearchHistory(string query)
    {
        string trimmed = query.Trim();
        if (trimmed.Length == 0) return;

        _services.Settings.SearchHistory.RemoveAll(h => string.Equals(h, trimmed, StringComparison.OrdinalIgnoreCase));
        _services.Settings.SearchHistory.Insert(0, trimmed);
        while (_services.Settings.SearchHistory.Count > 15)
        {
            _services.Settings.SearchHistory.RemoveAt(_services.Settings.SearchHistory.Count - 1);
        }

        _services.TrySaveSettings(out _);
        ReloadSearchHistory();
    }

    private void ReloadSearchHistory()
    {
        History.Clear();
        foreach (string entry in _services.Settings.SearchHistory) History.Add(new TextItem(entry));

        SavedSearches.Clear();
        foreach (SavedSearchEntry entry in _services.Settings.SavedSearches) SavedSearches.Add(new TextItem(entry.Name));

        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HasSavedSearches));
        OnPropertyChanged(nameof(ShowHistoryPanel));
    }

    internal string BuildUrl(int page)
    {
        EhSite site = _services.Online.Site;
        if (_searchMode)
        {
            return EhUrls.Search(site, _searchRequest, page);
        }

        return _mode switch
        {
            OnlineFeedMode.Favorites => EhUrls.Favorites(
                site,
                _favoriteSlot,
                _favoriteQuery.Trim().Length == 0 ? null : new EhSearchRequest { Query = _favoriteQuery.Trim() },
                page),
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

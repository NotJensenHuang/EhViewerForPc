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
    /// <summary>Front page with 首页 / 热门 / 关注 / 排行 tabs, plus the search bar.</summary>
    Front = 0,
    Favorites = 1,
}

/// <summary>
/// The one online listing page: the front feeds (首页/热门/关注/排行) and the
/// search results share the same card grid, paging stack and status bar.
/// Searching swaps the URL builder, not the page — so there is a single place
/// to browse and to search.
/// </summary>
public sealed partial class OnlineFeedViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly IOnlineHost _host;
    private readonly OnlineFeedMode _mode;
    private readonly List<string> _visitedUrls = [];
    private readonly List<FeedOption> _languages;

    private EhSearchRequest _searchRequest = new();
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
    public bool ShowSearchBar => IsFront;

    public bool ShowFavoriteSearch => IsFavorites;

    public bool ShowTabs => HasTabs && !_searchMode;

    public bool ShowSearchResultHeader => _searchMode;

    public bool ShowAdvancedPanel => _showAdvanced && IsFront;

    public bool ShowHistoryPanel => HasHistory && _showHistory && IsFront;

    public bool ShowToplistSelector => IsFront && !_searchMode && _selectedTab?.Key == "toplist";

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

    public string SearchResultText => _searchRequest.EffectiveQuery.Length > 0
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

            // Picking a feed tab leaves the search results behind.
            SetSearchMode(false);

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

    /// <summary>Runs a search from outside the page (tag chips, GID search).</summary>
    public void RunSearch(string query)
    {
        Query = query;
        _ = SearchAsync();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        EhSearchRequest request = BuildRequest();
        _searchRequest = request;
        AddSearchHistory(request.EffectiveQuery);
        SetSearchMode(true);
        OnPropertyChanged(nameof(SearchResultText));
        await LoadAsync(reset: true);
    }

    [RelayCommand]
    private async Task ClearSearchAsync()
    {
        Query = string.Empty;
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

        Query = entry.Query;
        MinPages = entry.MinPages > 0 ? entry.MinPages.ToString() : string.Empty;
        MaxPages = entry.MaxPages > 0 ? entry.MaxPages.ToString() : string.Empty;
        SelectedLanguage = _languages.FirstOrDefault(l => l.Key == entry.Language) ?? _languages[0];
        foreach (CategoryToggle toggle in Categories)
        {
            toggle.IsEnabled = (entry.CategoryMask & (1 << (int)toggle.Category)) != 0;
        }

        _ = SearchAsync();
    }

    [RelayCommand]
    private async Task SaveSearchAsync()
    {
        EhSearchRequest request = BuildRequest();
        string key = request.EffectiveQuery;
        if (key.Length == 0)
        {
            _host.Notify("当前搜索为空，无法保存");
            return;
        }

        var entry = new SavedSearchEntry
        {
            Name = key.Length > 40 ? key[..40] : key,
            Query = request.Query,
            CategoryMask = request.CategoryMask,
            MinPages = request.MinPages,
            MaxPages = request.MaxPages,
            Language = request.Language,
        };

        if (_services.Settings.SavedSearches.Any(s => s.Name == entry.Name))
        {
            _host.Notify("同名搜索已保存");
            return;
        }

        _services.Settings.SavedSearches.Add(entry);
        _services.TrySaveSettings(out _);
        ReloadSearchHistory();
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void RemoveSavedSearch(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _services.Settings.SavedSearches.RemoveAll(s => s.Name == name);
        _services.TrySaveSettings(out _);
        ReloadSearchHistory();
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
        SelectedLanguage = _languages[0];
        foreach (CategoryToggle toggle in Categories) toggle.IsEnabled = true;
        await Task.CompletedTask;
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
        StatusText = _searchMode ? "搜索中…" : "载入中…";
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

    private void SetSearchMode(bool value)
    {
        if (_searchMode == value) return;
        _searchMode = value;
        OnPropertyChanged(nameof(IsSearchMode));
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

    private EhSearchRequest BuildRequest()
    {
        var request = new EhSearchRequest
        {
            Query = Query.Trim(),
            CategoryMask = EhCategories.MaskOf(Categories.Where(c => c.IsEnabled).Select(c => c.Category)),
            SearchName = SearchName,
            SearchTags = SearchTags,
            SearchDescription = SearchDescription,
            OnlyTorrents = OnlyTorrents,
            Language = SelectedLanguage?.Key ?? string.Empty,
        };

        if (int.TryParse(MinPages, out int min)) request.MinPages = Math.Max(0, min);
        if (int.TryParse(MaxPages, out int max)) request.MaxPages = Math.Max(0, max);
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

    private string BuildUrl(int page)
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

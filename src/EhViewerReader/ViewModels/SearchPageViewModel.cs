using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Online;
using EhViewerReader.Services;

namespace EhViewerReader.ViewModels;

/// <summary>
/// Advanced search page: keyword + category mask + page range + scope, with the
/// same paging strategy as the feeds (follow the site's next link).
/// </summary>
public sealed partial class SearchPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly IOnlineHost _host;
    private readonly List<string> _visitedUrls = [];
    private readonly List<FeedOption> _languages;

    private EhSearchRequest _activeRequest = new();
    private string _nextUrl = string.Empty;
    private int _urlIndex;
    private bool _busy;
    private bool _hasMore;
    private string _statusText = "输入关键词或标签后搜索";
    private string _pageLabel = string.Empty;
    private string _query = string.Empty;
    private string _minPages = string.Empty;
    private string _maxPages = string.Empty;
    private bool _searchName = true;
    private bool _searchTags = true;
    private bool _searchDescription;
    private bool _onlyTorrents;
    private FeedOption? _selectedLanguage;
    private bool _showAdvanced;
    private bool _showHistory;

    public SearchPageViewModel(AppServices services, IOnlineHost host)
    {
        _services = services;
        _host = host;

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

        ReloadHistory();
    }

    public ObservableCollection<OnlineGalleryItemViewModel> Items { get; } = [];
    public ObservableCollection<CategoryToggle> Categories { get; } = [];
    public IReadOnlyList<FeedOption> Languages => _languages;
    public ObservableCollection<TextItem> History { get; } = [];
    public ObservableCollection<TextItem> SavedSearches { get; } = [];

    public bool HasHistory => History.Count > 0;
    public bool HasSavedSearches => SavedSearches.Count > 0;

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
        set => SetProperty(ref _showAdvanced, value);
    }

    public bool ShowHistory
    {
        get => _showHistory;
        set => SetProperty(ref _showHistory, value);
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                OnPropertyChanged(nameof(ShowEmpty));
                OnPropertyChanged(nameof(CanGoBack));
            }
        }
    }

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

    [RelayCommand]
    private async Task SearchAsync()
    {
        EhSearchRequest request = BuildRequest();
        _activeRequest = request;
        AddHistory(request.EffectiveQuery);
        await LoadAsync(request, reset: true);
    }

    public void ApplyTagSearch(string term)
    {
        Query = term;
        _ = SearchAsync();
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (!HasMore || _busy) return;

        string url = _nextUrl.Length > 0
            ? _nextUrl
            : EhUrls.Search(_services.Online.Site, _activeRequest, _urlIndex + 1);
        _urlIndex = _visitedUrls.Count;
        await LoadUrlAsync(url, append: true, reset: false);
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (_busy || _urlIndex <= 0) return;
        _urlIndex--;
        _nextUrl = string.Empty;
        await LoadUrlAsync(_visitedUrls[_urlIndex], append: false, reset: false);
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        Query = string.Empty;
        MinPages = string.Empty;
        MaxPages = string.Empty;
        SearchName = true;
        SearchTags = true;
        SearchDescription = false;
        OnlyTorrents = false;
        SelectedLanguage = _languages[0];
        foreach (CategoryToggle toggle in Categories) toggle.IsEnabled = true;
        Items.Clear();
        _visitedUrls.Clear();
        _urlIndex = 0;
        _nextUrl = string.Empty;
        HasMore = false;
        PageLabel = string.Empty;
        StatusText = "输入关键词或标签后搜索";
        OnPropertyChanged(nameof(ShowEmpty));
        await Task.CompletedTask;
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
        ReloadHistory();
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void RemoveSavedSearch(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _services.Settings.SavedSearches.RemoveAll(s => s.Name == name);
        _services.TrySaveSettings(out _);
        ReloadHistory();
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

    private async Task LoadAsync(EhSearchRequest request, bool reset)
    {
        if (reset)
        {
            _visitedUrls.Clear();
            _nextUrl = string.Empty;
            _urlIndex = 0;
        }

        string url = EhUrls.Search(_services.Online.Site, request, _urlIndex);
        await LoadUrlAsync(url, append: !reset, reset: reset);
    }

    private async Task LoadUrlAsync(string url, bool append, bool reset)
    {
        IsBusy = true;
        StatusText = "搜索中…";
        int filtered = 0;
        try
        {
            EhGalleryListPage page = await _services.Online
                .GetListAsync(url, _urlIndex, CancellationToken.None)
                .ConfigureAwait(true);

            if (reset)
            {
                Items.Clear();
                _visitedUrls.Clear();
                _visitedUrls.Add(url);
                _urlIndex = 0;
            }
            else if (append || !_visitedUrls.Contains(url))
            {
                if (_visitedUrls.Count <= _urlIndex) _visitedUrls.Add(url);
            }

            foreach (EhGallerySummary summary in page.Items)
            {
                if (!EhFilter.IsVisible(summary, _services.Settings.Online.Filters))
                {
                    filtered++;
                    continue;
                }

                var item = new OnlineGalleryItemViewModel(
                    summary,
                    _services.OnlineImages.CreateThumbnailRequest(summary.ThumbUrl),
                    _host);
                item.ApplyProgress(_services.Progress.Get(summary.Gid.ToString()));
                Items.Add(item);
            }

            _nextUrl = page.NextUrl;
            HasMore = page.HasMore;
            PageLabel = $"第 {_urlIndex + 1} 页";
            StatusText = page.Items.Count == 0
                ? page.StatusMessage ?? "没有匹配结果"
                : page.StatusMessage ?? (filtered > 0
                    ? $"{page.Items.Count} 项结果 · 已过滤 {filtered} 项"
                    : $"{page.Items.Count} 项结果");
            OnPropertyChanged(nameof(ShowEmpty));
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            StatusText = ex.Message;
            if (!append) Items.Clear();
            OnPropertyChanged(nameof(ShowEmpty));
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanGoBack));
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

    private void AddHistory(string query)
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
        ReloadHistory();
    }

    private void ReloadHistory()
    {
        History.Clear();
        foreach (string entry in _services.Settings.SearchHistory) History.Add(new TextItem(entry));

        SavedSearches.Clear();
        foreach (SavedSearchEntry entry in _services.Settings.SavedSearches) SavedSearches.Add(new TextItem(entry.Name));

        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HasSavedSearches));
    }
}

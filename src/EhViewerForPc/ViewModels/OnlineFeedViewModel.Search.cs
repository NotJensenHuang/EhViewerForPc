using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;

namespace EhViewerForPc.ViewModels;

/// <summary>Completes the upstream search form, bookmark restoration and remote image lookup.</summary>
public sealed partial class OnlineFeedViewModel
{
    private FeedOption? _selectedSearchMode;
    private bool _imageResults;
    private EhGalleryListPage? _imageFirstPage;
    private bool _disposed;
    private byte[]? _imageJpeg;
    private SavedSearchEntry? _editingBookmark;
    /// <summary>Raised to persist an edited bookmark's complete form.</summary>
    public event Action<SavedSearchEntry, EhSearchRequest>? BookmarkOptionsSaved;
    /// <summary>Gets whether the full search form is editing an existing bookmark.</summary>
    public bool IsEditingBookmark => _editingBookmark is not null;
    /// <summary>Loads a bookmark into the editor without issuing a network request.</summary>
    public void EditBookmark(SavedSearchEntry entry)
    { _editingBookmark = entry; ApplyRequest(entry.ToRequest()); OnPropertyChanged(nameof(IsEditingBookmark)); }
    [RelayCommand] private void SaveEditedBookmark()
    {
        if (_editingBookmark is not { } entry) return;
        try { var request = BuildRequest(); BookmarkOptionsSaved?.Invoke(entry, request); CancelBookmarkEdit(); }
        catch (ArgumentException error) { StatusText = error.Message; }
    }
    [RelayCommand] private void CancelBookmarkEdit()
    { _editingBookmark = null; OnPropertyChanged(nameof(IsEditingBookmark)); }
    [ObservableProperty] private bool _advancedEnabled = true;
    [ObservableProperty] private bool _searchTorrentNames;
    [ObservableProperty] private bool _onlyExpunged;
    [ObservableProperty] private bool _searchLowPowerTags;
    [ObservableProperty] private bool _searchDownvotedTags;
    [ObservableProperty] private bool _disableLanguageFilter;
    [ObservableProperty] private bool _disableUploaderFilter;
    [ObservableProperty] private bool _disableTagFilter;
    [ObservableProperty] private FeedOption? _selectedRating;
    [ObservableProperty] private bool _showTagLookup;
    [ObservableProperty] private string _tagKeyword = string.Empty;
    [ObservableProperty] private string _tagStatus = "使用本机标签翻译库检索英文或中文标签。";
    [ObservableProperty] private bool _showImageLookup;
    [ObservableProperty] private string _imageName = "尚未选择图片";
    [ObservableProperty] private bool _imageSimilar = true;
    [ObservableProperty] private bool _imageCoversOnly;
    [ObservableProperty] private bool _imageIncludeExpunged;
    [ObservableProperty] private bool _isLoadingImage;

    /// <summary>Raised when bookmark storage changes.</summary>
    public event EventHandler? BookmarksChanged;
    /// <summary>Raised to open the bookmark navigation section.</summary>
    public event EventHandler? BookmarksRequested;

    /// <summary>Gets the four upstream normal-search modes.</summary>
    public IReadOnlyList<FeedOption> SearchModes { get; } =
    [new("Normal", "一般搜索"), new("Subscription", "订阅搜索"), new("Uploader", "指定上传者"), new("Tag", "指定标签"), new("Popular", "热门列表"), new("Toplist", "排行榜")];
    /// <summary>Gets the minimum-rating options supported by the site.</summary>
    public IReadOnlyList<FeedOption> RatingOptions { get; } =
    [new("0", "不限评分"), new("2", "至少 2 星"), new("3", "至少 3 星"), new("4", "至少 4 星"), new("5", "5 星")];
    /// <summary>Gets or sets the current search destination.</summary>
    public FeedOption? SelectedSearchMode
    { get => _selectedSearchMode; set { if (SetProperty(ref _selectedSearchMode, value)) { OnPropertyChanged(nameof(UsesNormalOptions)); OnPropertyChanged(nameof(ShowSearchToplistSelector)); } } }
    /// <summary>Gets the matching canonical tags from the local translation database.</summary>
    public ObservableCollection<TagLookupItem> TagMatches { get; } = [];
    /// <summary>Gets whether the current list can be represented by an Android search bookmark.</summary>
    public bool CanBookmarkCurrent => !IsFavorites && !_imageResults;
    /// <summary>Reloads bookmark chips after edits on the manager page.</summary>
    public void RefreshBookmarks() => ReloadSearchHistory();
    /// <summary>Gets whether ordinary query controls apply to this mode.</summary>
    public bool UsesNormalOptions => SelectedSearchMode?.Key is "Normal" or "Subscription" or "Filter";
    /// <summary>Gets whether the form is choosing a saved ranking period.</summary>
    public bool ShowSearchToplistSelector => SelectedSearchMode?.Key == "Toplist";
    /// <summary>Gets the actual list state saved when adding a bookmark.</summary>
    public EhSearchRequest CurrentRequest => _searchMode ? _searchRequest.Clone() : _mode switch
    {
        OnlineFeedMode.Subscription => new() { Mode = EhSearchMode.Subscription, AdvancedEnabled = false },
        OnlineFeedMode.Toplist => new() { Mode = EhSearchMode.Toplist, ToplistIndex = _toplistIndex, AdvancedEnabled = false },
        _ => new() { Mode = _selectedTab?.Key == "popular" ? EhSearchMode.Popular : EhSearchMode.Normal, AdvancedEnabled = false },
    };

    /// <summary>Restores every field without executing a request.</summary>
    public void ApplyRequest(EhSearchRequest request)
    {
        Query = request.Query; MinPages = request.MinPages > 0 ? request.MinPages.ToString() : "";
        MaxPages = request.MaxPages > 0 ? request.MaxPages.ToString() : "";
        AdvancedEnabled = request.AdvancedEnabled; SearchName = request.SearchName; SearchTags = request.SearchTags;
        SearchDescription = request.SearchDescription; SearchTorrentNames = request.SearchTorrentNames;
        OnlyTorrents = request.OnlyTorrents; OnlyExpunged = request.OnlyExpunged;
        SearchLowPowerTags = request.SearchLowPowerTags; SearchDownvotedTags = request.SearchDownvotedTags;
        DisableLanguageFilter = request.DisableLanguageFilter; DisableUploaderFilter = request.DisableUploaderFilter;
        DisableTagFilter = request.DisableTagFilter;
        SelectedRating = RatingOptions.FirstOrDefault(o => o.Key == request.MinRating.ToString()) ?? RatingOptions[0];
        SelectedLanguage = _languages.FirstOrDefault(l => l.Key == request.Language) ?? _languages[0];
        SelectedSearchMode = SearchModes.FirstOrDefault(m => m.Key == request.Mode.ToString()) ?? SearchModes[0];
        _toplistIndex = request.ToplistIndex; OnPropertyChanged(nameof(SelectedToplistOption));
        foreach (var category in Categories) category.IsEnabled = (request.CategoryMask & (1 << (int)category.Category)) != 0;
        ShowAdvanced = true;
    }

    /// <summary>Opens a saved list state directly, including popular, subscription and ranking modes.</summary>
    public Task OpenBookmarkAsync(SavedSearchEntry entry)
    {
        _imageResults = false; _searchRequest = entry.ToRequest(); ApplyRequest(_searchRequest);
        SetSearchMode(true); OnPropertyChanged(nameof(SearchResultText));
        return LoadAsync(reset: true);
    }

    [RelayCommand] private void OpenBookmarks() => BookmarksRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void ToggleTagLookup() { ShowAdvanced = true; ShowTagLookup = !ShowTagLookup; if (ShowTagLookup) LookupTags(); }
    [RelayCommand] private void ToggleImageLookup() { ShowAdvanced = true; ShowImageLookup = !ShowImageLookup; }
    [RelayCommand] private void LookupTags()
    {
        TagMatches.Clear(); string keyword = TagKeyword.Trim();
        var database = _services.TagTranslation.Database;
        if (database is null) { TagStatus = "尚未下载标签库，可点击“更新标签库”。"; return; }
        foreach (var pair in database.Tags.Where(p => keyword.Length > 0 &&
            (p.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase) || p.Value.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Key, StringComparer.Ordinal).Take(100)) TagMatches.Add(new(pair.Key, pair.Value));
        TagStatus = keyword.Length == 0 ? $"已有 {database.Count:N0} 条标签，输入英文或中文后检索。" : $"找到 {TagMatches.Count} 项（最多显示 100 项）";
    }
    [RelayCommand] private async Task UpdateTagDatabaseAsync()
    {
        TagStatus = "正在更新标签库…";
        await _services.TagTranslation.UpdateAsync(null, CancellationToken.None);
        _services.TrySaveSettings(out _); LookupTags();
    }
    [RelayCommand] private void InsertTag(TagLookupItem? item)
    {
        if (item is null) return;
        int separator = item.Term.IndexOf(':');
        string tag = separator < 0 ? item.Term : item.Term[..(separator + 1)] + "\"" + item.Term[(separator + 1)..] + "$\"";
        Query = Query.Trim().Length == 0 ? tag : Query.Trim() + " " + tag;
        SelectedSearchMode = SearchModes[0]; ShowTagLookup = false;
    }

    /// <summary>Normalizes a selected image on a worker thread; selecting alone never uploads it.</summary>
    public async Task LoadSearchImageAsync(string path)
    {
        if (IsLoadingImage || _disposed) return;
        if (IsBusy) { _requestGeneration++; _cancellation?.Cancel(); IsBusy = false; }
        IsLoadingImage = true;
        try
        {
            var jpeg = await Task.Run(() =>
            {
                var gray = ImageSearch.GrayImage.ReadFile(path);
                using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(gray.Width, gray.Height, SkiaSharp.SKColorType.Gray8));
                System.Runtime.InteropServices.Marshal.Copy(gray.Pixels, 0, bitmap.GetPixels(), gray.Pixels.Length);
                using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
                return encoded.ToArray();
            });
            if (_disposed) return;
            _imageJpeg = jpeg; ImageName = Path.GetFileName(path); ShowImageLookup = true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { _imageJpeg = null; ImageName = "读取图片失败：" + error.Message; }
        finally { IsLoadingImage = false; }
    }

    [RelayCommand] private async Task SearchImageAsync()
    {
        if (_imageJpeg is null || IsLoadingImage) { StatusText = "请先选择图片。"; return; }
        int generation = ++_requestGeneration;
        _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        IsBusy = true; StatusText = "正在上传图片并搜索…";
        try
        {
            var page = await _services.Online.SearchImageAsync(_imageJpeg, ImageSimilar, ImageCoversOnly, ImageIncludeExpunged, token);
            token.ThrowIfCancellationRequested(); if (generation != _requestGeneration) return;
            _imageResults = true; _imageFirstPage = page; SetSearchMode(true); ApplyPage(page, page.SourceUrl, reset: true);
            OnPropertyChanged(nameof(SearchResultText));
        }
        catch (OperationCanceledException) { if (generation == _requestGeneration) StatusText = "图片搜索已取消或超时。"; }
        catch (Exception error) when (error is EhException or HttpRequestException)
        { if (generation == _requestGeneration) StatusText = error.Message; }
        finally { if (generation == _requestGeneration) IsBusy = false; }
    }
    [RelayCommand] private void CancelSearch() => _cancellation?.Cancel();

    /// <summary>Cancels pending network operations and releases query image bytes.</summary>
    public void Dispose() { _disposed = true; _requestGeneration++; _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = null; _imageJpeg = null; _imageFirstPage = null; }
}

/// <summary>Displays a translated tag while retaining its canonical search term.</summary>
public sealed record TagLookupItem(string Term, string Translation);

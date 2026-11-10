using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Scanning;
using EhViewerReader.Services;

namespace EhViewerReader.ViewModels;

public sealed record SortOption(LibrarySort Value, string Label);

public sealed record LanguageOption(string? Value, string Label);

/// <summary>
/// Library screen state. Filtering/sorting always builds a fresh list and swaps
/// it in one notification; per-item notifications are reserved for favourites
/// and reading progress.
///
/// Several sources are scanned in sequence (one disk at a time on purpose: the
/// typical backup drive is a spinning disk). Copies of the same work that live
/// in different sources collapse into one card backed by
/// <see cref="LibraryMerger"/>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly List<GalleryItemViewModel> _all = [];
    private readonly DispatcherTimer _searchTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _scanCancellation;
    private IReadOnlyList<GalleryItemViewModel> _galleries = [];
    private Dictionary<long, GalleryItemViewModel> _byGid = [];
    private IReadOnlyList<SortOption> _sortOptions;
    private IReadOnlyList<FilterOption> _filterOptions;
    private SortOption _selectedSort;
    private FilterOption _selectedFilter;
    private LanguageOption _selectedLanguage;
    private string _searchText = string.Empty;
    private string _statusText = "准备就绪";
    private string _scanPhase = string.Empty;
    private double _scanPercent;
    private bool _isScanning;
    private bool _showWelcome;
    private bool _disposed;
    private int _totalGalleryCount;
    private long _totalPageCount;
    private int _filteredCount;
    private string _rootDisplay = "未配置数据源";
    private int _sourceCount;

    public MainViewModel(AppServices services)
    {
        _services = services;
        _sortOptions =
        [
            new SortOption(LibrarySort.RecentlyRead, "最近阅读"),
            new SortOption(LibrarySort.RecentlyModified, "最近下载"),
            new SortOption(LibrarySort.Title, "标题"),
            new SortOption(LibrarySort.PageCount, "页数"),
            new SortOption(LibrarySort.Gid, "GID"),
        ];
        _filterOptions =
        [
            new FilterOption(LibraryFilter.All, "全部"),
            new FilterOption(LibraryFilter.Reading, "在读"),
            new FilterOption(LibraryFilter.Unread, "未读"),
            new FilterOption(LibraryFilter.Completed, "已读"),
            new FilterOption(LibraryFilter.Favorite, "收藏"),
            new FilterOption(LibraryFilter.Incomplete, "不完整"),
        ];
        _selectedSort = _sortOptions.First(o => o.Value == services.Settings.Sort);
        _selectedFilter = _filterOptions.First(o => o.Value == services.Settings.Filter);
        UpdateFilterActiveStates();
        _selectedLanguage = new LanguageOption(null, "全部语言");

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ApplyFilter();
            RequestListReset();
        };
    }

    public event EventHandler<GalleryItemViewModel>? OpenGalleryRequested;
    public event EventHandler<GalleryItemViewModel>? OpenDetailsRequested;
    public event EventHandler<GalleryItemViewModel>? OpenVersionsRequested;
    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? ChooseFolderRequested;
    public event EventHandler? GalleryListReplaced;
    public event EventHandler? GalleryListRebalanced;

    /// <summary>Raised when the gid → local-copy index changes (scan, reload, clear).</summary>
    public event EventHandler? LocalIndexChanged;

    public AppServices Services => _services;

    /// <summary>True when this gallery id already exists in a local source.</summary>
    public bool HasLocalCopy(long gid)
    {
        lock (_byGid) return _byGid.ContainsKey(gid);
    }

    public bool TryGetLocalCopy(long gid, out GalleryItemViewModel? item)
    {
        lock (_byGid) return _byGid.TryGetValue(gid, out item);
    }

    /// <summary>Snapshot of every work, independent of the current filter/search.</summary>
    public IReadOnlyList<GalleryItemViewModel> SnapshotAllWorks() => _all.ToArray();

    /// <summary>Rebuilds the gid index so online listings can show "已在本机".</summary>
    public void RebuildLocalIndex()
    {
        var map = new Dictionary<long, GalleryItemViewModel>();
        foreach (GalleryItemViewModel item in _all)
        {
            if (item.Gid is long gid) map[gid] = item;
        }

        lock (_byGid) _byGid = map;
        LocalIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<GalleryItemViewModel> Galleries
    {
        get => _galleries;
        private set
        {
            if (SetProperty(ref _galleries, value))
            {
                OnPropertyChanged(nameof(ShowInitialScan));
            }
        }
    }

    public IReadOnlyList<SortOption> SortOptions
    {
        get => _sortOptions;
        private set => SetProperty(ref _sortOptions, value);
    }

    public IReadOnlyList<FilterOption> FilterOptions
    {
        get => _filterOptions;
        private set => SetProperty(ref _filterOptions, value);
    }

    public ObservableCollection<LanguageOption> LanguageOptions { get; } = [];

    public SortOption SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (!SetProperty(ref _selectedSort, value) || value is null) return;
            _services.Settings.Sort = value.Value;
            ApplyFilter();
            RequestListReset();
        }
    }

    public FilterOption SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (!SetProperty(ref _selectedFilter, value) || value is null) return;
            UpdateFilterActiveStates();
            _services.Settings.Filter = value.Value;
            ApplyFilter();
            RequestListReset();
        }
    }

    public void SelectFilter(FilterOption option)
    {
        if (option is null) return;
        SelectedFilter = option;
    }

    private void UpdateFilterActiveStates()
    {
        foreach (FilterOption option in _filterOptions)
        {
            option.IsActive = ReferenceEquals(option, _selectedFilter);
        }
    }

    public LanguageOption SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (!SetProperty(ref _selectedLanguage, value) || value is null) return;
            ApplyFilter();
            RequestListReset();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            _searchTimer.Stop();
            _searchTimer.Start();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ScanPhase
    {
        get => _scanPhase;
        private set => SetProperty(ref _scanPhase, value);
    }

    public double ScanPercent
    {
        get => _scanPercent;
        private set => SetProperty(ref _scanPercent, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(ShowNoResults));
                OnPropertyChanged(nameof(ShowInitialScan));
            }
        }
    }

    public bool IsIdle => !_isScanning;

    public bool ShowWelcome
    {
        get => _showWelcome;
        private set
        {
            if (SetProperty(ref _showWelcome, value))
            {
                OnPropertyChanged(nameof(ShowNoResults));
                OnPropertyChanged(nameof(ShowInitialScan));
            }
        }
    }

    public bool ShowNoResults => !_showWelcome && !_isScanning && _filteredCount == 0;

    public bool ShowInitialScan => !_showWelcome && _isScanning && _galleries.Count == 0;

    public int TotalGalleryCount
    {
        get => _totalGalleryCount;
        private set => SetProperty(ref _totalGalleryCount, value);
    }

    public long TotalPageCount
    {
        get => _totalPageCount;
        private set => SetProperty(ref _totalPageCount, value);
    }

    public int FilteredCount
    {
        get => _filteredCount;
        private set
        {
            if (SetProperty(ref _filteredCount, value))
            {
                OnPropertyChanged(nameof(ShowNoResults));
            }
        }
    }

    public int SourceCount
    {
        get => _sourceCount;
        private set
        {
            if (SetProperty(ref _sourceCount, value))
            {
                OnPropertyChanged(nameof(HasMultipleSources));
            }
        }
    }

    public bool HasMultipleSources => _sourceCount > 1;

    public string RootDisplay
    {
        get => _rootDisplay;
        private set => SetProperty(ref _rootDisplay, value);
    }

    public async Task InitializeAsync()
    {
        UpdateSourceDisplay();
        if (ResolveSources().Count == 0)
        {
            ShowWelcome = true;
            StatusText = "请选择 EhViewer 下载目录";
            return;
        }

        ShowWelcome = false;
        await LoadLibraryAsync(forceRefresh: false, useCacheFirst: true);
    }

    /// <summary>Enabled sources that still exist on disk.</summary>
    public IReadOnlyList<LibrarySource> ResolveSources()
    {
        List<LibrarySource> sources = _services.Settings.Sources.Where(s => s.Enabled).ToList();
        if (sources.Count == 0) return sources;
        return sources.Where(s => s.Exists).ToList();
    }

    private void UpdateSourceDisplay()
    {
        IReadOnlyList<LibrarySource> sources = ResolveSources();
        SourceCount = sources.Count;
        RootDisplay = sources.Count switch
        {
            0 => "未配置数据源",
            1 => sources[0].RootPath,
            _ => $"{sources.Count} 个数据源 · " + string.Join("、", sources.Select(s => s.DisplayName)),
        };
    }

    public async Task LoadLibraryAsync(bool forceRefresh, bool useCacheFirst)
    {
        if (IsScanning) return;

        IReadOnlyList<LibrarySource> sources = ResolveSources();
        UpdateSourceDisplay();
        if (sources.Count == 0)
        {
            _all.Clear();
            Galleries = [];
            FilteredCount = 0;
            TotalGalleryCount = 0;
            TotalPageCount = 0;
            ShowWelcome = true;
            StatusText = _services.Settings.Sources.Any(s => s.Enabled)
                ? "已配置的数据源目录不可用，请检查磁盘是否已连接"
                : "未找到可用的数据源目录";
            RebuildLocalIndex();
            return;
        }

        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken token = _scanCancellation.Token;

        bool qualifyIds = sources.Count > 1;
        IsScanning = true;
        ScanPercent = 0;
        ScanPhase = "读取缓存";

        var caches = new Dictionary<string, LibraryCache>(StringComparer.OrdinalIgnoreCase);
        foreach (LibrarySource source in sources)
        {
            try
            {
                caches[source.Id] = _services.CacheStore.Load(source.Id, source.RootPath, out _);
            }
            catch (Exception ex)
            {
                _services.Log.Warn("Cache load failed: " + source.RootPath, ex);
                caches[source.Id] = new LibraryCache { RootPath = source.RootPath };
            }
        }

        if (useCacheFirst && _all.Count == 0)
        {
            var cached = new List<GalleryInfo>();
            foreach (LibrarySource source in sources)
            {
                LibraryCache cache = caches[source.Id];
                if (cache.Entries.Count == 0) continue;
                cached.AddRange(LibraryScanner.FromCacheOnly(
                    cache, source.RootPath, source.Id, source.DisplayName, qualifyIds));
            }

            if (cached.Count > 0)
            {
                ApplyGalleries(cached, fromCache: true);
                StatusText = $"缓存 {_all.Count:N0} 个作品，正在校验 {sources.Count} 个数据源…";
            }
        }

        try
        {
            var galleries = new List<GalleryInfo>();
            int cacheHits = 0;
            double scanSeconds = 0;

            for (int i = 0; i < sources.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                LibrarySource source = sources[i];
                int index = i;
                int total = sources.Count;
                string name = source.DisplayName;

                var progress = new Progress<ScanProgress>(p => OnScanProgress(p, index, total, name));
                ScanResult result = await Task.Run(
                    () => _services.Scanner.Scan(
                        source.RootPath,
                        caches[source.Id],
                        forceRefresh,
                        progress,
                        token,
                        source.Id,
                        name,
                        qualifyIds),
                    token).ConfigureAwait(true);

                if (token.IsCancellationRequested) return;

                galleries.AddRange(result.Galleries);
                cacheHits += result.CacheHitCount;
                scanSeconds += result.Elapsed.TotalSeconds;
                source.LastScanUtc = DateTime.UtcNow.ToString("O");
                _services.CacheStore.Save(source.Id, result.Cache, out string? cacheError);
                if (cacheError is not null) _services.Log.Warn("Cache save failed: " + cacheError);
            }

            ApplyGalleries(galleries, fromCache: false);
            _services.Settings.LastScanCompletedUtc = DateTime.UtcNow.ToString("O");
            _services.TrySaveSettings(out _);

            string sourceText = sources.Count > 1 ? $"{sources.Count} 个数据源 · " : string.Empty;
            StatusText = $"{sourceText}{TotalGalleryCount:N0} 个作品 · {TotalPageCount:N0} 页 · " +
                         $"{scanSeconds:F1}s（缓存 {cacheHits:N0}）";
            ScanPercent = 100;
            ScanPhase = string.Empty;
        }
        catch (OperationCanceledException)
        {
            StatusText = "扫描已取消";
            ShowWelcome = ShouldShowWelcome;
        }
        catch (Exception ex)
        {
            _services.Log.Error("Library scan failed", ex);
            StatusText = "扫描失败：" + ex.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void OnScanProgress(ScanProgress progress, int index, int total, string sourceName)
    {
        double local = progress.Total <= 0 ? 0 : Math.Clamp(progress.Processed * 100.0 / progress.Total, 0, 100);
        ScanPercent = total <= 0 ? 0 : (index * 100.0 + local) / total;

        string phase = string.IsNullOrEmpty(progress.Phase) ? "扫描" : progress.Phase;
        ScanPhase = total > 1 ? $"[{index + 1}/{total}] {sourceName} · {phase}" : phase;
        if (progress.Total > 0)
        {
            StatusText = total > 1
                ? $"[{index + 1}/{total}] {sourceName} · {phase}… {progress.Processed:N0}/{progress.Total:N0}"
                : $"{phase}… {progress.Processed:N0}/{progress.Total:N0}";
        }
    }

    private void ApplyGalleries(IReadOnlyList<GalleryInfo> galleries, bool fromCache)
    {
        List<GalleryGroup> groups = LibraryMerger.Group(galleries, PreferredSourceOf);
        var items = new List<GalleryItemViewModel>(groups.Count);
        foreach (GalleryGroup group in groups)
        {
            ReadingProgress? progress = GalleryItemViewModel.LoadProgress(_services.Progress, group.Primary);
            var item = new GalleryItemViewModel(
                group,
                info => new CoverImageRequest(info, _services.Covers),
                progress);
            item.IsPrimaryVersionPreferred = IsExplicitlyPreferred(group);
            items.Add(item);
        }

        _all.Clear();
        _all.AddRange(items);
        TotalGalleryCount = _all.Count;
        TotalPageCount = _all.Sum(i => (long)i.PageCount);
        ShowWelcome = ShouldShowWelcome;
        RebuildLanguageOptions();
        UpdateSourceDisplay();

        RebuildLocalIndex();

        if (_all.Count == 0)
        {
            Galleries = [];
            FilteredCount = 0;
        }
        else
        {
            ApplyFilter();
        }

        if (fromCache)
        {
            StatusText = $"已载入缓存 {_all.Count:N0} 个作品";
        }
    }

    private string? PreferredSourceOf(string workId) =>
        _services.Settings.PreferredSourceByWork.TryGetValue(workId, out string? sourceId)
            ? sourceId
            : null;

    private bool IsExplicitlyPreferred(GalleryGroup group) =>
        group.IsMultiVersion &&
        string.Equals(PreferredSourceOf(group.WorkId), group.Primary.SourceId, StringComparison.OrdinalIgnoreCase);

    private void RebuildLanguageOptions()
    {
        string? previous = _selectedLanguage.Value;
        var options = new List<LanguageOption> { new(null, "全部语言") };
        var languages = _all
            .SelectMany(i => i.Info.Languages)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase);
        foreach (string language in languages)
        {
            options.Add(new LanguageOption(language, GalleryTags.GetLanguageDisplayName(language)));
        }

        LanguageOptions.Clear();
        foreach (LanguageOption option in options) LanguageOptions.Add(option);

        _selectedLanguage = options.FirstOrDefault(o => o.Value == previous) ?? options[0];
        OnPropertyChanged(nameof(SelectedLanguage));
    }

    public void ApplyFilter()
    {
        IEnumerable<GalleryItemViewModel> query = _all;

        switch (_selectedFilter.Value)
        {
            case LibraryFilter.Reading:
                query = query.Where(i => !i.IsUnread && !i.IsCompleted);
                break;
            case LibraryFilter.Unread:
                query = query.Where(i => i.IsUnread);
                break;
            case LibraryFilter.Completed:
                query = query.Where(i => i.IsCompleted);
                break;
            case LibraryFilter.Favorite:
                query = query.Where(i => i.IsFavorite);
                break;
            case LibraryFilter.Incomplete:
                query = query.Where(i => i.IsIncomplete);
                break;
        }

        if (_selectedLanguage.Value is { Length: > 0 } language)
        {
            query = query.Where(i => i.Info.Languages.Contains(language, StringComparer.OrdinalIgnoreCase));
        }

        string search = _searchText.Trim();
        if (search.Length > 0)
        {
            bool numeric = long.TryParse(search, out long gidSearch);
            query = query.Where(i =>
                i.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                i.FolderName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (numeric && i.Gid == gidSearch));
        }

        List<GalleryItemViewModel> list = _selectedSort.Value switch
        {
            LibrarySort.RecentlyRead => query
                // Never-read works fall back to their folder time, so a fresh
                // download shows up first instead of at the very bottom.
                .OrderByDescending(i => i.LastReadUtc ?? i.Info.LastWriteUtc)
                .ThenByDescending(i => i.Info.LastWriteUtc)
                .ThenBy(i => i.Title, StringComparer.CurrentCulture)
                .ToList(),
            LibrarySort.RecentlyModified => query
                .OrderByDescending(i => i.Info.LastWriteUtc)
                .ThenBy(i => i.Title, StringComparer.CurrentCulture)
                .ToList(),
            LibrarySort.Title => query
                .OrderBy(i => i.Title, StringComparer.CurrentCulture)
                .ThenBy(i => i.Gid ?? long.MaxValue)
                .ToList(),
            LibrarySort.PageCount => query
                .OrderByDescending(i => i.PageCount)
                .ThenBy(i => i.Title, StringComparer.CurrentCulture)
                .ToList(),
            LibrarySort.Gid => query
                .OrderBy(i => i.Gid ?? long.MaxValue)
                .ThenBy(i => i.Title, StringComparer.CurrentCulture)
                .ToList(),
            _ => query.ToList(),
        };

        Galleries = list;
        FilteredCount = list.Count;
        if (_all.Count > 0)
        {
            StatusText = list.Count == _all.Count
                ? $"{_all.Count:N0} 个作品 · {TotalPageCount:N0} 页"
                : $"{list.Count:N0} / {_all.Count:N0} 个作品 · {TotalPageCount:N0} 页";
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadLibraryAsync(forceRefresh: true, useCacheFirst: false);

    [RelayCommand]
    private void CancelScan() => _scanCancellation?.Cancel();

    [RelayCommand]
    private void ChooseFolder() => ChooseFolderRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenGallery(GalleryItemViewModel? item)
    {
        if (item is null) return;
        if (item.PageCount == 0)
        {
            OpenDetailsRequested?.Invoke(this, item);
            return;
        }

        OpenGalleryRequested?.Invoke(this, item);
    }

    [RelayCommand]
    private void OpenDetails(GalleryItemViewModel? item)
    {
        if (item is null) return;
        OpenDetailsRequested?.Invoke(this, item);
    }

    [RelayCommand]
    private void OpenVersions(GalleryItemViewModel? item)
    {
        if (item is null || !item.HasMultipleVersions) return;
        OpenVersionsRequested?.Invoke(this, item);
    }

    [RelayCommand]
    private void OpenFolder(GalleryItemViewModel? item)
    {
        if (item is null) return;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + item.FolderPath + "\"",
                UseShellExecute = true,
            };
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            _services.Log.Warn("Open folder failed: " + item.FolderPath, ex);
        }
    }

    [RelayCommand]
    private void ToggleFavorite(GalleryItemViewModel? item)
    {
        if (item is null) return;
        _services.Progress.ToggleFavorite(item.WorkId);
        item.ApplyProgress(GalleryItemViewModel.LoadProgress(_services.Progress, item.Info));
        _services.Progress.Save(out _);
        ApplyFilterIfNeeded();
    }

    [RelayCommand]
    private void MarkRead(GalleryItemViewModel? item)
    {
        if (item is null || item.PageCount == 0) return;
        _services.Progress.MarkRead(item.WorkId, item.PageCount);
        item.ApplyProgress(GalleryItemViewModel.LoadProgress(_services.Progress, item.Info));
        _services.Progress.Save(out _);
        ApplyFilterIfNeeded();
    }

    [RelayCommand]
    private void MarkUnread(GalleryItemViewModel? item)
    {
        if (item is null) return;
        _services.Progress.MarkUnread(item.WorkId);
        item.ApplyProgress(GalleryItemViewModel.LoadProgress(_services.Progress, item.Info));
        _services.Progress.Save(out _);
        ApplyFilterIfNeeded();
    }

    [RelayCommand]
    private void ClearProgress(GalleryItemViewModel? item)
    {
        if (item is null) return;
        _services.Progress.Clear(item.WorkId);
        item.ApplyProgress(null);
        _services.Progress.Save(out _);
        ApplyFilterIfNeeded();
    }

    /// <summary>
    /// Filters whose membership depends on reading state; toggling them must
    /// rebuild the projection.
    /// </summary>
    private bool FilterDependsOnProgress =>
        _selectedFilter.Value is LibraryFilter.Reading or LibraryFilter.Unread or LibraryFilter.Completed;

    private void ApplyFilterIfNeeded()
    {
        if (FilterDependsOnProgress || _selectedFilter.Value == LibraryFilter.Favorite)
        {
            ApplyFilter();
            GalleryListRebalanced?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Refreshes one card after the reader closes. Deliberately does not re-sort
    /// <see cref="LibrarySort.RecentlyRead"/>: replacing the item list rebuilds
    /// every ItemsRepeater container and loses the scroll position. Ordering is
    /// refreshed on the next explicit refresh/sort change instead.
    /// </summary>
    public void RefreshItemProgress(GalleryItemViewModel item)
    {
        item.ApplyProgress(GalleryItemViewModel.LoadProgress(_services.Progress, item.Info));
        ApplyFilterIfNeeded();
    }

    private void RequestListReset() => GalleryListReplaced?.Invoke(this, EventArgs.Empty);

    private bool ShouldShowWelcome => ResolveSources().Count == 0;

    public void ReportError(string message)
    {
        StatusText = message;
        _services.Log.Warn(message);
    }

    public bool OpenById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        GalleryItemViewModel? item = _all.FirstOrDefault(i =>
            string.Equals(i.StableId, id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.WorkId, id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.Gid?.ToString(), id, StringComparison.OrdinalIgnoreCase));
        if (item is null) return false;
        OpenGalleryCommand.Execute(item);
        return true;
    }

    /// <summary>Marks a copy as the one this work opens by default.</summary>
    public void SetPreferredSource(GalleryItemViewModel item, GalleryInfo version)
    {
        if (string.IsNullOrEmpty(version.SourceId))
        {
            item.SetPrimary(version, preferred: false);
            return;
        }

        _services.Settings.PreferredSourceByWork[item.WorkId] = version.SourceId;
        _services.TrySaveSettings(out _);
        item.SetPrimary(version, preferred: true);

        // Keep the merged ordering consistent for the next rebuild.
        ApplyFilterIfNeeded();
    }

    public async Task<string?> SetRootAsync(string path)
    {
        string? error = ValidateSourcePath(path, out string full);
        if (error is not null) return error;

        await StopScanAsync();
        _services.Settings.Sources.Clear();
        _services.Settings.AddSource(full);
        _services.Settings.LibraryRoot = null;
        _services.TrySaveSettings(out string? saveError);

        _all.Clear();
        Galleries = [];
        FilteredCount = 0;
        TotalGalleryCount = 0;
        TotalPageCount = 0;
        ShowWelcome = false;
        UpdateSourceDisplay();
        await LoadLibraryAsync(forceRefresh: false, useCacheFirst: true);
        return saveError;
    }

    public async Task<string?> AddSourceAsync(string path)
    {
        string? error = ValidateSourcePath(path, out string full);
        if (error is not null) return error;

        LibrarySource? existing = _services.Settings.Sources.FirstOrDefault(s =>
            string.Equals(s.RootPath, full, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && existing.Enabled)
        {
            return null;
        }

        await StopScanAsync();
        _services.Settings.AddSource(full);
        _services.TrySaveSettings(out string? saveError);
        UpdateSourceDisplay();
        await LoadLibraryAsync(forceRefresh: false, useCacheFirst: true);
        return saveError;
    }

    /// <summary>Applies edited source settings (add/remove/enable) and rescans.</summary>
    public async Task ReloadSourcesAsync()
    {
        await StopScanAsync();
        _services.TrySaveSettings(out _);
        _all.Clear();
        Galleries = [];
        FilteredCount = 0;
        UpdateSourceDisplay();
        if (ResolveSources().Count == 0)
        {
            ShowWelcome = true;
            return;
        }

        ShowWelcome = false;
        await LoadLibraryAsync(forceRefresh: false, useCacheFirst: true);
    }

    /// <summary>Drops the scan cache of every source (files are never touched).</summary>
    public void ClearCaches()
    {
        foreach (LibrarySource source in _services.Settings.Sources)
        {
            _services.CacheStore.Delete(source.Id);
        }

        try
        {
            if (File.Exists(_services.Paths.LegacyLibraryCacheFile))
            {
                File.Delete(_services.Paths.LegacyLibraryCacheFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Warn("Clear legacy cache failed", ex);
        }
    }

    /// <summary>Checks that a folder can be used as a source without touching settings.</summary>
    public string? ValidateSource(string path) => ValidateSourcePath(path, out _);

    /// <summary>Drops the current projection (all sources removed).</summary>
    public void ClearLibraryView()
    {
        _all.Clear();
        Galleries = [];
        FilteredCount = 0;
        TotalGalleryCount = 0;
        TotalPageCount = 0;
        RootDisplay = "未配置数据源";
        ShowWelcome = true;
        StatusText = "未配置数据源";
        RebuildLocalIndex();
    }

    private string? ValidateSourcePath(string path, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return "目录不存在";
        }

        full = Path.GetFullPath(path);
        if (IsUnder(full, _services.Paths.BaseDirectory) || IsUnder(_services.Paths.BaseDirectory, full))
        {
            return "应用数据目录不能与资料库目录互相包含";
        }

        return null;
    }

    private async Task StopScanAsync()
    {
        _scanCancellation?.Cancel();
        for (int i = 0; i < 100 && IsScanning; i++)
        {
            await Task.Delay(50);
        }
    }

    private static bool IsUnder(string candidate, string root)
    {
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _searchTimer.Stop();
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Format;
using EhViewerForPc.Core.Scanning;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.Services;

namespace EhViewerForPc.ViewModels;

public sealed record SortOption(LibrarySort Value, string Label);

public sealed record LanguageOption(string? Value, string Label);

/// <summary>
/// Library screen state. Explicit filtering replaces the projection; live
/// downloads update affected cards and reconcile the visible collection in place.
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
    private Task? _libraryLoadTask;
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
    private IReadOnlyList<LibrarySource> _availableSources = [];
    private readonly Dictionary<string, string> _loadedSourceRoots = new(StringComparer.Ordinal);
    private Dictionary<string, IReadOnlyList<GalleryInfo>>? _scanFolderUpdates;

    public MainViewModel(AppServices services)
    {
        _services = services;
        Deletion = new(services);
        _services.LocalDeletionPreparing += PrepareLocalDeletionAsync;
        _services.LocalCopyDeleted += OnLocalCopyDeleted;
        _services.LocalCopyChecked += OnLocalCopyChecked;
        _services.LocalReaderPagesValidated += OnLocalReaderPagesValidated;
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
            new FilterOption(LibraryFilter.Duplicates, "重复"),
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
    public event EventHandler? ChooseFolderRequested;
    public event EventHandler? GalleryListReplaced;
    public event EventHandler? GalleryListRebalanced;

    /// <summary>Raised when the gid → local-copy index changes (scan, reload, clear).</summary>
    public event EventHandler? LocalIndexChanged;

    public AppServices Services => _services;
    public LocalCopyDeletionViewModel Deletion { get; }

    private async Task PrepareLocalDeletionAsync(CancellationToken token)
    {
        CancelScanNow();
        if (_libraryLoadTask is { } load) await load.WaitAsync(token);
        token.ThrowIfCancellationRequested();
    }

    private void OnLocalCopyDeleted(object? sender, string folder)
    {
        if (_disposed) return;
        var paths = _all.SelectMany(item => item.Versions).Select(copy => copy.FolderPath)
            .Where(path => PathIdentity.Comparer.Equals(path, folder) || PathIdentity.IsDescendantOf(path, folder))
            .Distinct(PathIdentity.Comparer).ToArray();
        foreach (string path in paths)
        {
            _queuedCopies.Remove(path);
            ApplyDownloadedCopies([], path);
        }
        if (_all.Count == 0) StatusText = "本地库暂无作品";
    }


    [RelayCommand]
    private async Task ContinueDownloadAsync(GalleryItemViewModel? item)
    {
        if (item is null || !item.CanContinueDownload) return;
        StatusText = "正在读取缺页下载信息…";
        try
        {
            var task = await _services.ContinueLocalDownloadAsync(item.Info, _lifetime.Token);
            if (_disposed) return;
            StatusText = task is null ? "页面已完整，无需补页" : "已加入补页任务 · " + task.FolderPath;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_disposed) StatusText = "无法继续下载：" + error.Message; }
    }

    /// <summary>True when this gallery id already exists in a local source.</summary>
    public bool HasLocalCopy(long gid)
    {
        lock (_byGid) return _byGid.TryGetValue(gid, out var item) && item.PageCount > 0 && !item.IsIncomplete && Directory.Exists(item.FolderPath);
    }

    public bool TryGetLocalCopy(long gid, out GalleryItemViewModel? item)
    {
        lock (_byGid)
        {
            if (_byGid.TryGetValue(gid, out item) && item.PageCount > 0 && !item.IsIncomplete && Directory.Exists(item.FolderPath)) return true;
            item = null; return false;
        }
    }

    public bool TryGetAnyLocalCopy(long gid, out GalleryItemViewModel? item)
    {
        lock (_byGid) return _byGid.TryGetValue(gid, out item) && item.PageCount > 0 && Directory.Exists(item.FolderPath);
    }

    /// <summary>Snapshot of every work, independent of the current filter/search.</summary>
    public IReadOnlyList<GalleryItemViewModel> SnapshotAllWorks() => _all.ToArray();

    /// <summary>Rebuilds the gid index so online listings can show "已在本机".</summary>
    public void RebuildLocalIndex()
    {
        var map = new Dictionary<long, GalleryItemViewModel>();
        foreach (GalleryItemViewModel item in _all)
        {
            if (item.Gid is long gid && (!map.TryGetValue(gid, out var previous) ||
                (previous.IsIncomplete && !item.IsIncomplete) || (previous.IsIncomplete == item.IsIncomplete && previous.PageCount < item.PageCount))) map[gid] = item;
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
                OnPropertyChanged(nameof(CanFillMissingPages));
                FillMissingPagesCommand.NotifyCanExecuteChanged();
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

    public Task InitializeAsync() => LoadLibraryAsync(forceRefresh: false);

    /// <summary>目录存在性检查可能等待网络盘或休眠磁盘，后台只访问数据源快照。</summary>
    private Task<LibrarySource[]> ResolveSourcesAsync(CancellationToken token)
    {
        var sources = _services.Settings.Sources.Where(source => source.Enabled).Select(source => source.Clone()).ToArray();
        if (sources.Length == 0) return Task.FromResult(sources);

        // 单次系统目录检查不可取消，取消后允许界面继续，旧检查只持有快照。
        return Task.Run(() => sources.Where(source =>
        {
            token.ThrowIfCancellationRequested();
            return source.Exists;
        }).ToArray(), token).WaitAsync(token);
    }

    private void UpdateSourceDisplay(IReadOnlyList<LibrarySource> sources)
    {
        _availableSources = sources;
        SourceCount = sources.Count;
        RootDisplay = sources.Count switch
        {
            0 => "未配置数据源",
            1 => sources[0].RootPath,
            _ => $"{sources.Count} 个数据源 · " + string.Join("、", sources.Select(s => s.DisplayName)),
        };
    }

    private Task LoadLibraryAsync(bool forceRefresh)
    {
        if (_disposed) return Task.CompletedTask;
        if (_libraryLoadTask is { IsCompleted: false } load) return load;
        return _libraryLoadTask = LoadLibraryCoreAsync(forceRefresh);
    }

    private async Task LoadLibraryCoreAsync(bool forceRefresh)
    {
        _scanCancellation?.Dispose();
        _scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken token = _scanCancellation.Token;

        _scanFolderUpdates = new(PathIdentity.Comparer);
        if (!forceRefresh)
            foreach (var copy in _all.SelectMany(item => item.Versions))
                if (!copy.IsQueuedDownload && !_loadedSourceRoots.ContainsKey(copy.SourceId)) _scanFolderUpdates[copy.FolderPath] = [copy];

        try
        {
            IReadOnlyList<LibrarySource> sources = await ResolveSourcesAsync(token);
            token.ThrowIfCancellationRequested();
            if (_scanFolderUpdates is { Count: > 0 } updates)
            {
                var importedSourceIds = updates.Values.SelectMany(copies => copies).Select(copy => copy.SourceId).ToHashSet();
                sources = sources.Concat(_availableSources.Where(source => importedSourceIds.Contains(source.Id))).DistinctBy(source => source.Id).ToArray();
            }
            UpdateSourceDisplay(sources);
            ShowWelcome = sources.Count == 0;
            foreach (string id in _loadedSourceRoots.Keys.Where(id => !sources.Any(source => source.Id == id)).ToArray())
                _loadedSourceRoots.Remove(id);
            if (sources.Count == 0)
            {
                ApplyScanGalleries([]);
                StatusText = _services.Settings.Sources.Any(s => s.Enabled)
                    ? "已配置的数据源目录不可用，请检查磁盘是否已连接"
                    : "请选择 EhViewer 下载目录";
                return;
            }

            bool qualifyIds = sources.Count > 1;
            var retained = sources.Where(source => !forceRefresh && _loadedSourceRoots.TryGetValue(source.Id, out var root) &&
                PathIdentity.Comparer.Equals(root, source.RootPath)).ToDictionary(source => source.Id, StringComparer.Ordinal);
            var galleries = _all.SelectMany(item => item.Versions).Where(copy => !copy.IsQueuedDownload && retained.ContainsKey(copy.SourceId)).ToList();
            foreach (var copy in galleries) copy.SourceName = retained[copy.SourceId].DisplayName;
            var pendingSources = sources.Where(source => !retained.ContainsKey(source.Id)).ToArray();
            var (cached, pending) = await Task.Run(() =>
            {
                var cached = new List<(LibrarySource Source, List<GalleryInfo> Galleries)>();
                var pending = new List<(LibrarySource Source, LibraryCache Cache)>();
                foreach (LibrarySource source in pendingSources)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        LibraryCache cache = _services.CacheStore.Load(source.Id, source.RootPath, out string? error);
                        if (!forceRefresh && error is null && cache.SavedUtc != default && cache.IsCompleteSnapshot)
                            cached.Add((source, LibraryScanner.FromCacheOnly(cache, source.RootPath, source.Id, source.DisplayName, qualifyIds)));
                        else pending.Add((source, cache));
                    }
                    catch (Exception ex)
                    {
                        _services.Log.Warn("Cache load failed: " + source.RootPath, ex);
                        pending.Add((source, new LibraryCache { RootPath = source.RootPath }));
                    }
                }

                return (cached, pending);
            }, token);

            token.ThrowIfCancellationRequested();
            foreach (var (source, copies) in cached)
            {
                _loadedSourceRoots[source.Id] = source.RootPath;
                galleries.AddRange(copies);
            }
            if (!forceRefresh) ApplyScanGalleries(galleries);
            if (pending.Count == 0) return;

            IsScanning = true;
            ScanPercent = 0;
            ScanPhase = "枚举目录";
            int cacheHits = 0;
            double scanSeconds = 0;

            for (int i = 0; i < pending.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var (source, cache) = pending[i];
                int index = i;
                int total = pending.Count;
                string name = source.DisplayName;
                DateTime started = DateTime.UtcNow;

                var progress = new Progress<ScanProgress>(p => OnScanProgress(p, index, total, name));
                ScanResult result = await Task.Run(
                    () => _services.Scanner.Scan(
                        source.RootPath,
                        cache,
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
                var currentSource = _services.Settings.Sources.Find(item => item.Id == source.Id &&
                    PathIdentity.Comparer.Equals(item.RootPath, source.RootPath));
                if (currentSource is not null) currentSource.LastScanUtc = DateTime.UtcNow.ToString("O");
                string? cacheError = await Task.Run(() => { _services.CacheStore.Save(source.Id, result.Cache, out var failure, started); return failure; }, token);
                if (cacheError is not null) _services.Log.Warn("Cache save failed: " + cacheError);
            }

            token.ThrowIfCancellationRequested();
            ApplyScanGalleries(galleries);
            foreach (var (source, _) in pending) _loadedSourceRoots[source.Id] = source.RootPath;
            if (forceRefresh) _services.Settings.LastScanCompletedUtc = DateTime.UtcNow.ToString("O");
            _services.TrySaveSettings(out _);

            string sourceText = sources.Count > 1 ? $"{sources.Count} 个数据源 · " : string.Empty;
            StatusText = $"{sourceText}{TotalGalleryCount:N0} 个作品 · {TotalPageCount:N0} 页 · " +
                         $"{scanSeconds:F1}s（缓存 {cacheHits:N0}）";
            ScanPercent = 100;
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) StatusText = "扫描已取消";
        }
        catch (Exception ex)
        {
            _services.Log.Error("Library scan failed", ex);
            if (!_disposed) StatusText = "扫描失败：" + ex.Message;
        }
        finally
        {
            _scanFolderUpdates = null;
            if (!_disposed)
            {
                ScanPhase = string.Empty;
                IsScanning = false;
            }
        }
    }

    private void ApplyScanGalleries(IReadOnlyList<GalleryInfo> galleries)
    {
        if (_scanFolderUpdates is { Count: > 0 } updates)
        {
            // 全库扫描可能早于刚落盘的文件。保留扫描期间发布的定向更新。
            var enabled = _services.Settings.Sources.Where(source => source.Enabled).Select(source => source.Id).ToHashSet(StringComparer.Ordinal);
            galleries = galleries.Where(copy => !updates.ContainsKey(copy.FolderPath))
                .Concat(updates.Values.SelectMany(copies => copies).Where(copy => enabled.Contains(copy.SourceId))).ToArray();
        }
        ApplyGalleries(galleries);
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

    private void ApplyGalleries(IReadOnlyList<GalleryInfo> galleries)
    {
        if (_queuedCopies.Count > 0)
        {
            var existingFolders = galleries.Select(copy => copy.FolderPath).ToHashSet(PathIdentity.Comparer);
            var enabledSources = _services.Settings.Sources.Where(source => source.Enabled).Select(source => source.Id).ToHashSet(StringComparer.Ordinal);
            galleries = [..galleries, .._queuedCopies.Values.Where(copy => enabledSources.Contains(copy.SourceId) && !existingFolders.Contains(copy.FolderPath))];
        }
        List<GalleryGroup> groups = LibraryMerger.Group(galleries, PreferredSourceOf);
        var previousItems = _all.ToDictionary(i => i.StableId, PathIdentity.Comparer);
        var items = new List<GalleryItemViewModel>(groups.Count);
        foreach (GalleryGroup group in groups)
        {
            ReadingProgress? progress = GalleryItemViewModel.LoadProgress(_services.Progress, group.Primary);
            GalleryItemViewModel item;
            if (previousItems.TryGetValue(group.Primary.StableId, out var previous))
            {
                item = previous;
                item.UpdateGroup(group);
                item.ApplyProgress(progress);
            }
            else item = new GalleryItemViewModel(group, CreateCoverRequest, progress);
            item.IsPrimaryVersionPreferred = IsExplicitlyPreferred(group);
            items.Add(item);
        }

        _all.Clear();
        _all.AddRange(items);
        TotalGalleryCount = _all.Count;
        TotalPageCount = _all.Sum(i => (long)i.PageCount);
        ShowWelcome = SourceCount == 0 && _all.Count == 0;
        RebuildLanguageOptions();

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

        // A fresh scan rebuilds every card, so the download overlay has to be
        // re-projected; otherwise running tasks would look idle until the next
        // engine event.
        ApplyDownloadStates(_services.Downloads.Tasks);
    }

    private IImageRequest CreateCoverRequest(GalleryInfo copy) => copy.IsQueuedDownload
        ? NullImageRequest.Instance : new CoverImageRequest(copy, _services.Covers);

    public void RefreshAllProgress()
    {
        foreach (var item in _all) item.ApplyProgress(GalleryItemViewModel.LoadProgress(_services.Progress, item.Info));
    }

    public void RefreshWorkProgress(string workId)
    {
        bool found = false;
        foreach (var item in _all)
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(item.WorkId, workId)) continue;
            item.ApplyProgress(GalleryItemViewModel.LoadProgress(_services.Progress, item.Info));
            found = true;
        }
        if (found && FilterDependsOnProgress) ApplyFilter(updateInPlace: true);
    }

    public async Task ReloadAfterRelocationAsync(string oldRoot, string newRoot, bool movedFiles = true)
    {
        var previous = _all.SelectMany(i => i.Versions).Where(g => g.Gid is null && PathIdentity.IsDescendantOf(g.FolderPath, oldRoot))
            .GroupBy(g => Path.GetRelativePath(oldRoot, g.FolderPath), PathIdentity.Comparer)
            .ToDictionary(g => g.Key, g => g.First(), PathIdentity.Comparer);
        await StopScanAsync();
        while (IsScanning) await Task.Delay(50, _lifetime.Token);
        CancellationToken token = _lifetime.Token;
        var active = await ResolveSourcesAsync(token);
        token.ThrowIfCancellationRequested();
        UpdateSourceDisplay(active);
        var retained = _all.SelectMany(i => i.Versions)
            .Where(g => (!movedFiles || !PathIdentity.IsDescendantOf(g.FolderPath, oldRoot)) && active.Any(s => s.Id == g.SourceId)).ToList();
        foreach (var gallery in retained) gallery.SourceName = active.First(s => s.Id == gallery.SourceId).DisplayName;
        var indexed = retained.Select(g => g.SourceId).ToHashSet(StringComparer.Ordinal);
        foreach (var source in active.Where(s => PathIdentity.Comparer.Equals(s.RootPath, newRoot) || PathIdentity.IsDescendantOf(s.RootPath, newRoot) || !indexed.Contains(s.Id)))
        {
            var scanned = await Task.Run(() =>
            {
                var cache = _services.CacheStore.Load(source.Id, source.RootPath, out _);
                var result = _services.Scanner.Scan(source.RootPath, cache, false, null, token, source.Id, source.DisplayName, active.Length > 1);
                _services.CacheStore.Save(source.Id, result.Cache, out _);
                return result.Galleries;
            });
            retained.AddRange(scanned);
        }
        ApplyGalleries(retained);
        _loadedSourceRoots.Clear();
        foreach (var source in active) _loadedSourceRoots[source.Id] = source.RootPath;
        foreach (var gallery in _all.SelectMany(i => i.Versions))
        {
            if (!movedFiles || gallery.Gid is not null || !PathIdentity.IsDescendantOf(gallery.FolderPath, newRoot) ||
                !previous.TryGetValue(Path.GetRelativePath(newRoot, gallery.FolderPath), out var old)) continue;
            var progress = _services.Progress.Get(old.WorkId) ?? _services.Progress.Get(old.StableId);
            if (progress is not null)
            {
                _services.Progress.Set(gallery.WorkId, progress);
                if (gallery.WorkId != old.WorkId) _services.Progress.Clear(old.WorkId);
                if (gallery.WorkId != old.StableId) _services.Progress.Clear(old.StableId);
            }
            _services.Works.MoveIdentity(old.WorkId, gallery.WorkId);
            if (_services.Settings.PreferredSourceByWork.Remove(old.WorkId, out string? sourceId) && sourceId is not null) _services.Settings.PreferredSourceByWork[gallery.WorkId] = sourceId;
        }
        RefreshAllProgress();
        _services.TrySaveProgress(out _);
        _services.TrySaveSettings(out _);
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
        => ApplyFilter(updateInPlace: false);

    private void ApplyFilter(bool updateInPlace)
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
            case LibraryFilter.Duplicates:
                query = query.Where(i => i.HasMultipleVersions);
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

        if (updateInPlace) ReconcileVisibleGalleries(list);
        else Galleries = new ObservableCollection<GalleryItemViewModel>(list);
        FilteredCount = list.Count;
        OnPropertyChanged(nameof(CanFillMissingPages));
        FillMissingPagesCommand.NotifyCanExecuteChanged();
        if (_all.Count > 0)
        {
            StatusText = list.Count == _all.Count
                ? $"{_all.Count:N0} 个作品 · {TotalPageCount:N0} 页"
                : $"{list.Count:N0} / {_all.Count:N0} 个作品 · {TotalPageCount:N0} 页";
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadLibraryAsync(forceRefresh: true);

    /// <summary>Full rescan of every source; exposed by settings (not the toolbar).</summary>
    public Task RescanAllAsync() => LoadLibraryAsync(forceRefresh: true);

    /// <summary>Cancels a running scan without clearing the current projection.</summary>
    public void CancelScanNow() => _scanCancellation?.Cancel();

    /// <summary>
    /// Projects download-engine state onto library cards by task folder. A task
    /// never maps to a card by gid alone, so two copies in different folders keep
    /// their own progress.
    /// </summary>
    public void ApplyDownloadStates(IReadOnlyCollection<Core.Download.DownloadTask> tasks)
    {
        var byFolder = new Dictionary<string, Core.Download.DownloadTask>(
            Core.Storage.PathIdentity.Comparer);
        foreach (Core.Download.DownloadTask task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.FolderPath)) continue;
            try { byFolder[AppSettings.NormalizeRoot(task.FolderPath)] = task; }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }

        foreach (GalleryItemViewModel item in _all)
        {
            Core.Download.DownloadTask? match = null;
            bool isPrimary = false;
            foreach (GalleryInfo version in item.Versions)
            {
                if (!byFolder.TryGetValue(version.FolderPath, out Core.Download.DownloadTask? found)) continue;
                bool versionIsPrimary = string.Equals(version.StableId, item.StableId, StringComparison.OrdinalIgnoreCase);
                if (match is null || versionIsPrimary)
                {
                    match = found;
                    isPrimary = versionIsPrimary;
                }

                if (versionIsPrimary) break;
            }

            item.ApplyDownload(match, isPrimary);
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCancellation?.Cancel();

    [RelayCommand]
    private void ChooseFolder() => ChooseFolderRequested?.Invoke(this, EventArgs.Empty);

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
            EhViewerForPc.Services.PlatformLauncher.Open(item.FolderPath);
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
        _services.TrySaveProgress(out _);
        ApplyFilterIfNeeded();
    }

    [RelayCommand]
    private void MarkRead(GalleryItemViewModel? item)
    {
        if (item is null || item.PageCount == 0) return;
        _services.Progress.MarkRead(item.WorkId, item.PageCount);
        item.ApplyProgress(GalleryItemViewModel.LoadProgress(_services.Progress, item.Info));
        _services.TrySaveProgress(out _);
        ApplyFilterIfNeeded();
    }

    [RelayCommand]
    private void MarkUnread(GalleryItemViewModel? item)
    {
        if (item is null) return;
        _services.Progress.MarkUnread(item.WorkId);
        item.ApplyProgress(GalleryItemViewModel.LoadProgress(_services.Progress, item.Info));
        _services.TrySaveProgress(out _);
        ApplyFilterIfNeeded();
    }

    [RelayCommand]
    private void ClearProgress(GalleryItemViewModel? item)
    {
        if (item is null) return;
        _services.Progress.ResetReading(item.WorkId);
        item.ApplyProgress(_services.Progress.Get(item.WorkId));
        _services.TrySaveProgress(out _);
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
        if (version.IsQueuedDownload) return;
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
        _services.Settings.AddSource(full);
        _services.Settings.LibraryRoot = null;
        _services.TrySaveSettings(out string? saveError);

        ShowWelcome = false;
        await LoadLibraryAsync(forceRefresh: false);
        return saveError;
    }

    public async Task<string?> AddSourceAsync(string path)
    {
        string? error = ValidateSourcePath(path, out string full);
        if (error is not null) return error;

        LibrarySource? existing = _services.Settings.Sources.FirstOrDefault(s =>
            EhViewerForPc.Core.Storage.PathIdentity.Comparer.Equals(s.RootPath, full));
        if (existing is not null && existing.Enabled)
        {
            return null;
        }

        await StopScanAsync();
        _services.Settings.AddSource(full);
        _services.TrySaveSettings(out string? saveError);
        await LoadLibraryAsync(forceRefresh: false);
        return saveError;
    }

    /// <summary>保留已载入的来源，只加载新启用或路径变化的数据源。</summary>
    public async Task ReloadSourcesAsync()
    {
        await StopScanAsync();
        _services.TrySaveSettings(out _);
        ShowWelcome = false;
        await LoadLibraryAsync(forceRefresh: false);
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
        if (_libraryLoadTask is { } load) await load;
    }

    private static bool IsUnder(string candidate, string root)
    {
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedRoot, EhViewerForPc.Core.Storage.PathIdentity.Comparison);
    }

    public void Dispose()
    {
        _services.LocalCopyChecked -= OnLocalCopyChecked;
        _services.LocalReaderPagesValidated -= OnLocalReaderPagesValidated;
        _services.LocalDeletionPreparing -= PrepareLocalDeletionAsync;
        _services.LocalCopyDeleted -= OnLocalCopyDeleted;
        Deletion.Dispose();
        if (_disposed) return;
        _disposed = true;
        _searchTimer.Stop();
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

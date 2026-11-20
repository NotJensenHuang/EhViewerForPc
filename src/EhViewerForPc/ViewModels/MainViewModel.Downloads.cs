using System.Collections.ObjectModel;
using System.Globalization;
using EhViewerForPc.Core.Download;
using EhViewerForPc.Core.Format;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Scanning;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EhViewerForPc.ViewModels;

public sealed partial class MainViewModel
{
    private readonly Dictionary<string, GalleryInfo> _queuedCopies = new(PathIdentity.Comparer);

    internal void SyncQueuedDownloadCopies(IReadOnlyCollection<DownloadTask> tasks)
    {
        if (_disposed) return;
        var retained = new HashSet<string>(PathIdentity.Comparer);
        foreach (var task in tasks)
        {
            if (task.FillMissingOnly || !ShouldImportDownload(task) || !TryGetDownloadPaths(task, out var root, out var folder)) continue;
            var source = FindDownloadSource(root);
            if (source is { Enabled: false }) continue;
            retained.Add(folder);
            if (_queuedCopies.ContainsKey(folder) || task.IsFinished || FindLocalCopy(folder) is not null) continue;

            source = IncludeDownloadSource(root, source);
            string workId = task.Gid.ToString(CultureInfo.InvariantCulture);
            var (languages, tags) = GalleryTags.Parse(task.DisplayTitle);
            var copy = new GalleryInfo
            {
                StableId = "queued-" + StableIdFactory.PathHash(folder),
                WorkId = workId,
                SourceId = source.Id,
                SourceName = source.DisplayName,
                Gid = task.Gid,
                Token = task.Token,
                Title = task.DisplayTitle,
                FolderName = Path.GetFileName(folder),
                FolderPath = folder,
                ExpectedPages = task.PageCount > 0 ? task.PageCount : null,
                IsIncomplete = true,
                IsQueuedDownload = true,
                Languages = languages,
                Tags = tags,
                LastWriteUtc = DateTime.TryParse(task.AddedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var added)
                    ? added.ToUniversalTime() : DateTime.UtcNow,
            };
            _queuedCopies.Add(folder, copy);
            ApplyDownloadedCopies([copy], folder, recordScanUpdate: false);
        }

        foreach (string folder in _queuedCopies.Keys.Where(folder => !retained.Contains(folder)).ToArray())
        {
            _queuedCopies.Remove(folder);
            if (FindLocalCopy(folder) is { IsQueuedDownload: true }) ApplyDownloadedCopies([], folder, recordScanUpdate: false);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFillMissingPages))]
    [NotifyCanExecuteChangedFor(nameof(FillMissingPagesCommand))]
    private bool _isFillingMissingPages;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepairFailures))]
    private string _repairFailureDetails = string.Empty;
    public bool HasRepairFailures => RepairFailureDetails.Length > 0;
    public bool CanFillMissingPages => !_disposed && !IsFillingMissingPages && Galleries.Any(item => item.Versions.Any(info => info.IsIncomplete && !info.IsQueuedDownload));

    [RelayCommand(CanExecute = nameof(CanFillMissingPages))]
    private async Task FillMissingPagesAsync()
    {
        if (!CanFillMissingPages) return;
        var copies = Galleries.SelectMany(item => item.Versions).Where(copy => !copy.IsQueuedDownload).ToArray();
        IsFillingMissingPages = true;
        RepairFailureDetails = string.Empty;
        StatusText = "正在加入缺页任务…";
        try
        {
            var progress = new Progress<LocalRepairProgress>(p => { if (!_disposed && IsFillingMissingPages) StatusText = $"正在加入缺页任务 {p.Processed:N0} / {p.Total:N0}"; });
            var result = await _services.ContinueLocalDownloadsAsync(copies, progress, _lifetime.Token);
            if (_disposed) return;
            RepairFailureDetails = string.Join(Environment.NewLine, result.Failures.Select(f => f.Folder + "：" + f.Message));
            StatusText = $"补页：新增 {result.Added} · 继续 {result.Continued} · 已有 {result.Existing} · 已完整 {result.Complete} · 失败 {result.Failures.Count}";
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error) { if (!_disposed) StatusText = "无法批量补页：" + error.Message; }
        finally { if (!_disposed) IsFillingMissingPages = false; }
    }

    public async Task<bool> ImportDownloadAsync(DownloadTask task, bool persist = false)
    {
        if (_disposed || !ShouldImportDownload(task)) return false;
        CancellationToken token = _lifetime.Token;
        if (!TryGetDownloadPaths(task, out var root, out var folder)) return false;

        var source = FindDownloadSource(root)?.Clone();
        if (source is { Enabled: false } || (source is null && task.FillMissingOnly)) return false;
        bool qualifyStableId = _availableSources.Any(other => !PathIdentity.Comparer.Equals(other.RootPath, root));
        bool validatePages = task.FillMissingOnly || task.State != DownloadState.Running;
        int expected = task.PageCount;
        var result = await Task.Run(() =>
        {
            if (!Directory.Exists(folder)) return null;
            return _services.Scanner.Scan(root, null, cancellationToken: token, sourceId: source?.Id,
                sourceName: source?.DisplayName, qualifyStableId: qualifyStableId, onlyFolder: folder,
                validatePages: validatePages, expectedPages: expected);
        }, token);
        var copy = result?.Galleries.Single();
        if (_disposed || !ShouldImportDownload(task) || !PathIdentity.Comparer.Equals(AppSettings.NormalizeRoot(task.FolderPath), folder) ||
            (!_services.Downloads.IsQueued(task) && task.State != DownloadState.Completed) || copy is not { PageCount: > 0 }) return false;

        LibrarySource? current = FindDownloadSource(root);
        if (current is { Enabled: false } || (source is not null && current?.Id != source.Id)) return false;
        current = IncludeDownloadSource(root, current);

        if (persist && result!.Cache.Entries.TryGetValue(Path.GetRelativePath(root, folder), out var entry))
        {
            string sourceId = current.Id;
            _services.Writes.Schedule("library-copy:" + folder, () =>
            {
                try
                {
                    if (!_services.CacheStore.UpdateFolder(sourceId, root, folder, entry, out var error))
                        _services.Log.Warn("Download library cache update failed: " + error);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    _services.Log.Warn("Download library cache update failed", error);
                }
            });
        }

        if (copy.SourceId.Length == 0)
        {
            copy.SourceId = current.Id;
            copy.SourceName = current.DisplayName;
            if (SourceCount > 1) copy.StableId += "@" + current.Id;
        }
        _queuedCopies.Remove(folder);
        if (FindLocalCopy(folder) is { IsQueuedDownload: false } previous && previous.SourceId == copy.SourceId && previous.Gid == copy.Gid)
        {
            copy.StableId = previous.StableId;
            copy.IsDuplicateGid = previous.IsDuplicateGid;
            if (AppServices.SameLocalPageState(previous, copy) && previous.Token == copy.Token &&
                previous.HasEhviewer == copy.HasEhviewer && previous.HasThumb == copy.HasThumb &&
                PathIdentity.Comparer.Equals(previous.CoverPath, copy.CoverPath) && previous.CoverKind == copy.CoverKind)
                return true;
        }
        ApplyDownloadedCopies([copy], folder);
        return true;
    }

    private bool ShouldImportDownload(DownloadTask task) => task.FillMissingOnly ||
        (_services.Settings.Download.AddAsSource && _services.Settings.Download.RefreshLibraryOnComplete);

    private bool TryGetDownloadPaths(DownloadTask task, out string root, out string folder)
    {
        root = folder = string.Empty;
        if (string.IsNullOrWhiteSpace(task.FolderPath)) return false;
        try
        {
            root = AppSettings.NormalizeRoot(string.IsNullOrEmpty(task.DestinationRoot) ? _services.Downloads.RootPath : task.DestinationRoot);
            folder = AppSettings.NormalizeRoot(task.FolderPath);
            return PathIdentity.IsDescendantOf(folder, root);
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    private LibrarySource? FindDownloadSource(string root) => _services.Settings.Sources.FirstOrDefault(source =>
        PathIdentity.Comparer.Equals(source.RootPath, root));

    private LibrarySource IncludeDownloadSource(string root, LibrarySource? source)
    {
        if (source is null)
        {
            source = _services.Settings.AddSource(root, "下载目录");
            _services.TrySaveSettings(out _);
        }
        if (!_availableSources.Any(existing => existing.Id == source.Id)) UpdateSourceDisplay([.. _availableSources, source.Clone()]);
        return source;
    }

    internal GalleryInfo? FindLocalCopy(string folder)
    {
        folder = AppSettings.NormalizeRoot(folder);
        foreach (var item in _all)
        foreach (var copy in item.Versions)
            if (PathIdentity.Comparer.Equals(copy.FolderPath, folder)) return copy;
        return null;
    }

    internal void ApplyScannedCopy(GalleryInfo copy) => ApplyDownloadedCopies([copy], copy.FolderPath);

    private void OnLocalCopyChecked(object? sender, GalleryInfo copy)
    {
        if (!_disposed) ApplyScannedCopy(copy);
    }

    private void OnLocalReaderPagesValidated(object? sender, (GalleryInfo Copy, PageScanResult Pages) result)
    {
        var (copy, pages) = result;
        if (_disposed || (copy.CorruptPages == pages.CorruptPages && copy.IsIncomplete == pages.IsIncomplete) ||
            !ReferenceEquals(FindLocalCopy(copy.FolderPath), copy)) return;
        copy.CorruptPages = pages.CorruptPages;
        copy.IsIncomplete = pages.IsIncomplete;
        ApplyScannedCopy(copy);
    }

    private void ApplyDownloadedCopies(IReadOnlyList<GalleryInfo> copies, string folder, bool recordScanUpdate = true)
    {
        if (recordScanUpdate && _scanFolderUpdates is not null) _scanFolderUpdates[folder] = copies;
        // 只重新合并受影响的作品；其他卡片、封面请求和可见控件保持原实例。
        var workIds = copies.Select(g => g.WorkId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var affected = _all.Where(item => workIds.Contains(item.WorkId) ||
            item.Versions.Any(v => PathIdentity.Comparer.Equals(v.FolderPath, folder))).ToList();
        var previous = affected.SelectMany(i => i.Versions).ToArray();
        bool languagesChanged = copies.Any(copy => !previous.Any(old =>
            PathIdentity.Comparer.Equals(old.FolderPath, copy.FolderPath) && old.Languages.SequenceEqual(copy.Languages)));
        var groups = LibraryMerger.Group(previous.Where(v => !PathIdentity.Comparer.Equals(v.FolderPath, folder)).Concat(copies), PreferredSourceOf);
        var available = new List<GalleryItemViewModel>(affected);
        var updated = new List<GalleryItemViewModel>(groups.Count);
        foreach (var group in groups)
        {
            var item = available.FirstOrDefault(i => StringComparer.OrdinalIgnoreCase.Equals(i.WorkId, group.WorkId) &&
                i.Versions.Any(v => PathIdentity.Comparer.Equals(v.FolderPath, group.Primary.FolderPath)))
                ?? available.FirstOrDefault(i => StringComparer.OrdinalIgnoreCase.Equals(i.WorkId, group.WorkId) &&
                    i.Versions.Any(v => group.Versions.Any(g => PathIdentity.Comparer.Equals(v.FolderPath, g.FolderPath))));
            var progress = GalleryItemViewModel.LoadProgress(_services.Progress, group.Primary);
            if (item is null) item = new GalleryItemViewModel(group, CreateCoverRequest, progress);
            else
            {
                available.Remove(item);
                item.UpdateGroup(group, preserveCover: true);
                item.ApplyProgress(progress);
            }
            item.IsPrimaryVersionPreferred = IsExplicitlyPreferred(group);
            updated.Add(item);
        }
        int insertion = affected.Count == 0 ? 0 : _all.IndexOf(affected[0]);
        _all.RemoveAll(affected.Contains);
        _all.InsertRange(insertion, updated);
        TotalGalleryCount = _all.Count;
        TotalPageCount = _all.Sum(i => (long)i.PageCount);
        ShowWelcome = SourceCount == 0 && _all.Count == 0;
        if (languagesChanged) RebuildLanguageOptions();
        RebuildLocalIndex();
        ApplyFilter(updateInPlace: true);
        ApplyDownloadStates(_services.Downloads.Tasks);
    }

    private void ReconcileVisibleGalleries(List<GalleryItemViewModel> desired)
    {
        if (Galleries is not ObservableCollection<GalleryItemViewModel> visible)
        {
            Galleries = new ObservableCollection<GalleryItemViewModel>(desired);
            return;
        }
        if (visible.Count == desired.Count && visible.SequenceEqual(desired)) return;
        // ItemsRepeater 的 Move 会短暂清空 DataContext，进度绑定随之归零。
        // 下载更新保留已有顺序；排序只决定新增项位置，完整重排由显式筛选 / 刷新负责。
        var additions = new HashSet<GalleryItemViewModel>(desired);
        for (int i = visible.Count - 1; i >= 0; i--)
            if (!additions.Remove(visible[i])) visible.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            if (additions.Remove(desired[i])) visible.Insert(Math.Min(i, visible.Count), desired[i]);
        }
    }
}

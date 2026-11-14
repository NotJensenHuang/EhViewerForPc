using System.Collections.ObjectModel;
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFillMissingPages))]
    [NotifyCanExecuteChangedFor(nameof(FillMissingPagesCommand))]
    private bool _isFillingMissingPages;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepairFailures))]
    private string _repairFailureDetails = string.Empty;
    public bool HasRepairFailures => RepairFailureDetails.Length > 0;
    public bool CanFillMissingPages => !_disposed && !IsFillingMissingPages && Galleries.Any(item => item.Versions.Any(info => info.IsIncomplete));

    [RelayCommand(CanExecute = nameof(CanFillMissingPages))]
    private async Task FillMissingPagesAsync()
    {
        if (!CanFillMissingPages) return;
        var copies = Galleries.SelectMany(item => item.Versions).ToArray();
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

    public async Task ImportDownloadAsync(DownloadTask task)
    {
        if (!_services.Settings.Download.AddAsSource && !task.FillMissingOnly) return;
        while (IsScanning) await Task.Delay(50, _lifetime.Token);
        string folder = task.FolderPath;
        if (!Directory.Exists(folder)) return;
        string root = string.IsNullOrEmpty(task.DestinationRoot) ? _services.Downloads.RootPath : task.DestinationRoot;
        var source = _services.Settings.Sources.FirstOrDefault(s => PathIdentity.Comparer.Equals(s.RootPath, root));
        if (source is null && task.FillMissingOnly) return;
        if (source is null)
        {
            source = _services.Settings.AddSource(root, "下载目录");
            _services.TrySaveSettings(out _);
        }
        int expected = task.PageCount;
        bool qualifyStableId = ResolveSources().Count > 1;
        var completed = await Task.Run(() =>
        {
            var result = _services.Scanner.Scan(root, null, false, null, _lifetime.Token, source.Id, source.DisplayName, qualifyStableId, onlyFolder: folder);
            foreach (var gallery in result.Galleries)
            {
                if (expected <= 0 || gallery.ExpectedPages == expected) continue;
                var pages = PageFileParser.Scan(gallery.FolderPath, new SpiderHeader { HasPages = true, Pages = expected }, out _);
                gallery.ExpectedPages = expected;
                gallery.IsIncomplete = pages.IsIncomplete;
                gallery.MissingRanges = pages.MissingRanges;
                gallery.HasHeaderPageCountOutdated = pages.HeaderPageCountOutdated;
            }
            return result.Galleries;
        });
        while (IsScanning) await Task.Delay(50, _lifetime.Token);
        if (_disposed || !_services.Settings.Sources.Any(s => s.Id == source.Id && s.Enabled && PathIdentity.Comparer.Equals(s.RootPath, root))) return;
        ApplyDownloadedCopies(completed, folder);
    }

    internal void ApplyScannedCopy(GalleryInfo copy) => ApplyDownloadedCopies([copy], copy.FolderPath);

    private void OnLocalCopyChecked(object? sender, GalleryInfo copy)
    {
        if (!_disposed) ApplyScannedCopy(copy);
    }

    private void ApplyDownloadedCopies(IReadOnlyList<GalleryInfo> copies, string folder)
    {
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
            if (item is null) item = new GalleryItemViewModel(group, info => new CoverImageRequest(info, _services.Covers), progress);
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
        ShowWelcome = ShouldShowWelcome;
        if (languagesChanged) RebuildLanguageOptions();
        UpdateSourceDisplay();
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

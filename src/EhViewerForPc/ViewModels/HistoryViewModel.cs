using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Services;

namespace EhViewerForPc.ViewModels;

/// <summary>Reading history is work-level state, including online and unavailable local works.</summary>
public sealed partial class HistoryViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly MainViewModel _library;
    private bool _disposed;
    public HistoryViewModel(AppServices services, MainViewModel library)
    {
        _services = services;
        _library = library;
        library.LocalIndexChanged += OnLibraryChanged;
        library.GalleryListRebalanced += OnLibraryChanged;
        Reload();
    }
    public IReadOnlyList<HistoryEntryViewModel> Items { get; private set; } = [];
    public bool ShowEmpty => Items.Count == 0;
    public bool HasItems => Items.Count > 0;
    public string CountText => $"{Items.Count} 个作品";
    public string StatusText { get; private set; } = "本机阅读记录：在线与本地共享进度";
    public event EventHandler? OpenLibraryRequested;
    public event EventHandler? OpenSiteHistoryRequested;
    public event EventHandler<EhGallerySummary>? OnlineRequested;

    public void Reload()
    {
        if (_disposed) return;
        var snapshot = _library.SnapshotAllWorks();
        var local = snapshot.GroupBy(i => i.WorkId).ToDictionary(g => g.Key, g => g.OrderBy(i => i.IsIncomplete).ThenByDescending(i => i.PageCount).First());
        foreach (var copy in snapshot) local.TryAdd(copy.StableId, copy);
        var entries = new List<HistoryEntryViewModel>();
        foreach (var pair in _services.Progress.Snapshot())
        {
            if (pair.Value.HiddenFromHistory || pair.Value.LastReadUtc == default) continue;
            local.TryGetValue(pair.Key, out var item);
            var work = _services.Works.Get(item?.WorkId ?? pair.Key);
            if (work is null && item is null) continue;
            work ??= new WorkRecord { WorkId = item!.WorkId, Gid = item.Gid, Title = item.Title, PageCount = item.PageCount };
            entries.Add(new HistoryEntryViewModel(item, work, pair.Value, _services, this));
        }
        Items = entries.GroupBy(e => e.Work.WorkId).Select(g => g.OrderByDescending(e => e.LastReadUtc).First()).OrderByDescending(e => e.LastReadUtc).ToArray();
        StatusText = Items.Count == 0 ? "阅读在线或本地作品后，会自动记录进度" : $"共 {Items.Count} 个作品 · 按最近阅读排序";
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(StatusText));
    }
    [RelayCommand] private void Refresh() => Reload();
    [RelayCommand] private void OpenLibrary() => OpenLibraryRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void OpenSiteHistory() => OpenSiteHistoryRequested?.Invoke(this, EventArgs.Empty);
    internal void Open(HistoryEntryViewModel entry)
    {
        if (entry.Item is { PageCount: > 0 } item && Directory.Exists(item.FolderPath)) _library.OpenGalleryCommand.Execute(item);
        else if (entry.Work.Gid is long gid && entry.Work.Token.Length > 0)
            OnlineRequested?.Invoke(this, new EhGallerySummary { Gid = gid, Token = entry.Work.Token, Title = entry.Title, ThumbUrl = entry.Work.ThumbnailUrl, Pages = entry.PageCount });
        else { StatusText = "原资料库不可用，请连接磁盘后刷新"; OnPropertyChanged(nameof(StatusText)); }
    }
    internal void Remove(HistoryEntryViewModel entry)
    {
        _services.Progress.HideFromHistory(entry.Work.WorkId);
        if (entry.Item is { } item && item.StableId != item.WorkId) _services.Progress.HideFromHistory(item.StableId);
        _services.TrySaveProgress(out _);
        Reload();
    }
    private void OnLibraryChanged(object? sender, EventArgs e) => Reload();
    public void Dispose()
    {
        _disposed = true;
        _library.LocalIndexChanged -= OnLibraryChanged;
        _library.GalleryListRebalanced -= OnLibraryChanged;
    }
}

public sealed partial class HistoryEntryViewModel
{
    private readonly HistoryViewModel _owner;
    private readonly ReadingProgress _progress;
    public HistoryEntryViewModel(GalleryItemViewModel? item, WorkRecord work, ReadingProgress progress, AppServices services, HistoryViewModel owner)
    {
        Item = item;
        Work = work;
        _progress = progress;
        _owner = owner;

        // A local copy is only a usable cover source while its folder exists.
        // Otherwise fall back to the persisted thumbnail (cached by work id, so
        // it survives a restart) instead of showing a dead local request.
        if (item is { PageCount: > 0 } && Directory.Exists(item.FolderPath))
        {
            CoverRequest = item.CoverRequest;
        }
        else
        {
            CoverRequest = services.OnlineImages.CreateThumbnailRequest(
                work.ThumbnailUrl, "work:" + work.WorkId);
        }
    }
    public GalleryItemViewModel? Item { get; }
    public WorkRecord Work { get; }
    public bool CanOpen => Item?.CanRead == true || (Work.Gid is > 0 && Work.Token.Length > 0);
    [RelayCommand(CanExecute = nameof(CanOpen))] private void Open() => _owner.Open(this);
    [RelayCommand] private void Remove() => _owner.Remove(this);
    public string Title => Work.Title;
    public IImageRequest CoverRequest { get; }
    public int PageCount => Item?.PageCount ?? Work.PageCount;
    public string PageCountText => $"{PageCount} 页";
    public string ProgressText => PageCount <= 0 ? "无可读页面" : _progress.Completed ? "已读完" : $"第 {_progress.PageNumber} 页";
    public double ProgressFraction => PageCount <= 0 ? 0 : _progress.Completed ? 1 : Math.Clamp(_progress.PageNumber / (double)PageCount, 0, 1);
    public string ProgressDetail => PageCount <= 0 ? ProgressText : ProgressText + $" / 共 {PageCount} 页";
    public DateTime LastReadUtc => _progress.LastReadUtc;
    public string LastReadText => FormatLastRead(LastReadUtc);
    public string SourceText => Item?.SourceName ?? (Work.Token.Length > 0 ? "在线" : "原资料库不可用");
    public string MetaText => ProgressDetail + " · " + SourceText;
    public string TooltipText => Title + "\n" + MetaText + "\n最近阅读：" + LastReadText;
    private static string FormatLastRead(DateTime? utc)
    {
        if (utc is null) return "未记录";

        DateTime local = utc.Value.ToLocalTime();
        DateTime today = DateTime.Now.Date;
        int days = (int)(today - local.Date).TotalDays;

        if (days <= 0) return "今天 " + local.ToString("HH:mm");
        if (days == 1) return "昨天 " + local.ToString("HH:mm");
        if (days < 7) return days + " 天前";
        return local.Year == DateTime.Now.Year
            ? local.ToString("MM-dd HH:mm")
            : local.ToString("yyyy-MM-dd");
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerReader.Services;

namespace EhViewerReader.ViewModels;

/// <summary>
/// Local reading history. The site has its own history page, but the original
/// desktop workflow is "what did I read on this machine": it is built from the
/// library plus the progress store, so it also covers galleries that are no
/// longer reachable online.
/// </summary>
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

    public ObservableCollection<HistoryEntryViewModel> Items { get; } = [];

    public bool ShowEmpty => Items.Count == 0;

    public bool HasItems => Items.Count > 0;

    public string CountText => Items.Count == 0 ? "暂无阅读记录" : $"{Items.Count} 个作品";

    public string StatusText { get; private set; } =
        "打开任意本地作品后，这里会按最近阅读时间排序显示";

    public event EventHandler? OpenLibraryRequested;

    public event EventHandler? OpenSiteHistoryRequested;

    /// <summary>Rebuilds the list from the current library snapshot and progress.</summary>
    public void Reload()
    {
        if (_disposed) return;

        List<GalleryItemViewModel> works = _library.SnapshotAllWorks()
            .Where(item => !item.IsUnread)
            .OrderByDescending(item => item.LastReadUtc ?? DateTime.MinValue)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Items.Clear();
        foreach (GalleryItemViewModel item in works) Items.Add(new HistoryEntryViewModel(item, this));

        StatusText = works.Count == 0
            ? "还没有阅读记录：打开任意本地作品后会自动记录"
            : $"共 {works.Count} 个作品 · 按最近阅读排序";

        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(StatusText));
    }

    [RelayCommand]
    private void Refresh() => Reload();

    [RelayCommand]
    private void OpenLibrary() => OpenLibraryRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenSiteHistory() => OpenSiteHistoryRequested?.Invoke(this, EventArgs.Empty);

    internal void Open(HistoryEntryViewModel entry)
    {
        // The library page owns the reader/details windows. Bring it up first so
        // its handlers exist even when the user has never visited that tab.
        OpenLibraryRequested?.Invoke(this, EventArgs.Empty);

        if (entry.Item.PageCount == 0)
        {
            _library.OpenDetailsCommand.Execute(entry.Item);
            return;
        }

        _library.OpenGalleryCommand.Execute(entry.Item);
    }

    internal void Remove(HistoryEntryViewModel entry)
    {
        _services.Progress.Clear(entry.Item.WorkId);
        _services.Progress.Clear(entry.Item.StableId);
        entry.Item.ApplyProgress(null);
        _services.Progress.Save(out _);
        Reload();
    }

    private void OnLibraryChanged(object? sender, EventArgs e) => Reload();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _library.LocalIndexChanged -= OnLibraryChanged;
        _library.GalleryListRebalanced -= OnLibraryChanged;
    }
}

/// <summary>One row of the local reading history.</summary>
public sealed partial class HistoryEntryViewModel
{
    private readonly HistoryViewModel _owner;

    public HistoryEntryViewModel(GalleryItemViewModel item, HistoryViewModel owner)
    {
        Item = item;
        _owner = owner;
    }

    public GalleryItemViewModel Item { get; }

    [RelayCommand]
    private void Open() => _owner.Open(this);

    [RelayCommand]
    private void Remove() => _owner.Remove(this);

    public string Title => Item.Title;
    public IImageRequest CoverRequest => Item.CoverRequest;
    public int PageCount => Item.PageCount;
    public string PageCountText => Item.PageCountText;
    public string ProgressText => Item.ProgressText;
    public double ProgressFraction => Item.ProgressFraction;
    public string ProgressDetail => Item.ProgressDetail;
    public string LastReadText => FormatLastRead(Item.LastReadUtc);
    public string SourceText => Item.HasSourceName ? Item.SourceName : "默认数据源";

    public string MetaText
    {
        get
        {
            // ProgressDetail already carries the page count; the source is only
            // interesting when the same work exists in several of them.
            var parts = new List<string>(2) { ProgressDetail };
            if (Item.SourceCount > 1) parts.Add(SourceText);
            return string.Join(" · ", parts);
        }
    }

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

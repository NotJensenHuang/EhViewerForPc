using EhViewerForPc.Core.Download;
using EhViewerForPc.Core.Format;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Scanning;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.Services;

namespace EhViewerForPc.ViewModels;

/// <summary>
/// One library card. A card represents a work, which may exist in several
/// sources (device backups); <see cref="Info"/> is the copy that will be opened.
/// Mutable state is kept to progress/favourite plus the selected copy so the
/// 6,000+ instances stay cheap.
/// </summary>
public sealed class GalleryItemViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private static readonly string[] DerivedProperties =
    [
        nameof(Info), nameof(StableId), nameof(WorkId), nameof(Title), nameof(FolderName),
        nameof(FolderPath), nameof(FolderPathDisplay), nameof(Gid), nameof(GidText),
        nameof(PageCount), nameof(CanRead), nameof(PageCountText), nameof(LanguageText), nameof(HasLanguages),
        nameof(IsIncomplete), nameof(HasMissingPages), nameof(HasZeroByteFiles), nameof(IsEmpty),
        nameof(IsDuplicateGid), nameof(IncompleteText), nameof(MissingText), nameof(ExtensionSummary),
        nameof(ExpectedPagesText), nameof(LastWriteText), nameof(CoverSourceText), nameof(HeaderErrorText),
        nameof(ProgressDetail), nameof(TooltipText), nameof(Details), nameof(CoverRequest),
        nameof(SourceName), nameof(HasSourceName), nameof(IsPrimaryVersionPreferred),
        nameof(VersionText), nameof(VersionBadge), nameof(VersionSummary),
    ];

    private readonly Func<GalleryInfo, IImageRequest> _coverFactory;
    private bool _isFavorite;
    private bool _isCompleted;
    private int _progressPage;
    private double _progressFraction;
    private DateTime? _lastReadUtc;
    private GalleryInfo _info;
    private IImageRequest _coverRequest;
    private DownloadProjection? _download;
    private bool _downloadIsPrimary = true;

    public GalleryItemViewModel(
        GalleryGroup group,
        Func<GalleryInfo, IImageRequest> coverFactory,
        ReadingProgress? progress)
    {
        _coverFactory = coverFactory;
        Versions = group.Versions;
        WorkId = group.WorkId;
        _info = group.Primary;
        _coverRequest = coverFactory(_info);
        ApplyProgress(progress);
    }

    public IReadOnlyList<GalleryInfo> Versions { get; private set; }

    public void UpdateGroup(GalleryGroup group)
    {
        string selected = StableId;
        Versions = group.Versions;
        var primary = IsPrimaryVersionPreferred
            ? Versions.FirstOrDefault(v => v.StableId == selected) ?? group.Primary
            : group.Primary;
        SetPrimary(primary, IsPrimaryVersionPreferred);
        OnPropertyChanged(nameof(SourceCount));
    }
    public string WorkId { get; }

    public GalleryInfo Info => _info;
    public IImageRequest CoverRequest => _coverRequest;

    public bool HasMultipleVersions => Versions.Count > 1;

    public int SourceCount => Versions
        .Select(v => v.SourceId)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    public string VersionBadge => Versions.Count > 1 ? $"×{Versions.Count}" : string.Empty;

    public string VersionText => Versions.Count > 1 ? $"{Versions.Count} 个版本" : "单一版本";

    public string VersionSummary => Versions.Count > 1
        ? $"共 {Versions.Count} 个版本 · {SourceCount} 个数据源"
        : string.Empty;

    public string SourceName => Info.SourceName;
    public bool HasSourceName => !string.IsNullOrEmpty(Info.SourceName);

    /// <summary>True when the user explicitly picked the copy that is on screen.</summary>
    public bool IsPrimaryVersionPreferred { get; set; }

    public string StableId => Info.StableId;
    public string Title => Info.DisplayTitle;
    public string FolderName => Info.FolderName;
    public string FolderPath => Info.FolderPath;
    public string FolderPathDisplay => Info.FolderPath.Replace("\\", "\\\u200B");
    public long? Gid => Info.Gid;
    public string GidText => Info.Gid?.ToString() ?? "—";
    public int PageCount => Info.PageCount;
    public bool CanRead => PageCount > 0;

    public string PageCountText => Info.ExpectedPages is int expected && expected != Info.PageCount
        ? $"{Info.PageCount}/{expected} 页"
        : $"{Info.PageCount} 页";

    public string LanguageText => Info.Languages.Length == 0
        ? string.Empty
        : string.Join(" · ", Info.Languages.Select(GalleryTags.GetLanguageDisplayName));

    public bool HasLanguages => Info.Languages.Length > 0;
    public bool IsIncomplete => Info.IsIncomplete;
    public bool HasMissingPages => Info.HasMissingPages;
    public bool HasZeroByteFiles => Info.ZeroByteFiles > 0;
    public bool IsEmpty => Info.PageCount == 0;
    public bool IsDuplicateGid => Info.IsDuplicateGid;

    public string IncompleteText
    {
        get
        {
            if (Info.PageCount == 0) return "无图片";
            if (Info.ZeroByteFiles > 0) return $"{Info.ZeroByteFiles} 个损坏页";
            if (Info.HasMissingPages) return "缺页";
            if (Info.HasHeaderPageCountOutdated) return "页数过期";
            return string.Empty;
        }
    }

    public string MissingText => Info.MissingRanges.Count == 0
        ? "无"
        : string.Join(", ", Info.MissingRanges.Select(r => r.ToString()));

    public string ExtensionSummary => string.IsNullOrWhiteSpace(Info.ExtensionSummary)
        ? "—"
        : Info.ExtensionSummary;

    public string ExpectedPagesText => Info.ExpectedPages is int expected ? expected.ToString() : "未知";

    public string LastWriteText => Info.LastWriteUtc == default
        ? "—"
        : Info.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string CoverSourceText => Info.CoverSource switch
    {
        "thumb" => ".thumb",
        "page" => "首个可读页",
        _ => "无",
    };

    public string HeaderErrorText => string.IsNullOrWhiteSpace(Info.HeaderError) ? "—" : Info.HeaderError!;

    public string ProgressDetail => _isCompleted
        ? "已读完"
        : IsUnread
            ? "未读"
            : $"第 {Math.Max(1, _progressPage)} 页 / 共 {Math.Max(1, Info.PageCount)} 页";

    public string TooltipText => Title + "\n" + Details;

    public string Details
    {
        get
        {
            var parts = new List<string>(5);
            if (Versions.Count > 1) parts.Add($"共 {Versions.Count} 个版本");
            if (Info.ExpectedPages is int expected && expected != Info.PageCount)
            {
                parts.Add($"预期 {expected} 页");
            }

            if (Info.HasMissingPages) parts.Add("缺页 " + MissingText);
            if (Info.ZeroByteFiles > 0) parts.Add($"0 字节 {Info.ZeroByteFiles}");
            if (!Info.HasEhviewer) parts.Add("无 .ehviewer");
            if (!Info.HasThumb) parts.Add("无 .thumb");
            if (Info.HasGidConflict) parts.Add("gid 冲突");
            if (Info.IsDuplicateGid) parts.Add("重复 gid");
            return parts.Count == 0 ? "完整" : string.Join(" · ", parts);
        }
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        private set
        {
            if (SetProperty(ref _isFavorite, value))
            {
                OnPropertyChanged(nameof(FavoriteText));
            }
        }
    }

    public string FavoriteText => _isFavorite ? "取消收藏" : "收藏";

    public bool IsCompleted
    {
        get => _isCompleted;
        private set
        {
            if (SetProperty(ref _isCompleted, value))
            {
                OnPropertyChanged(nameof(IsUnread));
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(ProgressDetail));
            }
        }
    }

    public bool IsUnread => !_isCompleted && _progressPage <= 1 && _lastReadUtc is null;

    public int ProgressPage
    {
        get => _progressPage;
        private set
        {
            if (SetProperty(ref _progressPage, value))
            {
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(IsUnread));
                OnPropertyChanged(nameof(ProgressDetail));
            }
        }
    }

    public double ProgressFraction
    {
        get => _progressFraction;
        private set => SetProperty(ref _progressFraction, value);
    }

    // ------------------------------------------------- download projection

    /// <summary>Download-engine state shown as an overlay on the cover. Kept
    /// separate from reading progress; a task never overwrites it.</summary>
    public readonly record struct DownloadProjection(DownloadState State, int DownloadedPages, int TotalPages);

    public bool HasActiveDownload => _download is not null;

    public bool IsDownloadActive => _download is { State: DownloadState.Running or DownloadState.Pending };

    public bool IsDownloadPaused => _download is { State: DownloadState.Paused };

    public bool IsDownloadFailed => _download is { State: DownloadState.Failed };

    public bool IsDownloadIndeterminate => _download is { TotalPages: <= 0 };

    public double DownloadFraction => _download is { TotalPages: > 0 } d
        ? Math.Clamp(d.DownloadedPages / (double)d.TotalPages, 0, 1)
        : 0;

    public string DownloadStatusText
    {
        get
        {
            if (_download is not { } d) return string.Empty;
            string state = d.State switch
            {
                DownloadState.Running => "下载中",
                DownloadState.Pending => "等待下载",
                DownloadState.Paused => "已暂停",
                DownloadState.Failed => "下载失败",
                _ => d.State.ToString(),
            };
            string pages = d.TotalPages > 0 ? $" · {d.DownloadedPages}/{d.TotalPages}" : $" · {d.DownloadedPages} 页";
            string prefix = _downloadIsPrimary ? string.Empty : "其他版本 · ";
            return prefix + state + pages;
        }
    }

    /// <summary>Accessible description for the cover overlay.</summary>
    public string DownloadAutomationText => HasActiveDownload
        ? $"下载状态：{DownloadStatusText}"
        : string.Empty;

    /// <summary>
    /// Applies the download task that writes into this card's folder. Null (or a
    /// completed task) removes the overlay and leaves reading progress untouched.
    /// </summary>
    public void ApplyDownload(DownloadTask? task, bool isPrimaryVersion)
    {
        DownloadProjection? projection = task is null || task.State == DownloadState.Completed
            ? null
            : new DownloadProjection(task.State, task.DownloadedPages, task.PageCount);
        bool primary = projection is null || isPrimaryVersion;

        if (projection == _download && primary == _downloadIsPrimary) return;
        _download = projection;
        _downloadIsPrimary = primary;

        OnPropertyChanged(nameof(HasActiveDownload));
        OnPropertyChanged(nameof(IsDownloadActive));
        OnPropertyChanged(nameof(IsDownloadPaused));
        OnPropertyChanged(nameof(IsDownloadFailed));
        OnPropertyChanged(nameof(IsDownloadIndeterminate));
        OnPropertyChanged(nameof(DownloadFraction));
        OnPropertyChanged(nameof(DownloadStatusText));
        OnPropertyChanged(nameof(DownloadAutomationText));
    }

    public string ProgressText
    {
        get
        {
            if (_isCompleted) return "已读完";
            if (IsUnread) return "未读";
            return $"第 {Math.Max(1, _progressPage)} 页";
        }
    }

    public DateTime? LastReadUtc
    {
        get => _lastReadUtc;
        private set => SetProperty(ref _lastReadUtc, value);
    }

    /// <summary>
    /// Switches the copy this card opens. Used by the version window; the work
    /// identity (and therefore progress/favourite) does not change.
    /// </summary>
    public void SetPrimary(GalleryInfo version, bool preferred)
    {
        GalleryInfo target = Versions.FirstOrDefault(v =>
            string.Equals(v.StableId, version.StableId, StringComparison.OrdinalIgnoreCase)) ?? version;

        _info = target;
        _coverRequest = _coverFactory(target);
        IsPrimaryVersionPreferred = preferred;

        // Page counts differ between copies, so the progress bar has to be
        // recomputed against the new total.
        double total = Math.Max(1, _info.PageCount);
        ProgressFraction = _isCompleted ? 1 : Math.Clamp(_progressPage / total, 0, 1);

        foreach (string property in DerivedProperties)
        {
            OnPropertyChanged(property);
        }

        OnPropertyChanged(nameof(HasMultipleVersions));
        OnPropertyChanged(nameof(VersionSummary));
    }

    public void ApplyProgress(ReadingProgress? progress)
    {
        if (progress is null)
        {
            IsFavorite = false;
            IsCompleted = false;
            ProgressPage = 1;
            ProgressFraction = 0;
            LastReadUtc = null;
            return;
        }

        IsFavorite = progress.Favorite;
        IsCompleted = progress.Completed;
        ProgressPage = progress.PageNumber;
        double total = Math.Max(1, Info.PageCount);
        ProgressFraction = progress.Completed
            ? 1
            : Math.Clamp(progress.PageNumber / total, 0, 1);
        LastReadUtc = progress.LastReadUtc == default ? null : progress.LastReadUtc;
        OnPropertyChanged(nameof(ProgressDetail));
    }

    /// <summary>Legacy progress keys that were used before work-level identity.</summary>
    public static ReadingProgress? LoadProgress(ProgressStore store, GalleryInfo info)
    {
        ReadingProgress? progress = store.Get(info.WorkId);
        if (progress is null && store.Get(info.StableId) is { } legacy)
        {
            store.Set(info.WorkId, legacy);
            progress = legacy;
        }
        return progress;
    }
}

/// <summary>One row in the version management window.</summary>
public sealed class GalleryVersionViewModel
{
    public GalleryVersionViewModel(GalleryInfo info, bool isPrimary)
    {
        Info = info;
        IsPrimary = isPrimary;
    }

    public GalleryInfo Info { get; }
    public bool IsPrimary { get; }
    public string PrimaryBadge => IsPrimary ? "默认" : string.Empty;
    public bool IsPreferred => IsPrimary;

    public string SourceName => string.IsNullOrWhiteSpace(Info.SourceName) ? "默认数据源" : Info.SourceName;
    public string FolderName => Info.FolderName;
    public string FolderPath => Info.FolderPath;

    public string PageCountText => Info.ExpectedPages is int expected && expected != Info.PageCount
        ? $"{Info.PageCount} / {expected} 页"
        : $"{Info.PageCount} 页";

    public string StatusText
    {
        get
        {
            if (Info.PageCount == 0) return "无图片";
            if (Info.ZeroByteFiles > 0) return $"{Info.ZeroByteFiles} 个损坏页";
            if (Info.HasMissingPages) return "缺页 " + string.Join(",", Info.MissingRanges.Select(r => r.ToString()));
            if (Info.HasHeaderPageCountOutdated) return "页数过期";
            return "完整";
        }
    }

    public bool IsIncomplete => Info.IsIncomplete;

    public string LastWriteText => Info.LastWriteUtc == default
        ? "—"
        : Info.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string DetailText
    {
        get
        {
            var parts = new List<string>(3) { Info.ExtensionSummary.Length == 0 ? "—" : Info.ExtensionSummary };
            parts.Add(Info.HasThumb ? ".thumb" : "无 .thumb");
            parts.Add(Info.HasEhviewer ? ".ehviewer" : "无 .ehviewer");
            return string.Join(" · ", parts);
        }
    }
}

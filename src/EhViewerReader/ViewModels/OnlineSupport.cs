using System.Diagnostics;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerReader.Core.Download;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Online;
using EhViewerReader.Services;

namespace EhViewerReader.ViewModels;

/// <summary>Navigation/action surface a gallery listing needs from the shell.</summary>
public interface IOnlineHost
{
    EhSite Site { get; }

    void OpenGallery(EhGallerySummary summary);

    Task DownloadAsync(EhGallerySummary summary);

    Task DownloadDetailsAsync(EhGalleryDetails details);

    void SearchTag(string query);

    void ShowDownloads();

    void Notify(string message);

    void OpenExternal(string url);
}

/// <summary>
/// Plain string wrapper used by the search history / saved-search chips. Binding
/// raw strings would force a reflection binding, which is not AOT-safe.
/// </summary>
public sealed class TextItem
{
    public TextItem(string text)
    {
        Text = text;
    }

    public string Text { get; }

    public override string ToString() => Text;
}

/// <summary>One card in an online listing.</summary>
public sealed partial class OnlineGalleryItemViewModel : ObservableObject
{
    private readonly IOnlineHost _host;
    private ReadingProgress? _progress;

    public OnlineGalleryItemViewModel(EhGallerySummary summary, IImageRequest cover, IOnlineHost host)
    {
        Summary = summary;
        CoverRequest = cover;
        _host = host;
        CategoryBrush = new SolidColorBrush(Color.Parse(summary.CategoryColor));
    }

    public EhGallerySummary Summary { get; }
    public IImageRequest CoverRequest { get; }

    [RelayCommand]
    private void Open() => _host.OpenGallery(Summary);

    [RelayCommand]
    private Task Download() => _host.DownloadAsync(Summary);

    [RelayCommand]
    private void OpenInBrowser() =>
        _host.OpenExternal(EhUrls.Detail(_host.Site, Summary.Gid, Summary.Token));

    [RelayCommand]
    private void SearchGid() => _host.SearchTag("gid:" + Summary.Gid);

    public long Gid => Summary.Gid;
    public string Title => Summary.Title;
    public string GidText => "#" + Summary.Gid;
    public string CategoryText
    {
        get
        {
            EhCategory? category = EhCategories.Parse(Summary.Category);
            return category is null ? Summary.CategoryDisplay : EhCategories.GetDisplayName(category.Value);
        }
    }
    public IBrush CategoryBrush { get; }
    public string PagesText => Summary.PagesText;
    public string PostedText => Summary.Posted;
    public string RatingText => Summary.RatingText;
    public bool HasRating => Summary.HasRating;
    public bool HasPosted => Summary.Posted.Length > 0;

    public string MetaText
    {
        get
        {
            var parts = new List<string>(3) { PagesText };
            if (HasRating) parts.Add("★ " + RatingText);
            if (HasPosted) parts.Add(PostedText);
            return string.Join(" · ", parts);
        }
    }

    public string ProgressText => _progress switch
    {
        null => string.Empty,
        { Completed: true } => "已读完",
        { PageNumber: > 1 } p => $"读到第 {p.PageNumber} 页",
        _ => string.Empty,
    };

    public bool HasProgress => ProgressText.Length > 0;
    public bool IsFavorite => _progress?.Favorite == true;

    public string FavoriteBadge => IsFavorite ? "♥" : string.Empty;

    public string TooltipText
    {
        get
        {
            var lines = new List<string> { Title, $"{CategoryText} · {PagesText}" };
            if (Summary.Uploader.Length > 0) lines.Add("上传者：" + Summary.Uploader);
            if (HasRating) lines.Add("评分：" + RatingText);
            if (HasProgress) lines.Add(ProgressText);
            return string.Join("\n", lines);
        }
    }

    public void ApplyProgress(ReadingProgress? progress)
    {
        _progress = progress;
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(FavoriteBadge));
    }
}

/// <summary>A selectable chip used for feed tabs and favourite slots.</summary>
public sealed partial class FeedOption : ObservableObject
{
    private bool _isActive;

    public FeedOption(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }
    public string Label { get; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}

/// <summary>Category checkbox with the site's colour coding.</summary>
public sealed partial class CategoryToggle : ObservableObject
{
    private bool _isEnabled = true;

    public CategoryToggle(EhCategory category, bool enabled)
    {
        Category = category;
        Label = EhCategories.GetDisplayName(category);
        _isEnabled = enabled;
        Brush = new SolidColorBrush(Color.Parse(EhCategories.GetColor(category)));
    }

    public EhCategory Category { get; }
    public string Label { get; }
    public IBrush Brush { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }
}

public sealed class TagChip
{
    public required string Display { get; init; }
    public required string Term { get; init; }
    public bool IsNamespace { get; init; }
    public string NamespaceLabel { get; init; } = string.Empty;
    public System.Windows.Input.ICommand? Command { get; init; }
}

/// <summary>Live view over one queued download (the engine raises change events).</summary>
public sealed partial class DownloadTaskViewModel : ObservableObject
{
    private readonly DownloadsViewModel _owner;

    public DownloadTaskViewModel(DownloadTask task, DownloadsViewModel owner)
    {
        Task = task;
        _owner = owner;
    }

    public DownloadTask Task { get; }

    [RelayCommand]
    private void Pause() => _owner.PauseTask(this);

    [RelayCommand]
    private void Resume() => _owner.ResumeTask(this);

    [RelayCommand]
    private void Remove() => _owner.RemoveTask(this);

    [RelayCommand]
    private void OpenFolder() => _owner.OpenTaskFolder(this);

    [RelayCommand]
    private void DeleteFiles() => _owner.RemoveWithFiles(this);

    public string Title => Task.DisplayTitle;
    public string GidText => "#" + Task.Gid;
    public string StateText => Task.StateText;
    public string ProgressText => Task.ProgressText;
    public double Progress => Task.Progress;
    public string SizeText => Task.SizeText;
    public string Message => Task.Message;
    public string Label => Task.Label;
    public string FolderPath => Task.FolderPath;

    public bool IsRunning => Task.State == DownloadState.Running;
    public bool IsPending => Task.State == DownloadState.Pending;
    public bool IsPaused => Task.State == DownloadState.Paused;
    public bool IsFailed => Task.State == DownloadState.Failed;
    public bool IsCompleted => Task.State == DownloadState.Completed;
    public bool CanPause => Task.State is DownloadState.Running or DownloadState.Pending;
    public bool CanResume => Task.State is DownloadState.Paused or DownloadState.Failed;
    public bool HasMessage => Task.Message.Length > 0 && !IsCompleted;

    public string StateBadgeColor => Task.State switch
    {
        DownloadState.Completed => "#5AD1A0",
        DownloadState.Failed => "#FF6B81",
        DownloadState.Running => "#FF6B81",
        DownloadState.Paused => "#F2C46D",
        _ => "#9AA3B2",
    };

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
    }
}

public sealed partial class DownloadArchiveViewModel
{
    private readonly DownloadsViewModel _owner;

    public DownloadArchiveViewModel(DownloadArchiveEntry entry, DownloadsViewModel owner)
    {
        Entry = entry;
        _owner = owner;
    }

    [RelayCommand]
    private void OpenFolder() => _owner.OpenArchiveFolder(this);

    public DownloadArchiveEntry Entry { get; }
    public string Title => Entry.DisplayTitle;
    public string GidText => "#" + Entry.Gid;
    public string Category => Entry.Category;
    public string PagesText => Entry.PageCount > 0 ? $"{Entry.PageCount} 页" : "—";
    public string SizeText => Entry.SizeText;
    public string FinishedText => Entry.FinishedText;
    public string FolderPath => Entry.FolderPath;
}

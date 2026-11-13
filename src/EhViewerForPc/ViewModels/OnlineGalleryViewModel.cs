using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using EhViewerForPc.Core.Download;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Services;

namespace EhViewerForPc.ViewModels;

/// <summary>Seed values for the favourite dialog (read-only snapshot).</summary>
public sealed record FavoriteDraft(bool IsFavorite, int Slot, string Note);

/// <summary>
/// Online gallery detail: metadata, tags, favourite slot, rating, comments and
/// the entry points that matter — read online, download, torrents and archive.
/// </summary>
public sealed partial class OnlineGalleryViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AppServices _services;
    private readonly IOnlineHost _host;
    private readonly EhGallerySummary _summary;
    private IReadOnlyList<FeedOption> _qualityOptions = [];
    private EhGalleryDetails? _details;
    private bool _loading;
    private string _statusText = "载入中…";
    private IImageRequest? _coverRequest;
    private bool _isFavorite;
    private int _favoriteSlot = -1;
    private string _favoriteNote = string.Empty;
    private bool _favoriteBusy;
    private bool _favoriteNeedsCheck;
    private FeedOption? _selectedSlot;
    private FeedOption _selectedQuality = new("orig", "原图");

    private int _myRating;
    private string _ratingStatus = string.Empty;
    private string _commentText = string.Empty;
    private string _commentStatus = string.Empty;
    private bool _postingComment;
    private bool _loadingComments;
    private long? _editingCommentId;

    public OnlineGalleryViewModel(AppServices services, IOnlineHost host, EhGallerySummary summary)
    {
        _services = services;
        _host = host;
        _summary = summary;
        Presentation = new(services, term => TagSearchRequested?.Invoke(this, term));
        _host.LocalStateChanged += OnWorkStateChanged;
        _services.Downloads.Changed += OnWorkStateChanged;

        RebuildFavoriteSlots();
        RebuildQualityOptions();

        Title = summary.Title;
        if (summary.ThumbUrl.Length > 0)
        {
            _coverRequest = _services.OnlineImages.CreateThumbnailRequest(summary.ThumbUrl, "work:" + summary.Gid);
        }
        Presentation.Apply(new WorkRecord { WorkId = summary.Gid.ToString(), Gid = summary.Gid, Title = summary.Title, PageCount = summary.Pages }, _coverRequest, fallbackTags: summary.Tags);
    }

    public event EventHandler<OnlineReadRequest>? ReadRequested;

    public event EventHandler<string>? TagSearchRequested;

    public event EventHandler<EhGalleryDetails>? TorrentsRequested;

    public event EventHandler<EhGalleryDetails>? ArchiveRequested;

    /// <summary>Raised after a favourite change so the favourites feed can reload.</summary>
    public event EventHandler? FavoriteChanged;

    public GalleryDetailsViewModel Presentation { get; }
    public ObservableCollection<TagChip> Tags => Presentation.Tags;
    public ObservableCollection<FeedOption> FavoriteSlots { get; } = [];
    public ObservableCollection<CommentItemViewModel> Comments { get; } = [];

    public EhGallerySummary Summary => _summary;

    /// <summary>Loaded detail page; null until <see cref="InitializeAsync"/> finishes.</summary>
    public EhGalleryDetails? Details => _details;
    public long Gid => _summary.Gid;
    public string GidText => "#" + _summary.Gid;
    public string Url => EhUrls.Detail(_services.Online.Site, _summary.Gid, _summary.Token);

    public string Title { get; private set; }
    public string JapaneseTitle => _details?.JapaneseTitle ?? string.Empty;
    public bool HasJapaneseTitle => JapaneseTitle.Length > 0 && JapaneseTitle != Title;

    public IImageRequest? CoverRequest
    {
        get => _coverRequest;
        private set => SetProperty(ref _coverRequest, value);
    }

    public bool IsLoading
    {
        get => _loading;
        private set { SetProperty(ref _loading, value); Presentation.IsLoading = value; }
    }

    public string StatusText
    {
        get => _statusText;
        private set { SetProperty(ref _statusText, value); Presentation.StatusText = value; }
    }

    public bool IsSignedIn => _services.Online.IsSignedIn;

    /// <summary>API identity is only present for signed-in users.</summary>
    public bool CanInteract => _details?.CanUseApi == true;

    public string CategoryText => _details?.Category ?? _summary.CategoryDisplay;
    public IBrush CategoryBrush => new SolidColorBrush(Color.Parse(_details?.CategoryColor ?? _summary.CategoryColor));
    public string PagesText => (_details?.Pages ?? _summary.Pages) is int pages and > 0 ? $"{pages} 页" : "—";
    public string RatingText => _details is { Rating: > 0 } d ? d.Rating.ToString("F2") : _summary.RatingText;
    public string RatingCountText => _details is { RatingCount: > 0 } d ? $"{d.RatingCount} 人评分" : string.Empty;
    public string PostedText => _details?.Posted ?? _summary.Posted;
    public string UploaderText => _details?.Uploader ?? _summary.Uploader;
    public string FileSizeText => _details?.FileSize ?? "—";
    public string LanguageText => _details?.Language ?? "—";
    public string VisibleText => _details?.Visible ?? "—";
    public string ParentText => _details?.Parent ?? "—";
    public string TorrentText => _details is { TorrentCount: > 0 } d ? $"种子 ({d.TorrentCount})" : "种子";
    public bool HasTorrents => _details is { TorrentCount: > 0 };
    public bool HasArchive => (_details?.ArchiveUrl.Length ?? 0) > 0;
    public string FavoriteCountText => _details is { Favorited.Length: > 0 } d ? d.Favorited : string.Empty;

    /// <summary>True when the local library already holds this gallery id.</summary>
    public bool IsInLocalLibrary => _host.HasLocalCopy(_summary.Gid);

    public string DownloadButtonText => _services.Downloads.FindTask(Gid)?.State switch
    {
        DownloadState.Completed => "已下载", DownloadState.Running => "下载中", DownloadState.Pending => "等待下载",
        DownloadState.Paused => "继续下载", DownloadState.Failed => "重试下载", _ => "下载"
    };
    public bool CanDownload => _details is not null && _services.Downloads.FindTask(Gid)?.State is not (DownloadState.Completed or DownloadState.Running or DownloadState.Pending);
    private bool _disposed;
    private void OnWorkStateChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(IsInLocalLibrary)); OnPropertyChanged(nameof(HasPartialLocalCopy));
        OnPropertyChanged(nameof(ReadButtonText)); OnPropertyChanged(nameof(DownloadButtonText)); OnPropertyChanged(nameof(CanDownload));
    }, DispatcherPriority.Background);

    public bool HasPartialLocalCopy => !IsInLocalLibrary && _host.HasLocalCopy(_summary.Gid, false);

    public bool HasFavoriteCount => FavoriteCountText.Length > 0;

    /// <summary>The primary button reads local when the work is already on disk.</summary>
    public string ReadButtonText => IsInLocalLibrary ? "本地阅读" : "在线阅读";

    /// <summary>Image size used by both the reader and the download of this gallery.</summary>
    public IReadOnlyList<FeedOption> QualityOptions => _qualityOptions;

    public FeedOption SelectedQuality
    {
        get => _selectedQuality;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedQuality)) return;
            _selectedQuality = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(QualityHint));

            // Remember the choice: it is the default for the next gallery too.
            ImageQuality quality = IsOriginalQuality ? ImageQuality.Original : ImageQuality.Resampled;
            _services.Settings.ImageQuality = quality;
            if (!IsOriginalQuality && int.TryParse(value.Key.AsSpan(3), out int width)) _services.Settings.ResampledWidth = width;
            _services.TrySaveSettings(out _);
        }
    }

    public bool IsOriginalQuality => _selectedQuality.Key == "orig";

    public string QualityHint => IsOriginalQuality
        ? (_services.Online.IsSignedIn
            ? "原图：每页单独解析，画质最好、流量最大"
            : "原图需要登录；未登录时会自动改用站点压缩图")
        : $"请求宽度 {_services.Settings.ResampledWidth}px 的站点压缩图；实际尺寸受站点权限及源图影响";

    private void RebuildQualityOptions()
    {
        _qualityOptions = new[] { new FeedOption("orig", "原图") }
            .Concat(new[] { 780, 980, 1280, 1600, 2400 }.Select(w => new FeedOption("res" + w, "压缩图 " + w + "px"))).ToArray();
        string key = _services.Settings.ImageQuality == ImageQuality.Original ? "orig" : "res" + _services.Settings.ResampledWidth;
        _selectedQuality = _qualityOptions.First(o => o.Key == key);
        _selectedQuality.IsActive = true;
    }

    public string MetaLine
    {
        get
        {
            var parts = new List<string> { CategoryText, PagesText };
            if (RatingText != "—") parts.Add("★ " + RatingText);
            if (PostedText.Length > 0) parts.Add(PostedText);
            if (FileSizeText != "—") parts.Add(FileSizeText);
            return string.Join(" · ", parts);
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
                OnPropertyChanged(nameof(FavoriteSlotText));
                OnPropertyChanged(nameof(HasFavoriteSlotText));
            }
        }
    }

    public string FavoriteText => _isFavorite ? "已收藏" : "收藏";

    /// <summary>Category shown next to the favourite button; never a guessed slot.</summary>
    public string FavoriteSlotText => !_isFavorite
        ? string.Empty
        : _favoriteSlot >= 0
            ? _services.Settings.Account.FavoriteSlotName(_favoriteSlot)
            : "分类待核实";

    public bool HasFavoriteSlotText => FavoriteSlotText.Length > 0;

    /// <summary>True while a favourite change is in flight; blocks duplicate submits.</summary>
    public bool FavoriteBusy
    {
        get => _favoriteBusy;
        private set
        {
            if (SetProperty(ref _favoriteBusy, value))
            {
                OnPropertyChanged(nameof(CanChangeFavorite));
                OnPropertyChanged(nameof(CanOpenFavoriteDialog));
            }
        }
    }

    /// <summary>True after an unverifiable submit; offers an explicit re-check.</summary>
    public bool FavoriteNeedsCheck
    {
        get => _favoriteNeedsCheck;
        private set => SetProperty(ref _favoriteNeedsCheck, value);
    }

    public bool CanChangeFavorite => IsSignedIn && _details is not null && !FavoriteBusy;

    /// <summary>
    /// Whether the favourite dialog may be opened. Deliberately independent of
    /// the sign-in state: a signed-out click must produce an explanation
    /// instead of a permanently greyed-out button.
    /// </summary>
    public bool CanOpenFavoriteDialog => _details is not null && !FavoriteBusy;

    /// <summary>Explains the sign-in requirement without opening the dialog.</summary>
    public void NotifySignInRequired() => _host.Notify("收藏需要先登录 E-Hentai 账号");

    /// <summary>Real categories only (0–9), used by the confirmation dialog.</summary>
    public IReadOnlyList<FeedOption> FavoriteCategories => FavoriteSlots
        .Where(o => o.Key.Length > 0 && char.IsDigit(o.Key[0]))
        .ToArray();

    /// <summary>Display-only selection. Changing it never submits anything.</summary>
    public FeedOption? SelectedSlot
    {
        get => _selectedSlot;
        private set
        {
            if (ReferenceEquals(value, _selectedSlot)) return;
            _selectedSlot = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The rating this window submitted (0 = not rated here).</summary>
    public int MyRating
    {
        get => _myRating;
        private set
        {
            if (!SetProperty(ref _myRating, value))
            {
                return;
            }

            OnPropertyChanged(nameof(MyRatingText));
        }
    }

    public string MyRatingText => _myRating <= 0
        ? "未评分"
        : new string('★', _myRating) + new string('☆', 5 - _myRating);

    public string RatingStatus
    {
        get => _ratingStatus;
        private set
        {
            if (SetProperty(ref _ratingStatus, value)) OnPropertyChanged(nameof(HasRatingStatus));
        }
    }

    public bool HasRatingStatus => _ratingStatus.Length > 0;

    public string CommentText
    {
        get => _commentText;
        set => SetProperty(ref _commentText, value);
    }

    public string CommentStatus
    {
        get => _commentStatus;
        private set => SetProperty(ref _commentStatus, value);
    }

    public bool PostingComment
    {
        get => _postingComment;
        private set
        {
            if (SetProperty(ref _postingComment, value)) OnPropertyChanged(nameof(CanPostComment));
        }
    }

    public bool LoadingComments
    {
        get => _loadingComments;
        private set
        {
            if (SetProperty(ref _loadingComments, value)) OnPropertyChanged(nameof(CanPostComment));
        }
    }

    public bool CanPostComment => IsSignedIn && !PostingComment && !LoadingComments;

    public string CommentHeader => Comments.Count > 0 ? $"评论（{Comments.Count}）" : "评论";

    public bool IsEditingComment => _editingCommentId is not null;

    public string CommentSubmitText => IsEditingComment ? "保存修改" : "发布评论";

    public bool HasComments => Comments.Count > 0;

    public bool HasMoreComments => _details?.HasMoreComments == true;

    public async Task InitializeAsync()
    {
        IsLoading = true;
        StatusText = "载入画廊详情…";
        try
        {
            EhGalleryDetails details = await _services.Online
                .GetDetailsAsync(_summary.Gid, _summary.Token, _lifetime.Token)
                .ConfigureAwait(true);

            ApplyDetails(details);

            int slot = -1;
            if (IsSignedIn && details.IsFavorited)
            {
                slot = ResolveFavoriteSlot(details);
                if (slot < 0)
                {
                    // #favoritelink proves the state but not the slot; the popup
                    // names the selected one and carries the stored note.
                    EhFavoritePopup? popup = await _services.Online
                        .GetFavoritePopupAsync(_summary.Gid, _summary.Token, _lifetime.Token)
                        .ConfigureAwait(true);
                    if (popup is not null)
                    {
                        slot = popup.Slot;
                        _favoriteNote = popup.Note;
                    }
                }
            }

            SetFavoriteState(details.IsFavorited, slot);

        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal void ApplyDetails(EhGalleryDetails details)
    {
        _details = details;
        OnWorkStateChanged(this, EventArgs.Empty);
        Title = details.DisplayTitle.Length > 0 ? details.DisplayTitle : _summary.Title;
        StatusText = string.Empty;
        MyRating = 0;
        RatingStatus = string.Empty;
        CommentStatus = string.Empty;

        if (details.ThumbUrl.Length > 0)
        {
            CoverRequest = _services.OnlineImages.CreateThumbnailRequest(details.ThumbUrl, "work:" + details.Gid);
        }

        BuildTags(details);
        SetFavoriteState(details.IsFavorited, details.FavoriteSlot);
        RebuildComments(details.Comments);
        RaiseMetadata();
    }

    private void BuildTags(EhGalleryDetails details)
    {
        var work = WorkRecord.FromDetails(details);
        _services.Works.Upsert(work);
        Presentation.Apply(work, CoverRequest);
    }

    private void RebuildComments(IEnumerable<EhComment> comments)
    {
        Comments.Clear();
        foreach (EhComment comment in comments)
        {
            comment.IsUploader = comment.User.Length > 0 &&
                                 string.Equals(comment.User, _details?.Uploader, StringComparison.OrdinalIgnoreCase);
            Comments.Add(new CommentItemViewModel(comment, this));
        }

        OnPropertyChanged(nameof(HasComments));
        OnPropertyChanged(nameof(CommentHeader));
        OnPropertyChanged(nameof(HasMoreComments));
    }

    /// <summary>
    /// Applies the favourite state parsed from the detail page. A favourited
    /// gallery whose slot could not be resolved is shown as "已收藏（分类未知）"
    /// instead of pretending it is not favourited.
    /// </summary>
    private void SetFavoriteState(bool favorited, int slot)
    {
        if (favorited && slot < 0) slot = -2;

        _favoriteSlot = slot;
        IsFavorite = favorited;
        OnPropertyChanged(nameof(FavoriteSlotText));
        OnPropertyChanged(nameof(HasFavoriteSlotText));

        // The "unknown slot" pseudo-option only exists after a rebuild.
        if (slot == -2 && FavoriteSlots.All(o => o.Key != "-2"))
        {
            RebuildFavoriteSlots();
            return;
        }

        FeedOption? option = slot >= 0
            ? FavoriteSlots.FirstOrDefault(o => o.Key == slot.ToString())
            : slot == -2
                ? FavoriteSlots.FirstOrDefault(o => o.Key == "-2")
                : null;

        if (_selectedSlot is not null) _selectedSlot.IsActive = false;
        SelectedSlot = option;
        if (option is not null) option.IsActive = true;
    }

    /// <summary>Maps the slot name shown on the detail page to its index.</summary>
    private int ResolveFavoriteSlot(EhGalleryDetails details)
    {
        if (details.FavoriteSlot >= 0) return details.FavoriteSlot;

        string name = details.FavoriteName.Trim();
        if (name.Length == 0) return -1;

        for (int index = 0; index < 10; index++)
        {
            if (string.Equals(_services.Settings.Account.FavoriteSlotName(index), name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        if (name.StartsWith("Favorite ", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name["Favorite ".Length..], out int parsed) && parsed is >= 0 and <= 9)
        {
            return parsed;
        }

        return -1;
    }

    /// <summary>
    /// Reads the favourite popup before the dialog opens so the draft is seeded
    /// with the real category and note. Never changes the server state.
    /// </summary>
    public async Task<FavoriteDraft?> PrepareFavoriteDraftAsync()
    {
        if (_details is null || !IsSignedIn) return null;
        FavoriteBusy = true;
        try
        {
            EhFavoritePopup? popup = await _services.Online
                .GetFavoritePopupAsync(_summary.Gid, _summary.Token, _lifetime.Token)
                .ConfigureAwait(true);

            if (popup is not null)
            {
                // Only fill in a slot the detail page could not prove; an already
                // resolved slot wins, since the popup may preselect category 0
                // for a gallery that is not favourited at all.
                if (_favoriteSlot < 0 && IsFavorite) _favoriteSlot = popup.Slot;
                _favoriteNote = popup.Note;
            }

            return new FavoriteDraft(IsFavorite, _favoriteSlot, _favoriteNote);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            StatusText = "读取收藏分类失败：" + ex.Message;
            return new FavoriteDraft(IsFavorite, _favoriteSlot, _favoriteNote);
        }
        finally
        {
            FavoriteBusy = false;
        }
    }

    /// <summary>
    /// Applies one favourite change (add/move, or remove when
    /// <paramref name="add"/> is false) and refreshes the UI from the verified
    /// server state. Never called implicitly by a selection change.
    /// </summary>
    public async Task<FavoriteUpdateResult> ApplyFavoriteAsync(int slot, bool add)
    {
        if (_details is null) return FavoriteUpdateResult.Unknown("详情尚未载入");
        if (!IsSignedIn) return new FavoriteUpdateResult(FavoriteUpdateOutcome.Rejected, "收藏需要先登录账号", false, -1, false);
        if (FavoriteBusy) return FavoriteUpdateResult.Unknown("正在提交，请稍候");

        FavoriteBusy = true;
        StatusText = add ? "正在提交收藏…" : "正在取消收藏…";
        try
        {
            FavoriteUpdateResult result = await _services.Online
                .SetFavoriteAsync(_summary.Gid, _summary.Token, slot, add, _favoriteNote, _lifetime.Token)
                .ConfigureAwait(true);

            if (result.Verified)
            {
                SetFavoriteState(result.IsFavorited, result.Slot);
                FavoriteNeedsCheck = false;
            }
            else if (result.Outcome == FavoriteUpdateOutcome.Succeeded)
            {
                // The POST looked applied but the read-back failed; show the
                // intended state and say so instead of pretending it is verified.
                SetFavoriteState(add, add ? slot : -1);
                FavoriteNeedsCheck = true;
            }
            else
            {
                FavoriteNeedsCheck = result.Outcome == FavoriteUpdateOutcome.Unknown;
            }

            StatusText = result.Message;
            _host.Notify(result.Message);
            if (result.Outcome != FavoriteUpdateOutcome.Rejected) FavoriteChanged?.Invoke(this, EventArgs.Empty);
            return result;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return FavoriteUpdateResult.Unknown("已取消");
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            StatusText = "收藏失败：" + ex.Message;
            return FavoriteUpdateResult.Unknown("收藏失败：" + ex.Message);
        }
        finally
        {
            FavoriteBusy = false;
        }
    }

    /// <summary>Read-only re-check after an unknown outcome ("重新检查").</summary>
    public async Task<FavoriteUpdateResult> CheckFavoriteStateAsync()
    {
        if (_details is null) return FavoriteUpdateResult.Unknown("详情尚未载入");
        if (FavoriteBusy) return FavoriteUpdateResult.Unknown("正在检查，请稍候");

        FavoriteBusy = true;
        StatusText = "正在检查收藏状态…";
        try
        {
            (bool Favorited, int Slot)? state = await _services.Online
                .TryReadFavoriteStateAsync(_summary.Gid, _summary.Token, _lifetime.Token)
                .ConfigureAwait(true);

            if (state is not { } current)
            {
                StatusText = "无法读取收藏状态，请稍后重试";
                return FavoriteUpdateResult.Unknown(StatusText);
            }

            SetFavoriteState(current.Favorited, current.Slot);
            FavoriteNeedsCheck = false;
            string message = current.Favorited
                ? current.Slot >= 0
                    ? "已收藏到 " + _services.Settings.Account.FavoriteSlotName(current.Slot)
                    : "已收藏（分类待核实）"
                : "未收藏";
            StatusText = message;
            _host.Notify(message);
            FavoriteChanged?.Invoke(this, EventArgs.Empty);
            return new FavoriteUpdateResult(FavoriteUpdateOutcome.Succeeded, message, current.Favorited, current.Slot, true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return FavoriteUpdateResult.Unknown("已取消");
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            StatusText = "无法读取收藏状态：" + ex.Message;
            return FavoriteUpdateResult.Unknown(StatusText);
        }
        finally
        {
            FavoriteBusy = false;
        }
    }

    [RelayCommand]
    private async Task CheckFavoriteAsync() => await CheckFavoriteStateAsync();

    /// <summary>Rebuilds the slot list so settings renames appear immediately.</summary>
    public void RebuildFavoriteSlots()
    {
        int selected = _favoriteSlot;

        FavoriteSlots.Clear();
        FavoriteSlots.Add(new FeedOption("-1", "未收藏"));
        if (selected == -2) FavoriteSlots.Add(new FeedOption("-2", "已收藏（分类未知）"));
        for (int index = 0; index < 10; index++)
        {
            FavoriteSlots.Add(new FeedOption(index.ToString(), _services.Settings.Account.FavoriteSlotName(index)));
        }

        FeedOption? option = selected >= 0
            ? FavoriteSlots.FirstOrDefault(o => o.Key == selected.ToString())
            : selected == -2
                ? FavoriteSlots.FirstOrDefault(o => o.Key == "-2")
                : FavoriteSlots[0];

        SelectedSlot = option;
        if (option is not null) option.IsActive = true;
    }

    [RelayCommand]
    private async Task RefreshAsync() => await InitializeAsync();

    [RelayCommand]
    private void Read()
    {
        if (_details is null) return;

        // Already on disk: read the local copy, not the online one.
        if (IsInLocalLibrary && _host.OpenLocal(_summary.Gid))
        {
            _host.Notify("已打开本地副本");
            return;
        }

        ReadOnline();
    }

    [RelayCommand]
    private void ReadPartialLocal() => _host.OpenLocal(_summary.Gid, allowIncomplete: true);

    [RelayCommand]
    private void ReadOnline()
    {
        if (_details is null) return;
        ReadRequested?.Invoke(this, new OnlineReadRequest(_details, SelectedQuality.Key == "orig"
            ? ImageQuality.Original
            : ImageQuality.Resampled));
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (_details is null) return;
        await _host.DownloadDetailsAsync(_details, IsOriginalQuality
            ? ImageQuality.Original
            : ImageQuality.Resampled);
    }

    [RelayCommand]
    private void OpenTorrents()
    {
        if (_details is null) return;
        TorrentsRequested?.Invoke(this, _details);
    }

    [RelayCommand]
    private void OpenArchive()
    {
        if (_details is null) return;
        if (!IsSignedIn)
        {
            _host.Notify("归档下载需要登录 E-Hentai 账号");
            return;
        }

        ArchiveRequested?.Invoke(this, _details);
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        _host.OpenExternal(Url);
    }

    [RelayCommand]
    private void OpenHathPage() => _host.OpenExternal(EhUrls.HathClient(_host.Site));

    [RelayCommand]
    private void SearchTag(TagChip? chip)
    {
        if (chip is null) return;
        TagSearchRequested?.Invoke(this, chip.Term);
    }

    [RelayCommand]
    private async Task RateAsync(string? stars)
    {
        if (_details is null) return;
        if (!CanInteract)
        {
            RatingStatus = "评分需要登录（匿名页面没有 API 身份）";
            return;
        }

        if (!int.TryParse(stars, out int value) || value is < 1 or > 5) return;

        RatingStatus = "正在提交评分…";
        try
        {
            (double average, int count, string? error) = await _services.Online
                .RateGalleryAsync(_details, value, _lifetime.Token)
                .ConfigureAwait(true);

            if (error is not null)
            {
                RatingStatus = "评分失败：" + error;
                return;
            }

            _details.Rating = average;
            _details.RatingCount = count;
            MyRating = value;
            RatingStatus = "已评分";
            RaiseMetadata();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            RatingStatus = "评分失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task PostCommentAsync()
    {
        if (_details is null) return;
        if (!IsSignedIn)
        {
            CommentStatus = "需要登录后才能评论";
            return;
        }

        string text = CommentText.Trim();
        if (text.Length == 0)
        {
            CommentStatus = "评论内容不能为空";
            return;
        }

        PostingComment = true;
        CommentStatus = _editingCommentId is null ? "正在发布…" : "正在保存修改…";
        try
        {
            EhCommentActionResult result = await _services.Online
                .PostCommentAsync(_details.Gid, _details.Token, text, _editingCommentId, _lifetime.Token)
                .ConfigureAwait(true);

            CommentStatus = result.Message;
            if (result.Success)
            {
                CommentText = string.Empty;
                ClearEdit();
                await ReloadCommentsAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            CommentStatus = "发布失败：" + ex.Message;
        }
        finally
        {
            PostingComment = false;
        }
    }

    [RelayCommand]
    private async Task ReloadCommentsAsync()
    {
        if (_details is null) return;

        LoadingComments = true;
        try
        {
            EhCommentList list = await _services.Online
                .GetCommentsAsync(_details.Gid, _details.Token, _lifetime.Token)
                .ConfigureAwait(true);
            _details.HasMoreComments = list.HasMore;
            RebuildComments(list.Comments);
            OnPropertyChanged(nameof(HasMoreComments));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            CommentStatus = "评论加载失败：" + ex.Message;
        }
        finally
        {
            LoadingComments = false;
        }
    }

    /// <summary>Loads an editable comment into the box; save reuses the same command.</summary>
    public void StartEditComment(CommentItemViewModel item)
    {
        if (!item.CanEdit) return;
        _editingCommentId = item.Id;
        CommentText = item.Text;
        CommentStatus = "正在编辑评论，保存后立即生效";
        OnPropertyChanged(nameof(IsEditingComment));
        OnPropertyChanged(nameof(CommentSubmitText));
    }

    public void ClearEdit()
    {
        if (_editingCommentId is null) return;
        _editingCommentId = null;
        CommentText = string.Empty;
        CommentStatus = string.Empty;
        OnPropertyChanged(nameof(IsEditingComment));
        OnPropertyChanged(nameof(CommentSubmitText));
    }

    [RelayCommand]
    private void CancelEdit() => ClearEdit();

    public async Task VoteCommentAsync(CommentItemViewModel item, int vote)
    {
        if (_details is null) return;
        if (!CanInteract)
        {
            CommentStatus = "投票需要登录";
            return;
        }

        try
        {
            EhCommentActionResult result = await _services.Online
                .VoteCommentAsync(_details, item.Id, vote, _lifetime.Token)
                .ConfigureAwait(true);

            CommentStatus = result.Message;
            if (result.Success) item.ApplyScore(result.Score);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            CommentStatus = "投票失败：" + ex.Message;
        }
    }

    private void RaiseMetadata()
    {
        foreach (string property in new[]
                 {
                     nameof(JapaneseTitle), nameof(HasJapaneseTitle), nameof(CategoryText), nameof(CategoryBrush),
                     nameof(PagesText), nameof(RatingText), nameof(RatingCountText), nameof(PostedText),
                     nameof(UploaderText), nameof(FileSizeText), nameof(LanguageText), nameof(VisibleText),
                     nameof(ParentText), nameof(MetaLine), nameof(Title), nameof(TorrentText), nameof(HasTorrents),
                     nameof(HasArchive), nameof(FavoriteCountText), nameof(CanInteract), nameof(CanPostComment),
                     nameof(CanChangeFavorite), nameof(CanOpenFavoriteDialog),
                 })
        {
            OnPropertyChanged(property);
        }
    }
    public void Dispose()
    {
        _disposed = true;
        _host.LocalStateChanged -= OnWorkStateChanged;
        _services.Downloads.Changed -= OnWorkStateChanged;
        _lifetime.Cancel();
    }
}

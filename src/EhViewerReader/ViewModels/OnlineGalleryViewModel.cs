using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerReader.Core.Online;
using EhViewerReader.Core.Models;
using EhViewerReader.Services;

namespace EhViewerReader.ViewModels;

/// <summary>
/// Online gallery detail: metadata, tags, favourite slot, rating, comments and
/// the entry points that matter — read online, download, torrents and archive.
/// </summary>
public sealed partial class OnlineGalleryViewModel : ObservableObject
{
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
    private FeedOption? _selectedSlot;
    private FeedOption _selectedQuality = new("orig", "原图");
    private bool _suppressSlotChange;

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

        RebuildFavoriteSlots();
        RebuildQualityOptions();

        Title = summary.Title;
        if (summary.ThumbUrl.Length > 0)
        {
            _coverRequest = _services.OnlineImages.CreateThumbnailRequest(summary.ThumbUrl);
        }
    }

    public event EventHandler<OnlineReadRequest>? ReadRequested;

    public event EventHandler<string>? TagSearchRequested;

    public event EventHandler<EhGalleryDetails>? TorrentsRequested;

    public event EventHandler<EhGalleryDetails>? ArchiveRequested;

    public ObservableCollection<TagChip> Tags { get; } = [];
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
        private set => SetProperty(ref _loading, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
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
            if (_services.Settings.ImageQuality != quality)
            {
                _services.Settings.ImageQuality = quality;
                _services.TrySaveSettings(out _);
            }
        }
    }

    public bool IsOriginalQuality => _selectedQuality.Key == "orig";

    public string QualityHint => IsOriginalQuality
        ? (_services.Online.IsSignedIn
            ? "原图：每页单独解析，画质最好、流量最大"
            : "原图需要登录；未登录时会自动改用站点压缩图")
        : "站点压缩图：宽度约 1280（站点账号可改为 780/980/1600/2400）";

    private void RebuildQualityOptions()
    {
        _qualityOptions =
        [
            new FeedOption("orig", "原图"),
            new FeedOption("res", "压缩图（约 1280）"),
        ];

        bool original = _services.Settings.ImageQuality == ImageQuality.Original;
        _selectedQuality = _qualityOptions[original ? 0 : 1];
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
            }
        }
    }

    public string FavoriteText => _isFavorite ? "已收藏" : "收藏";

    public FeedOption? SelectedSlot
    {
        get => _selectedSlot;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedSlot)) return;
            _selectedSlot = value;
            OnPropertyChanged();
            if (!_suppressSlotChange) _ = ApplySlotAsync(value);
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
                .GetDetailsAsync(_summary.Gid, _summary.Token, CancellationToken.None)
                .ConfigureAwait(true);

            _details = details;
            Title = details.DisplayTitle.Length > 0 ? details.DisplayTitle : _summary.Title;
            StatusText = string.Empty;
            MyRating = 0;
            RatingStatus = string.Empty;
            CommentStatus = string.Empty;

            if (details.ThumbUrl.Length > 0)
            {
                CoverRequest = _services.OnlineImages.CreateThumbnailRequest(details.ThumbUrl);
            }

            BuildTags(details);

            int slot = -1;
            if (IsSignedIn && details.IsFavorited)
            {
                slot = ResolveFavoriteSlot(details);
                if (slot < 0)
                {
                    // #favoritelink proves the state but not the slot; the popup
                    // names the selected one.
                    slot = await _services.Online
                        .GetFavoriteSlotAsync(_summary.Gid, _summary.Token, CancellationToken.None)
                        .ConfigureAwait(true);
                }
            }

            SetFavoriteState(details.IsFavorited, slot);
            RebuildComments(details.Comments);
            RaiseMetadata();
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void BuildTags(EhGalleryDetails details)
    {
        Tags.Clear();
        bool translate = _services.Settings.Online.ShowTagTranslations;

        foreach (EhTag tag in details.Tags)
        {
            string term = tag.SearchTerm;
            string? translated = translate ? _services.TagTranslation.Translate(tag.Namespace, tag.Name) : null;
            string display;
            if (translated is null)
            {
                display = tag.Namespace.Length > 0 ? tag.Namespace + ":" + tag.Name : tag.Name;
            }
            else
            {
                string ns = _services.TagTranslation.Namespace(tag.Namespace);
                display = ns.Length > 0 ? ns + ":" + translated : translated;
            }

            Tags.Add(new TagChip
            {
                Display = display,
                Term = term,
                NamespaceLabel = tag.Namespace,
                Command = new RelayCommand(() => TagSearchRequested?.Invoke(this, term)),
            });
        }
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

        // The "unknown slot" pseudo-option only exists after a rebuild.
        if (slot == -2 && FavoriteSlots.All(o => o.Key != "-2"))
        {
            RebuildFavoriteSlots();
            return;
        }

        _suppressSlotChange = true;
        try
        {
            FeedOption? option = slot >= 0
                ? FavoriteSlots.FirstOrDefault(o => o.Key == slot.ToString())
                : slot == -2
                    ? FavoriteSlots.FirstOrDefault(o => o.Key == "-2")
                    : null;

            if (_selectedSlot is not null) _selectedSlot.IsActive = false;
            _selectedSlot = option;
            if (option is not null) option.IsActive = true;
            OnPropertyChanged(nameof(SelectedSlot));
        }
        finally
        {
            _suppressSlotChange = false;
        }
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

    private async Task ApplySlotAsync(FeedOption option)
    {
        if (_details is null) return;
        if (!IsSignedIn)
        {
            _host.Notify("收藏需要先登录账号");
            SetFavoriteState(IsFavorite, _favoriteSlot);
            return;
        }

        // Pseudo-option: the gallery is favourited but the slot could not be read.
        if (option.Key == "-2") return;

        bool add = int.TryParse(option.Key, out int slot) && slot >= 0;
        int target = add ? slot : _favoriteSlot;
        if (target < 0 && !add) return;

        try
        {
            StatusText = add ? "正在收藏…" : "正在取消收藏…";
            bool ok = await _services.Online
                .SetFavoriteAsync(_summary.Gid, _summary.Token, target, add, string.Empty, CancellationToken.None)
                .ConfigureAwait(true);

            if (ok)
            {
                SetFavoriteState(add, add ? target : -1);
                StatusText = add ? "已收藏到 " + _services.Settings.Account.FavoriteSlotName(target) : "已取消收藏";
                _host.Notify(StatusText);
            }
            else
            {
                StatusText = "收藏操作未成功";
                SetFavoriteState(IsFavorite, _favoriteSlot);
            }
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            StatusText = "收藏失败：" + ex.Message;
            SetFavoriteState(IsFavorite, _favoriteSlot);
        }
    }

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

        _suppressSlotChange = true;
        try
        {
            _selectedSlot = option;
            if (option is not null) option.IsActive = true;
            OnPropertyChanged(nameof(SelectedSlot));
        }
        finally
        {
            _suppressSlotChange = false;
        }
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
    private void ToggleFavorite()
    {
        FeedOption target = IsFavorite
            ? FavoriteSlots[0]
            : FavoriteSlots[Math.Clamp(_favoriteSlot < 0 ? 1 : _favoriteSlot + 1, 1, 10)];
        _ = ApplySlotAsync(target);
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
                .RateGalleryAsync(_details, value, CancellationToken.None)
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
                .PostCommentAsync(_details.Gid, _details.Token, text, _editingCommentId, CancellationToken.None)
                .ConfigureAwait(true);

            CommentStatus = result.Message;
            if (result.Success)
            {
                CommentText = string.Empty;
                ClearEdit();
                await ReloadCommentsAsync().ConfigureAwait(true);
            }
        }
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
                .GetCommentsAsync(_details.Gid, _details.Token, CancellationToken.None)
                .ConfigureAwait(true);
            _details.HasMoreComments = list.HasMore;
            RebuildComments(list.Comments);
            OnPropertyChanged(nameof(HasMoreComments));
        }
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
                .VoteCommentAsync(_details, item.Id, vote, CancellationToken.None)
                .ConfigureAwait(true);

            CommentStatus = result.Message;
            if (result.Success) item.ApplyScore(result.Score);
        }
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
                 })
        {
            OnPropertyChanged(property);
        }
    }
}

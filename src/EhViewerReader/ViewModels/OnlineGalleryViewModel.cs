using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerReader.Core.Online;
using EhViewerReader.Services;

namespace EhViewerReader.ViewModels;

/// <summary>
/// Online gallery detail: metadata, tags, favourite slot and the two entry
/// points that matter — read online and download.
/// </summary>
public sealed partial class OnlineGalleryViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly IOnlineHost _host;
    private readonly EhGallerySummary _summary;
    private EhGalleryDetails? _details;
    private bool _loading;
    private string _statusText = "载入中…";
    private IImageRequest? _coverRequest;
    private bool _isFavorite;
    private int _favoriteSlot = -1;
    private FeedOption? _selectedSlot;
    private bool _suppressSlotChange;

    public OnlineGalleryViewModel(AppServices services, IOnlineHost host, EhGallerySummary summary)
    {
        _services = services;
        _host = host;
        _summary = summary;

        FavoriteSlots.Add(new FeedOption("-1", "未收藏"));
        for (int slot = 0; slot < 10; slot++)
        {
            FavoriteSlots.Add(new FeedOption(slot.ToString(), "分类 " + slot));
        }

        _selectedSlot = FavoriteSlots[0];
        _selectedSlot.IsActive = true;

        Title = summary.Title;
        if (summary.ThumbUrl.Length > 0)
        {
            _coverRequest = _services.OnlineImages.CreateThumbnailRequest(summary.ThumbUrl);
        }
    }

    public event EventHandler<EhGalleryDetails>? ReadRequested;

    public event EventHandler<string>? TagSearchRequested;

    public ObservableCollection<TagChip> Tags { get; } = [];
    public ObservableCollection<FeedOption> FavoriteSlots { get; } = [];

    public EhGallerySummary Summary => _summary;
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

    public string CategoryText => _details?.Category ?? _summary.CategoryDisplay;
    public IBrush CategoryBrush => new SolidColorBrush(Color.Parse(_details?.CategoryColor ?? _summary.CategoryColor));
    public string PagesText => (_details?.Pages ?? _summary.Pages) is int pages and > 0 ? $"{pages} 页" : "—";
    public string RatingText => _details is { Rating: > 0 } d ? d.Rating.ToString("F2") : _summary.RatingText;
    public string PostedText => _details?.Posted ?? _summary.Posted;
    public string UploaderText => _details?.Uploader ?? _summary.Uploader;
    public string FileSizeText => _details?.FileSize ?? "—";
    public string LanguageText => _details?.Language ?? "—";
    public string VisibleText => _details?.Visible ?? "—";
    public string ParentText => _details?.Parent ?? "—";

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

            if (details.ThumbUrl.Length > 0)
            {
                CoverRequest = _services.OnlineImages.CreateThumbnailRequest(details.ThumbUrl);
            }

            Tags.Clear();
            foreach (EhTag tag in details.Tags)
            {
                string term = tag.SearchTerm;
                Tags.Add(new TagChip
                {
                    Display = (tag.Namespace.Length > 0 ? tag.Namespace + ":" : string.Empty) + tag.Name,
                    Term = term,
                    NamespaceLabel = tag.Namespace,
                    Command = new RelayCommand(() => TagSearchRequested?.Invoke(this, term)),
                });
            }

            int slot = details.FavoriteSlot;
            if (IsSignedIn && slot < 0)
            {
                slot = await _services.Online
                    .GetFavoriteSlotAsync(_summary.Gid, _summary.Token, CancellationToken.None)
                    .ConfigureAwait(true);
            }

            SetSlot(slot);
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

    private void SetSlot(int slot)
    {
        _favoriteSlot = slot;
        IsFavorite = slot >= 0;
        _suppressSlotChange = true;
        try
        {
            FeedOption? option = FavoriteSlots.FirstOrDefault(o => o.Key == slot.ToString());
            if (option is not null)
            {
                if (_selectedSlot is not null) _selectedSlot.IsActive = false;
                _selectedSlot = option;
                option.IsActive = true;
                OnPropertyChanged(nameof(SelectedSlot));
            }
        }
        finally
        {
            _suppressSlotChange = false;
        }
    }

    private async Task ApplySlotAsync(FeedOption option)
    {
        if (_details is null) return;
        if (!IsSignedIn)
        {
            _host.Notify("收藏需要先登录账号");
            SetSlot(_favoriteSlot);
            return;
        }

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
                SetSlot(add ? target : -1);
                StatusText = add ? "已收藏到分类 " + target : "已取消收藏";
                _host.Notify(StatusText);
            }
            else
            {
                StatusText = "收藏操作未成功";
                SetSlot(_favoriteSlot);
            }
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            StatusText = "收藏失败：" + ex.Message;
            SetSlot(_favoriteSlot);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await InitializeAsync();

    [RelayCommand]
    private void Read()
    {
        if (_details is null) return;
        ReadRequested?.Invoke(this, _details);
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (_details is null) return;
        await _host.DownloadDetailsAsync(_details);
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = Url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host.Notify("无法打开浏览器：" + ex.Message);
        }
    }

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

    private void RaiseMetadata()
    {
        foreach (string property in new[]
                 {
                     nameof(JapaneseTitle), nameof(HasJapaneseTitle), nameof(CategoryText), nameof(CategoryBrush),
                     nameof(PagesText), nameof(RatingText), nameof(PostedText), nameof(UploaderText),
                     nameof(FileSizeText), nameof(LanguageText), nameof(VisibleText), nameof(ParentText),
                     nameof(MetaLine), nameof(Title),
                 })
        {
            OnPropertyChanged(property);
        }
    }
}

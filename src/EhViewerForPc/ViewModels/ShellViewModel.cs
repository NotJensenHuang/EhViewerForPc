using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Core.Download;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Services;
using FluentAvalonia.UI.Controls;

namespace EhViewerForPc.ViewModels;

/// <summary>One entry of the navigation rail.</summary>
public sealed partial class NavItem : ObservableObject
{
    [ObservableProperty] private bool _isEnabled = true;

    public NavItem(string key, string label, object page)
    {
        Key = key;
        Label = label;
        Page = page;
        IconSource = new FASymbolIconSource { Symbol = key switch
        {
            "online" => FASymbol.Globe,
            "favorites" => FASymbol.Favorite,
            "history" => FASymbol.Clock,
            "downloads" => FASymbol.Download,
            "library" => FASymbol.Library,
            _ => FASymbol.Settings,
        } };
    }

    public string Key { get; }
    public string Label { get; }
    public FAIconSource IconSource { get; }
    public object Page { get; }

}

/// <summary>
/// Application shell: owns every page view model, the navigation state, the
/// account/site header and the cross-page actions (open gallery, download,
/// search a tag).
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IOnlineHost, IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _toastTimer;
    private NavItem? _selectedNav;
    private string _toastText = string.Empty;
    private bool _showToast;
    private bool _disposed;
    private CancellationTokenSource? _imageUpdateCancellation;
    private bool _imageUpdateRunning, _imageUpdateDirty, _imageMaintenanceReady;
    private bool _imageResumeRunning, _imageResumeChecked;
    private EhImageLimits? _imageLimits;
    private DateTime _imageLimitsFetchedUtc = DateTime.MinValue;
    private bool _imageLimitsBusy;

    public ShellViewModel(AppServices services)
    {
        _services = services;

        Library = new MainViewModel(services);
        Online = new OnlineFeedViewModel(services, this, OnlineFeedMode.Front, "在线浏览",
            "E-Hentai / ExHentai 首页、热门、订阅与排行");
        Favorites = new OnlineFeedViewModel(services, this, OnlineFeedMode.Favorites, "收藏夹",
            "按 E-Hentai 收藏分类浏览，登录后可用");
        Bookmarks = new BookmarksViewModel(services, () => Online.CanBookmarkCurrent ? Online.CurrentRequest : null);
        Online.BookmarksRequested += (_, _) => ShowBookmarks();
        Online.BookmarksChanged += (_, _) => Bookmarks.Reload();
        Online.BookmarkOptionsSaved += (entry, request) => { Bookmarks.UpdateOptions(entry, request); ShowBookmarks(); };
        Bookmarks.OpenRequested += (_, entry) =>
        {
            BookmarksDismissRequested?.Invoke(this, EventArgs.Empty);
            _ = Online.OpenBookmarkAsync(entry);
            Navigate("online");
        };
        Bookmarks.EditRequested += (_, entry) =>
        {
            BookmarksDismissRequested?.Invoke(this, EventArgs.Empty);
            Online.EditBookmark(entry);
            Navigate("online");
        };
        Bookmarks.Changed += (_, _) => { Online.RefreshBookmarks(); };
        History = new HistoryViewModel(services, Library);
        History.OnlineRequested += (_, summary) => OpenGallery(summary);
        History.OpenLibraryRequested += (_, _) => Navigate("library");
        History.OpenSiteHistoryRequested += (_, _) =>
            OpenExternal(EhUrls.Home(services.Online.Site) + "history.php");
        Downloads = new DownloadsViewModel(services, Library);
        Settings = new SettingsViewModel(services);
        Settings.AccountChanged += (_, _) => RaiseAccount();
        Library.LocalIndexChanged += OnImageIndexLibraryChanged;
        Library.PropertyChanged += OnImageIndexLibraryPropertyChanged;
        services.Downloads.TaskCompleted += OnImageIndexDownloadCompleted;
        services.Downloads.TaskChanged += OnDownloadTaskChanged;

        // The local index drives the "已在本机" badges on every online listing.
        Library.LocalIndexChanged += (_, _) => { Online.RefreshLocalState(); Favorites.RefreshLocalState(); LocalStateChanged?.Invoke(this, EventArgs.Empty); };

        NavItems =
        [
            new NavItem("online", "在线浏览", Online),
            new NavItem("favorites", "收藏", Favorites),
            new NavItem("history", "阅读历史", History),
            new NavItem("downloads", "下载", Downloads),
            new NavItem("library", "本地库", Library),
            new NavItem("settings", "设置", Settings),
        ];
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.IsSaving))
                foreach (var item in NavItems) item.IsEnabled = !Settings.IsSaving;
        };

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ShowToast = false;
        };

        SelectedNav = NavItems[0];
    }

    public event EventHandler<EhGallerySummary>? GalleryRequested;
    public event EventHandler<NavItem>? NavigationRequested;
    public event EventHandler? LocalStateChanged;
    public event EventHandler? BookmarksRequested;
    public event EventHandler? BookmarksDismissRequested;

    private void ShowBookmarks()
    {
        if (Settings.IsSaving) return;
        Bookmarks.Reload();
        BookmarksRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when a local copy should be opened in the reader.</summary>
    public event EventHandler<GalleryItemViewModel>? LocalReadRequested;

    /// <summary>Raised by the sidebar or settings; the window opens the browser login.</summary>
    public event EventHandler? WebLoginRequested;

    public event EventHandler<string>? ToastRequested;

    public MainViewModel Library { get; }
    public OnlineFeedViewModel Online { get; }
    public OnlineFeedViewModel Favorites { get; }
    /// <summary>Gets the ordered search bookmark manager.</summary>
    public BookmarksViewModel Bookmarks { get; }
    public HistoryViewModel History { get; }
    public DownloadsViewModel Downloads { get; }
    public SettingsViewModel Settings { get; }

    public ObservableCollection<NavItem> NavItems { get; }

    public NavItem? SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedNav)) return;
            if (Settings.IsSaving) return;
            _selectedNav = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(CurrentPageTitle));
            ActivatePage(value.Page);
            NavigationRequested?.Invoke(this, value);
        }
    }

    public object? CurrentPage => _selectedNav?.Page;

    public string CurrentPageTitle => _selectedNav?.Label ?? string.Empty;

    public string SiteLabel => EhUrls.SiteName(_services.Online.Site);

    public string SiteBadge => _services.Online.Site == EhSite.ExHentai ? "EX" : "EH";

    public bool IsExSite => _services.Online.Site == EhSite.ExHentai;

    public bool IsSignedIn => _services.Online.IsSignedIn;

    public string AccountLabel => IsSignedIn
        ? "已登录" + (string.IsNullOrEmpty(_services.Online.UserName) ? string.Empty : " · #" + _services.Online.UserName)
        : "未登录";

    public string AccountDetail => IsSignedIn
        ? (_services.Online.ExAccess ? "ExHentai 可访问" : "ExHentai 可能不可用")
        : "登录后可收藏、关注与下载";

    /// <summary>
    /// Image Limits reading shown under the account line. The site answers a
    /// 509 placeholder once the quota is used up, so the number is the fastest
    /// way to tell "my quota" from "this H@H node refuses the page".
    /// </summary>
    public bool ShowImageLimit => IsSignedIn;

    public string ImageLimitLabel => _imageLimits switch
    {
        { IsUnrestricted: true } => "图片额度：无限制",
        { IsExceeded: true } limits => $"图片额度 {Count(limits.Used)} / {Count(limits.Maximum)} · 已超限",
        { } limits => $"图片额度 {Count(limits.Used)} / {Count(limits.Maximum)}",
        _ => _imageLimitsBusy ? "图片额度：读取中…" : "图片额度：未读取",
    };

    public string ImageLimitDetail => _imageLimits switch
    {
        { IsUnrestricted: true } =>
            "站点当前未限制图片（按 IP 记账）。若仍出现 509，说明是该页图床节点拒绝，不是额度问题。点击这一行可刷新。",
        { } limits => $"图片额度 {Count(limits.Used)} / {Count(limits.Maximum)}。"
                      + "普通账号按 IP 记账，约每分钟恢复 3–5 次；用尽时图片返回 509。点击这一行可刷新。",
        _ => "图片额度尚未读取到。点击这一行重试；站点页面不可达时这里不会显示数字。",
    };

    /// <summary>浮出层用的短说明；刷新提示由旁边的按钮承担。</summary>
    public string ImageLimitNote => _imageLimits switch
    {
        { IsUnrestricted: true } => "站点当前未限制图片（按 IP 记账）。仍出现 509 说明是该页图床节点拒绝。",
        { } => "按 IP 记账，约每分钟恢复 3–5 次；用尽时图片返回 509。",
        _ => "尚未读取到；站点页面不可达时不会显示数字。",
    };

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    [RelayCommand]
    private async Task RefreshImageLimitsAsync()
    {
        if (_imageLimitsBusy || !IsSignedIn) return;
        _imageLimitsBusy = true;
        RaiseImageLimits();
        try
        {
            EhImageLimits? limits = await _services.Online.GetImageLimitsAsync(CancellationToken.None).ConfigureAwait(true);
            _imageLimitsFetchedUtc = DateTime.UtcNow;
            if (limits is not null) _imageLimits = limits;
        }
        finally
        {
            _imageLimitsBusy = false;
            RaiseImageLimits();
        }
    }

    private void RaiseImageLimits()
    {
        OnPropertyChanged(nameof(ShowImageLimit));
        OnPropertyChanged(nameof(ImageLimitLabel));
        OnPropertyChanged(nameof(ImageLimitDetail));
        OnPropertyChanged(nameof(ImageLimitNote));
    }

    /// <summary>Throttled refresh for automatic triggers (startup, sign-in, 509).</summary>
    private void MaybeRefreshImageLimits()
    {
        if (!IsSignedIn) return;
        if (DateTime.UtcNow - _imageLimitsFetchedUtc < TimeSpan.FromSeconds(30)) return;
        _ = RefreshImageLimitsAsync();
    }

    private void OnDownloadTaskChanged(object? sender, DownloadTask task)
    {
        if (!task.Message.Contains("509", StringComparison.Ordinal)) return;
        Dispatcher.UIThread.Post(MaybeRefreshImageLimits);
    }

    public string ToastText
    {
        get => _toastText;
        private set => SetProperty(ref _toastText, value);
    }

    public bool ShowToast
    {
        get => _showToast;
        private set => SetProperty(ref _showToast, value);
    }

    public async Task InitializeAsync()
    {
        _services.OnlineImages.StartBackgroundMaintenance();
        _ = Task.Run(BrowserLoginSession.CleanupStaleProfiles);
        _services.EnsureDownloadSource();
        _services.Downloads.AutoStart();
        await Library.InitializeAsync().ConfigureAwait(true);
        _imageMaintenanceReady = true;
        _ = ResumeImageIndexAsync();
        Settings.RefreshAccount();
        MaybeRefreshImageLimits();
        await Online.LoadAsync(reset: true).ConfigureAwait(true);
    }

    private void ActivatePage(object page)
    {
        switch (page)
        {
            case OnlineFeedViewModel feed when feed.Items.Count == 0 && !feed.IsBusy && !feed.IsEditingBookmark:
                _ = feed.LoadAsync(reset: true);
                break;
            case SettingsViewModel:
                Settings.RefreshAccount();
                Settings.RefreshSources();
                break;
            case DownloadsViewModel downloads:
                downloads.SyncAll();
                break;
            case HistoryViewModel history:
                history.Reload();
                break;
        }
    }

    [RelayCommand]
    private void SelectNav(NavItem? item)
    {
        if (item is null) return;
        if (ReferenceEquals(item, SelectedNav)) NavigationRequested?.Invoke(this, item);
        else SelectedNav = item;
    }

    public void Navigate(string key)
    {
        if (key == "bookmarks") { ShowBookmarks(); return; }
        NavItem? item = NavItems.FirstOrDefault(n => n.Key == key);
        SelectNav(item);
    }

    [RelayCommand]
    private async Task ToggleSiteAsync()
    {
        EhSite next = _services.Online.Site == EhSite.EHentai ? EhSite.ExHentai : EhSite.EHentai;
        _services.Online.SwitchSite(next);
        _services.TrySaveSettings(out _);
        RaiseAccount();

        Notify(next == EhSite.ExHentai
            ? "已切换到 ExHentai" + (_services.Online.ExAccess ? string.Empty : "（账号可能无访问权限）")
            : "已切换到 E-Hentai");

        await Online.LoadAsync(reset: true).ConfigureAwait(true);
        if (Favorites.Items.Count > 0) await Favorites.LoadAsync(reset: true).ConfigureAwait(true);
    }

    [RelayCommand]
    private void SignOut()
    {
        _services.Online.SignOut();
        _services.TrySaveSettings(out _);
        Settings.RefreshAccount();
        RaiseAccount();
        Notify("已退出登录");
    }

    [RelayCommand]
    public void ShowDownloads() => Navigate("downloads");

    [RelayCommand]
    private void OpenWebLogin() => WebLoginRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Applies a finished browser sign-in across the shell.</summary>
    public void ApplyLoginResult(EhLoginResult result)
    {
        Settings.ApplyLoginResult(result);
        RaiseAccount();
        Online.RefreshProgress();
        Favorites.RefreshProgress();
        Notify(result.Message);
    }

    [RelayCommand]
    private void OpenSettings() => Navigate("settings");

    public EhSite Site => _services.Online.Site;

    public void OpenGallery(EhGallerySummary summary) => GalleryRequested?.Invoke(this, summary);

    public bool HasLocalCopy(long gid, bool requireComplete = true) => requireComplete ? Library.HasLocalCopy(gid) : Library.TryGetAnyLocalCopy(gid, out _);

    public bool OpenLocal(long gid, bool allowIncomplete = false)
    {
        GalleryItemViewModel? item;
        bool found = allowIncomplete ? Library.TryGetAnyLocalCopy(gid, out item) : Library.TryGetLocalCopy(gid, out item);
        if (!found || item is null) return false;
        LocalReadRequested?.Invoke(this, item);
        return true;
    }

    public void OpenExternal(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Notify("无法打开浏览器：" + ex.Message);
        }
    }

    public async Task DownloadAsync(EhGallerySummary summary)
    {
        try
        {
            Notify("正在读取画廊信息…");
            EhGalleryDetails details = await _services.Online
                .GetDetailsAsync(summary.Gid, summary.Token, CancellationToken.None)
                .ConfigureAwait(true);
            await DownloadDetailsAsync(details, _services.Settings.ImageQuality).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            Notify("下载失败：" + ex.Message);
        }
    }

    public async Task DownloadDetailsAsync(EhGalleryDetails details, ImageQuality quality)
    {
        _services.Works.Upsert(WorkRecord.FromDetails(details));
        bool existed = _services.Downloads.IsQueued(details.Gid);
        var queued = await Task.Run(() => _services.Downloads.Enqueue(details, quality: quality));
        _services.EnsureDownloadSource();
        Notify(queued.IsFinished ? "下载已完成，可从本地库阅读" : existed ? "该作品已在下载队列中" : $"已加入下载队列：{details.DisplayTitle}");
        Downloads.SyncAll();
    }

    public void SearchTag(string query)
    {
        Navigate("online");
        Online.RunSearch(query);
    }

    public void Notify(string message)
    {
        ToastText = message;
        ShowToast = true;
        _toastTimer.Stop();
        _toastTimer.Start();
        ToastRequested?.Invoke(this, message);
        _services.Log.Info(message);
    }

    public void RaiseAccount()
    {
        // 退出登录后不能继续显示上一个账号的额度读数。
        if (!IsSignedIn) { _imageLimits = null; _imageLimitsFetchedUtc = DateTime.MinValue; }
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(AccountLabel));
        OnPropertyChanged(nameof(AccountDetail));
        OnPropertyChanged(nameof(SiteLabel));
        OnPropertyChanged(nameof(SiteBadge));
        OnPropertyChanged(nameof(IsExSite));
        RaiseImageLimits();
        Settings.RefreshAccount();
        MaybeRefreshImageLimits();
        Online.RefreshProgress();
        Favorites.RefreshFavoriteNames();
        Favorites.RefreshProgress();
        History.Reload();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Library.LocalIndexChanged -= OnImageIndexLibraryChanged;
        Library.PropertyChanged -= OnImageIndexLibraryPropertyChanged;
        _services.Downloads.TaskCompleted -= OnImageIndexDownloadCompleted;
        _services.Downloads.TaskChanged -= OnDownloadTaskChanged;
        _imageUpdateCancellation?.Cancel();
        _toastTimer.Stop();
        Downloads.Dispose();
        History.Dispose();
        Library.Dispose();
        Settings.Dispose();
        Online.Dispose(); Favorites.Dispose();
    }

    private void OnImageIndexLibraryChanged(object? sender, EventArgs e)
    {
        if (!_imageMaintenanceReady) return;
        _ = ResumeImageIndexAsync();
        QueueImageIndexUpdate();
    }
    private void OnImageIndexLibraryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_imageMaintenanceReady && e.PropertyName == nameof(MainViewModel.IsScanning) && !Library.IsScanning) _ = ResumeImageIndexAsync();
    }
    private async Task ResumeImageIndexAsync()
    {
        if (_disposed || _imageResumeRunning || _imageResumeChecked || Library.IsScanning) return;
        _imageResumeRunning = true;
        _imageResumeChecked = true;
        try
        {
            var galleries = Library.SnapshotAllWorks().SelectMany(w => w.Versions).DistinctBy(g => (g.SourceId, g.StableId))
                .Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
            await _services.ImageSearch.ResumePendingBuildAsync(galleries);
            if (_services.Settings.ImageSearch.Enabled && !_services.ImageSearch.IsBuilding)
                await _services.ImageSearch.CleanupAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _services.Log.Warn("Resume image index: " + error.Message); }
        finally { _imageResumeRunning = false; }
    }
    private void OnImageIndexDownloadCompleted(object? sender, EhViewerForPc.Core.Download.DownloadTask task)
        => Dispatcher.UIThread.Post(QueueImageIndexUpdate);
    private void QueueImageIndexUpdate()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(QueueImageIndexUpdate); return; }
        if (_disposed || !_services.Settings.ImageSearch.Enabled || !_services.Settings.ImageSearch.AutoUpdateAfterDownload) return;
        // Automatic maintenance starts only after an explicit first build.
        if (!File.Exists(Path.Combine(_services.ImageSearch.DirectoryPath, "current.json"))) return;
        _imageUpdateDirty = true;
        if (_imageUpdateRunning) return;
        _imageUpdateCancellation?.Cancel(); _imageUpdateCancellation = new();
        _ = UpdateImageIndexAfterQuietPeriodAsync(_imageUpdateCancellation.Token);
    }
    private async Task UpdateImageIndexAfterQuietPeriodAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            if (_disposed || !_services.Settings.ImageSearch.Enabled || !_services.Settings.ImageSearch.AutoUpdateAfterDownload) return;
            _imageUpdateRunning = true;
            while (_imageUpdateDirty && !token.IsCancellationRequested)
            {
                _imageUpdateDirty = false;
                while (_services.ImageSearch.IsBuilding) await Task.Delay(500, token);
                var copies = Library.SnapshotAllWorks().SelectMany(item => item.Versions).DistinctBy(g => (g.SourceId, g.StableId))
                    .Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
                await _services.ImageSearch.UpdateAutomaticallyAsync(copies, token);
                if (_imageUpdateDirty) await Task.Delay(TimeSpan.FromSeconds(10), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _services.Log.Warn("Image index update: " + error.GetType().Name); }
        finally { _imageUpdateRunning = false; }
    }
}

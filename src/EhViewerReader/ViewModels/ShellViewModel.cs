using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerReader.Core.Online;
using EhViewerReader.Services;

namespace EhViewerReader.ViewModels;

/// <summary>One entry of the navigation rail.</summary>
public sealed partial class NavItem : ObservableObject
{
    private bool _isActive;

    public NavItem(string key, string label, string glyph, object page)
    {
        Key = key;
        Label = label;
        Glyph = glyph;
        Page = page;
    }

    public string Key { get; }
    public string Label { get; }
    public string Glyph { get; }
    public object Page { get; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
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

    public ShellViewModel(AppServices services)
    {
        _services = services;

        Library = new MainViewModel(services);
        Online = new OnlineFeedViewModel(services, this, OnlineFeedMode.Front, "在线浏览",
            "E-Hentai / ExHentai 首页、热门、关注与排行");
        Search = new SearchPageViewModel(services, this);
        Favorites = new OnlineFeedViewModel(services, this, OnlineFeedMode.Favorites, "收藏夹",
            "按 E-Hentai 收藏分类浏览，登录后可用");
        History = new OnlineFeedViewModel(services, this, OnlineFeedMode.History, "浏览历史",
            "账号在 E-Hentai 上的浏览记录");
        Downloads = new DownloadsViewModel(services, Library);
        Settings = new SettingsViewModel(services);
        Settings.AccountChanged += (_, _) => RaiseAccount();

        NavItems =
        [
            new NavItem("online", "在线浏览", "◎", Online),
            new NavItem("search", "搜索", "⌕", Search),
            new NavItem("favorites", "收藏", "♥", Favorites),
            new NavItem("history", "历史", "◷", History),
            new NavItem("downloads", "下载", "↓", Downloads),
            new NavItem("library", "本地库", "▤", Library),
            new NavItem("settings", "设置", "⚙", Settings),
        ];

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ShowToast = false;
        };

        SelectedNav = NavItems[0];
    }

    public event EventHandler<EhGallerySummary>? GalleryRequested;

    public event EventHandler<string>? ToastRequested;

    public MainViewModel Library { get; }
    public OnlineFeedViewModel Online { get; }
    public SearchPageViewModel Search { get; }
    public OnlineFeedViewModel Favorites { get; }
    public OnlineFeedViewModel History { get; }
    public DownloadsViewModel Downloads { get; }
    public SettingsViewModel Settings { get; }

    public ObservableCollection<NavItem> NavItems { get; }

    public NavItem? SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedNav)) return;
            if (_selectedNav is not null) _selectedNav.IsActive = false;
            _selectedNav = value;
            value.IsActive = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(CurrentPageTitle));
            ActivatePage(value.Page);
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
        _ = Task.Run(() => _services.OnlineImages.PruneCache(TimeSpan.FromDays(14)));
        _services.EnsureDownloadSource();
        _services.Downloads.AutoStart();
        await Library.InitializeAsync().ConfigureAwait(true);
        Settings.RefreshAccount();
        await Online.LoadAsync(reset: true).ConfigureAwait(true);
    }

    private void ActivatePage(object page)
    {
        switch (page)
        {
            case OnlineFeedViewModel feed when feed.Items.Count == 0 && !feed.IsBusy:
                _ = feed.LoadAsync(reset: true);
                break;
            case SearchPageViewModel:
                break;
            case SettingsViewModel:
                Settings.RefreshAccount();
                break;
            case DownloadsViewModel downloads:
                downloads.SyncAll();
                break;
        }
    }

    [RelayCommand]
    private void SelectNav(NavItem? item)
    {
        if (item is not null) SelectedNav = item;
    }

    public void Navigate(string key)
    {
        NavItem? item = NavItems.FirstOrDefault(n => n.Key == key);
        if (item is not null) SelectedNav = item;
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
        if (History.Items.Count > 0) await History.LoadAsync(reset: true).ConfigureAwait(true);
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
    private void OpenSettings() => Navigate("settings");

    public EhSite Site => _services.Online.Site;

    public void OpenGallery(EhGallerySummary summary) => GalleryRequested?.Invoke(this, summary);

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
            await DownloadDetailsAsync(details).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            Notify("下载失败：" + ex.Message);
        }
    }

    public Task DownloadDetailsAsync(EhGalleryDetails details)
    {
        _services.Downloads.Enqueue(details);
        _services.EnsureDownloadSource();
        Notify($"已加入下载队列：{details.DisplayTitle}");
        Navigate("downloads");
        Downloads.SyncAll();
        return Task.CompletedTask;
    }

    public void SearchTag(string query)
    {
        Search.Query = query;
        Navigate("search");
        Search.SearchCommand.Execute(null);
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
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(AccountLabel));
        OnPropertyChanged(nameof(AccountDetail));
        OnPropertyChanged(nameof(SiteLabel));
        OnPropertyChanged(nameof(SiteBadge));
        OnPropertyChanged(nameof(IsExSite));
        Settings.RefreshAccount();
        Online.RefreshProgress();
        Favorites.RefreshFavoriteNames();
        Favorites.RefreshProgress();
        History.RefreshProgress();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _toastTimer.Stop();
        Downloads.Dispose();
        Library.Dispose();
        Settings.Dispose();
    }
}

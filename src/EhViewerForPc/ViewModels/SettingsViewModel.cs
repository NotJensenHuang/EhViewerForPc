using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Download;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Services;

namespace EhViewerForPc.ViewModels;

/// <summary>One editable row in the settings source list.</summary>
public sealed class SourceRowViewModel : ObservableObject
{
    private bool _enabled;
    private bool _indexImages = true;

    public SourceRowViewModel(LibrarySource source)
    {
        Source = source;
        _enabled = source.Enabled;
    }

    /// <summary>Gets or sets whether this source participates in image retrieval.</summary>
    public bool IndexImages { get => _indexImages; set => SetProperty(ref _indexImages, value); }

    public LibrarySource Source { get; private set; }
    public string DisplayName => Source.DisplayName;
    public string RootPath => Source.RootPath;
    public bool IsMissing => !Source.Exists;
    public string StatusText => Source.Exists ? string.Empty : "目录不可用";

    public void UpdateSource(LibrarySource source) { Source = source; NotifyPathChanged(); }
    public void NotifyPathChanged() { OnPropertyChanged(nameof(RootPath)); OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(IsMissing)); OnPropertyChanged(nameof(StatusText)); }
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value)) return;

        }
    }
}

/// <summary>One editable list filter rule.</summary>
public sealed partial class FilterRuleViewModel : ObservableObject
{
    private readonly FilterRule _rule;

    public FilterRuleViewModel(FilterRule rule)
    {
        _rule = rule;
    }

    public FilterRule Rule => _rule;

    public string Text
    {
        get => _rule.Text;
        set
        {
            if (_rule.Text == value) return;
            _rule.Text = value;
            OnPropertyChanged();
        }
    }

    public bool Enabled
    {
        get => _rule.Enabled;
        set
        {
            if (_rule.Enabled == value) return;
            _rule.Enabled = value;
            OnPropertyChanged();
        }
    }

    public bool Include
    {
        get => _rule.Include;
        set
        {
            if (_rule.Include == value) return;
            _rule.Include = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeText));
        }
    }

    public int TargetIndex
    {
        get => (int)_rule.Target;
        set
        {
            var target = (FilterTarget)Math.Clamp(value, 0, 2);
            if (_rule.Target == target) return;
            _rule.Target = target;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeText));
        }
    }

    public string ModeText => _rule.Include ? "仅显示" : "隐藏";

    public string[] TargetLabels { get; } = ["标签", "标题", "上传者"];
    public string[] IncludeLabels { get; } = ["隐藏", "仅显示"];

    public int IncludeIndex
    {
        get => _rule.Include ? 1 : 0;
        set => Include = value == 1;
    }
}

/// <summary>One editable favourite-slot name.</summary>
public sealed partial class FavoriteSlotRowViewModel : ObservableObject
{
    private string _name;

    public FavoriteSlotRowViewModel(int slot, string name)
    {
        Slot = slot;
        _name = name;
    }

    public int Slot { get; }
    public string SlotLabel => "分类 " + Slot;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }
}

/// <summary>
/// Settings for every subsystem: account, network (proxy / DoH / hosts),
/// downloads, reader defaults and local sources.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private bool _imageSearchEnabled;
    private string _imageSearchDirectory = "";
    private int _imageSearchProbeIndex = 1;
    private bool _imageSearchAutoUpdate = true;

    /// <summary>Gets or sets the unsaved local image search switch.</summary>
    public bool ImageSearchEnabled { get => _imageSearchEnabled; set => SetProperty(ref _imageSearchEnabled, value); }
    /// <summary>Gets or sets the unsaved index storage directory.</summary>
    public string ImageSearchDirectory { get => _imageSearchDirectory; set => SetProperty(ref _imageSearchDirectory, value ?? ""); }
    /// <summary>Gets or sets the unsaved speed/recall setting.</summary>
    public int ImageSearchProbeIndex { get => _imageSearchProbeIndex; set => SetProperty(ref _imageSearchProbeIndex, Math.Clamp(value, 0, 2)); }
    /// <summary>Gets or sets whether completed downloads schedule index updates.</summary>
    public bool ImageSearchAutoUpdate { get => _imageSearchAutoUpdate; set => SetProperty(ref _imageSearchAutoUpdate, value); }
    /// <summary>Gets the localized retrieval presets.</summary>
    public string[] ImageSearchProbeLabels { get; } = ["快速", "均衡", "精细"];
    /// <summary>Requests the same image search document as the library toolbar.</summary>
    public event EventHandler? ImageSearchRequested;
    [RelayCommand] private void ManageImageSearch() => ImageSearchRequested?.Invoke(this, EventArgs.Empty);
    private string ImageSearchSourceSelection => string.Join("\n", Sources.Where(row => row.IndexImages).Select(row => row.Source.Id).Order(StringComparer.Ordinal));
    private int _themeIndex;
    [ObservableProperty]
    private bool _closeToTray;
    private int _modeIndex;
    private int _fitIndex;
    private int _backgroundIndex;
    private bool _rightToLeft;
    private double _maxMegapixels;
    private double _pageCacheMegabytes;
    private double _coverDecodeWidth;

    private int _siteIndex;
    private string _userNameInput = string.Empty;
    private string _passwordInput = string.Empty;
    private string _cookieInput = string.Empty;
    private string _accountStatus = string.Empty;
    private bool _accountBusy;

    private int _proxyModeIndex;
    private string _proxyHost = string.Empty;
    private string _proxyPort = string.Empty;
    private string _proxyUserName = string.Empty;
    private string _proxyPassword = string.Empty;
    private bool _proxyForImages;
    private bool _useDoh;
    private string _dohEndpoint = string.Empty;
    private string _hostsOverride = string.Empty;
    private string _networkStatus = string.Empty;

    private string _downloadRoot = string.Empty;
    private bool _downloadAddAsSource;
    private double _maxTasks;
    private double _maxPages;
    private bool _writeThumb;
    private bool _writeHeader;
    private bool _autoStart;
    private bool _refreshAfterDownload;
    private string _defaultDownloadLabel = string.Empty;
    private string _downloadStatus = string.Empty;

    private string _networkSnapshot;
    private readonly Dictionary<string, object?> _editBaseline = new();
    private readonly HashSet<string> _removedSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _enabledSnapshot = new(StringComparer.OrdinalIgnoreCase);

    private bool _showTagTranslations;
    private string _tagTranslationUrl = string.Empty;
    private string _tagTranslationStatus = string.Empty;
    private bool _updatingTags;

    private int _imageQualityIndex;
    private int _resampledWidthIndex;
    private double _onlineCacheMegabytes;
    private double _onlineCacheDays;
    private string _cacheUsageText = "正在统计…";
    private string _rescanStatus = "全量扫描会重新读取所有数据源；单目录下载仍会自动增量更新";
    private bool _isRescanning;
    private bool _isSaving;
    private CancellationTokenSource? _moveCancellation;
    public string? LastMovedRoot { get; private set; }
    public string? PreviousDownloadRoot { get; private set; }
    private int _downloadDirectoryMode;
    public string[] DownloadDirectoryModes { get; } = ["仅更改后续下载位置（不搬迁文件）", "搬迁已有下载并同步路径"];
    public int DownloadDirectoryMode
    {
        get => _downloadDirectoryMode;
        set { if (SetProperty(ref _downloadDirectoryMode, Math.Clamp(value, 0, 1))) { OnPropertyChanged(nameof(SaveLabel)); OnPropertyChanged(nameof(DownloadDirectoryHint)); } }
    }
    public string DownloadDirectoryHint => DownloadDirectoryMode == 0
        ? "仅保存新位置：后续下载使用新目录；已有文件和已有任务保留原位置。注册本地库后，新目录与旧目录都可继续阅读。"
        : "暂停任务、搬迁已有文件并同步下载记录与本地库，再继续下载。目标目录须为空；取消时保留用户原有的目标文件夹。";
    public bool CanCancelMove => IsSaving && _moveCancellation is not null;
    public bool IsSaving { get => _isSaving; private set { if (SetProperty(ref _isSaving, value)) { OnPropertyChanged(nameof(SaveLabel)); OnPropertyChanged(nameof(CanCancelMove)); OnPropertyChanged(nameof(CanStartRescan)); } } }
    public string SaveLabel => IsSaving ? "正在保存…" : !IsDownloadRootChanged ? "保存设置" : DownloadDirectoryMode == 1 ? "保存并搬迁下载" : "保存设置（不搬迁）";
    public bool IsDownloadRootChanged => !PathIdentity.Comparer.Equals(DownloadRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), _services.Downloads.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    [RelayCommand] private void CancelMove() => _moveCancellation?.Cancel();
    private string _filterStatus = string.Empty;

    private string _favoriteSlotStatus = string.Empty;
    private string _hathStatus = string.Empty;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        AppSettings settings = services.Settings;
        _imageSearchEnabled = settings.ImageSearch.Enabled;
        _imageSearchDirectory = settings.ImageSearch.StorageDirectory;
        _imageSearchProbeIndex = Array.IndexOf(new[] { 4, 16, 64 }, settings.ImageSearch.NProbe);
        _imageSearchAutoUpdate = settings.ImageSearch.AutoUpdateAfterDownload;
        _themeIndex = (int)settings.Theme;
        _closeToTray = settings.CloseToTray;
        _modeIndex = (int)settings.DefaultReadingMode;
        _fitIndex = (int)settings.DefaultFitMode;
        _backgroundIndex = (int)settings.ReaderBackground;
        _rightToLeft = settings.RightToLeft;
        _maxMegapixels = settings.MaxDecodeMegapixels;
        _pageCacheMegabytes = settings.PageCacheMegabytes;
        _coverDecodeWidth = settings.CoverDecodeWidth;

        foreach (LibrarySource source in settings.Sources)
        {
            Sources.Add(new SourceRowViewModel(source) { IndexImages = settings.ImageSearch.SourceIds.Count == 0 || settings.ImageSearch.SourceIds.Contains(source.Id) });
            _enabledSnapshot[source.Id] = source.Enabled;
        }

        LoadAccountFields();
        LoadNetworkFields();
        LoadDownloadFields();
        LoadOnlineFields();
        _networkSnapshot = NetworkSnapshot();
        RefreshAccount();
        CaptureBaseline();
    }

    public event EventHandler? AccountChanged;

    /// <summary>The shell opens the browser login window; settings never own windows.</summary>
    public event EventHandler? WebLoginRequested;

    public string[] ThemeLabels { get; } = ["跟随系统", "深色", "浅色"];
    public string[] ModeLabels { get; } = ["单页", "纵向连续", "横向连续"];
    public string[] FitLabels { get; } = ["适应页面", "适应宽度", "适应高度", "原始大小"];
    public string[] BackgroundLabels { get; } = ["黑色", "深灰", "灰色", "白色"];
    public string[] SiteLabels { get; } = ["E-Hentai", "ExHentai"];
    public string[] ProxyModeLabels { get; } = ["不使用代理", "跟随系统", "HTTP 代理", "SOCKS5 代理"];
    public string[] FilterTargetLabels { get; } = ["标签", "标题", "上传者"];

    /// <summary>Settings categories, in the order the page presents them.</summary>
    public string[] Categories { get; } = ["账号", "网络", "浏览", "下载", "阅读", "资料库", "维护"];

    private int _selectedCategoryIndex;

    /// <summary>Selected category of the settings page. UI state only; unsaved
    /// edits live in the individual fields and survive category switches.</summary>
    public int SelectedCategoryIndex
    {
        get => _selectedCategoryIndex;
        set => SetProperty(ref _selectedCategoryIndex, Math.Clamp(value, 0, Categories.Length - 1));
    }

    public ObservableCollection<SourceRowViewModel> Sources { get; } = [];
    public ObservableCollection<FilterRuleViewModel> Filters { get; } = [];
    public ObservableCollection<FavoriteSlotRowViewModel> FavoriteSlots { get; } = [];

    public bool HasFilters => Filters.Count > 0;

    public bool SourcesChanged { get; private set; }

    /// <summary>True when a source was added, removed or enabled/disabled.</summary>
    public bool HasSourceChanges =>
        SourcesChanged ||
        Sources.Any(row => _enabledSnapshot.TryGetValue(row.Source.Id, out bool was) && was != row.Enabled) ||
        Sources.Any(row => !_enabledSnapshot.ContainsKey(row.Source.Id));

    public bool HasSources => Sources.Count > 0;

    // ------------------------------------------------------------- reader

    public int ThemeIndex
    {
        get => _themeIndex;
        set
        {
            if (!SetProperty(ref _themeIndex, Math.Clamp(value, 0, 2))) return;
            App.ApplyTheme((AppTheme)_themeIndex);
        }
    }

    public int ModeIndex
    {
        get => _modeIndex;
        set => SetProperty(ref _modeIndex, Math.Clamp(value, 0, 2));
    }

    public int FitIndex
    {
        get => _fitIndex;
        set => SetProperty(ref _fitIndex, Math.Clamp(value, 0, 3));
    }

    public int BackgroundIndex
    {
        get => _backgroundIndex;
        set => SetProperty(ref _backgroundIndex, Math.Clamp(value, 0, 3));
    }

    public bool RightToLeft
    {
        get => _rightToLeft;
        set => SetProperty(ref _rightToLeft, value);
    }

    public double MaxMegapixels
    {
        get => _maxMegapixels;
        set
        {
            if (SetProperty(ref _maxMegapixels, Math.Round(value))) OnPropertyChanged(nameof(MaxMegapixelsLabel));
        }
    }

    public string MaxMegapixelsLabel => $"{_maxMegapixels:F0} MP";

    public double PageCacheMegabytes
    {
        get => _pageCacheMegabytes;
        set
        {
            if (SetProperty(ref _pageCacheMegabytes, Math.Round(value))) OnPropertyChanged(nameof(PageCacheLabel));
        }
    }

    public string PageCacheLabel => $"{_pageCacheMegabytes:F0} MB";

    public double CoverDecodeWidth
    {
        get => _coverDecodeWidth;
        set
        {
            if (SetProperty(ref _coverDecodeWidth, Math.Round(value))) OnPropertyChanged(nameof(CoverDecodeLabel));
        }
    }

    public string CoverDecodeLabel => $"{_coverDecodeWidth:F0} px";

    public string DataDirectory => _services.Paths.BaseDirectory;

    // ------------------------------------------------------------ account

    public int SiteIndex
    {
        get => _siteIndex;
        set
        {
            if (!SetProperty(ref _siteIndex, Math.Clamp(value, 0, 1))) return;
            _services.Online.SwitchSite((EhSite)_siteIndex);
            _services.TrySaveSettings(out _);
            RefreshAccount();
            AccountChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string UserNameInput
    {
        get => _userNameInput;
        set => SetProperty(ref _userNameInput, value);
    }

    public string PasswordInput
    {
        get => _passwordInput;
        set => SetProperty(ref _passwordInput, value);
    }

    public string CookieInput
    {
        get => _cookieInput;
        set => SetProperty(ref _cookieInput, value);
    }

    public string AccountStatus
    {
        get => _accountStatus;
        private set => SetProperty(ref _accountStatus, value);
    }

    public bool AccountBusy
    {
        get => _accountBusy;
        private set => SetProperty(ref _accountBusy, value);
    }

    public bool IsSignedIn => _services.Online.IsSignedIn;

    public string AccountUserText => IsSignedIn
        ? "已登录" + (string.IsNullOrEmpty(_services.Online.UserName) ? string.Empty : " · ID " + _services.Online.UserName)
        : "未登录";

    public string ExAccessText => !IsSignedIn
        ? "—"
        : _services.Settings.Account.ExAccess ? "可访问" : "不可访问";

    public string LastSignInText => DateTime.TryParse(_services.Settings.Account.LastSignInUtc, out DateTime value)
        ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : "—";

    public void RefreshAccount()
    {
        OnPropertyChanged(nameof(IsSignedIn));
        if (!AccountBusy) AccountStatus = IsSignedIn ? "账号已登录，登录状态已保存。" : "未登录，可通过网页登录或导入 Cookie 登录。";
        OnPropertyChanged(nameof(AccountUserText));
        OnPropertyChanged(nameof(ExAccessText));
        OnPropertyChanged(nameof(LastSignInText));
        _siteIndex = (int)_services.Settings.Account.Site;
        OnPropertyChanged(nameof(SiteIndex));
    }

    private void LoadAccountFields()
    {
        _siteIndex = (int)_services.Settings.Account.Site;
        _userNameInput = _services.Settings.Account.LastUserName;
    }

    [RelayCommand]
    private void OpenWebLogin() => WebLoginRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Called by the shell after a successful browser sign-in.</summary>
    public void ApplyLoginResult(EhLoginResult result)
    {
        AccountStatus = result.Success && _services.Online.UserName.Length > 0
            ? result.Message + " · 账号 ID " + _services.Online.UserName
            : result.Message;
        if (!result.Success) return;

        PasswordInput = string.Empty;
        CookieInput = string.Empty;
        _services.TrySaveSettings(out _);
        RefreshAccount();
        AccountChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (AccountBusy) return;
        AccountBusy = true;
        AccountStatus = "登录中…";
        try
        {
            EhLoginResult result = await _services.Online
                .SignInAsync(UserNameInput, PasswordInput, CancellationToken.None)
                .ConfigureAwait(true);

            AccountStatus = result.RequiresBrowserLogin
                ? result.Message + "（已为你切换到网页登录）"
                : result.Message;

            if (result.Success)
            {
                PasswordInput = string.Empty;
                _services.TrySaveSettings(out _);
                RefreshAccount();
                AccountChanged?.Invoke(this, EventArgs.Empty);
            }
            else if (result.RequiresBrowserLogin)
            {
                WebLoginRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            AccountBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportCookiesAsync()
    {
        if (AccountBusy) return;

        var cookies = EhSession.ParseCookieString(CookieInput);
        if (cookies.Count == 0)
        {
            AccountStatus = "没有解析出 Cookie：至少需要 ipb_member_id 与 ipb_pass_hash";
            return;
        }

        AccountBusy = true;
        AccountStatus = "正在验证 Cookie…";
        try
        {
            EhLoginResult result = await _services.Online
                .CompleteBrowserSignInAsync(cookies, CancellationToken.None)
                .ConfigureAwait(true);

            ApplyLoginResult(result);
        }
        finally
        {
            AccountBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        _services.Online.SignOut();
        _services.TrySaveSettings(out _);
        AccountStatus = "已退出登录";
        RefreshAccount();
        AccountChanged?.Invoke(this, EventArgs.Empty);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CheckExAccessAsync()
    {
        if (!IsSignedIn)
        {
            AccountStatus = "请先登录";
            return;
        }

        AccountBusy = true;
        AccountStatus = "正在检测 ExHentai…";
        try
        {
            (bool access, bool known) = await _services.Online
                .ProbeExAccessAsync(CancellationToken.None)
                .ConfigureAwait(true);
            _services.Settings.Account.ExAccess = access;
            _services.TrySaveSettings(out _);
            AccountStatus = !known ? "检测失败（网络异常）" : access ? "ExHentai 可访问" : "账号无法访问 ExHentai";
            RefreshAccount();
            AccountChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            AccountBusy = false;
        }
    }

    [RelayCommand]
    private void OpenRegisterPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://forums.e-hentai.org/index.php?act=Reg&CODE=00",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AccountStatus = "无法打开浏览器：" + ex.Message;
        }
    }

    // ------------------------------------------------------------ network

    public int ProxyModeIndex
    {
        get => _proxyModeIndex;
        set
        {
            if (!SetProperty(ref _proxyModeIndex, Math.Clamp(value, 0, 3))) return;
            OnPropertyChanged(nameof(IsManualProxy));
        }
    }

    public bool IsManualProxy => _proxyModeIndex is 2 or 3;

    public string ProxyHost
    {
        get => _proxyHost;
        set => SetProperty(ref _proxyHost, value);
    }

    public string ProxyPort
    {
        get => _proxyPort;
        set => SetProperty(ref _proxyPort, value);
    }

    public string ProxyUserName
    {
        get => _proxyUserName;
        set => SetProperty(ref _proxyUserName, value);
    }

    public string ProxyPassword
    {
        get => _proxyPassword;
        set => SetProperty(ref _proxyPassword, value);
    }

    public bool ProxyForImages
    {
        get => _proxyForImages;
        set => SetProperty(ref _proxyForImages, value);
    }

    public bool UseDoh
    {
        get => _useDoh;
        set => SetProperty(ref _useDoh, value);
    }

    public string DohEndpoint
    {
        get => _dohEndpoint;
        set => SetProperty(ref _dohEndpoint, value);
    }

    public string HostsOverride
    {
        get => _hostsOverride;
        set => SetProperty(ref _hostsOverride, value);
    }

    public string NetworkStatus
    {
        get => _networkStatus;
        private set => SetProperty(ref _networkStatus, value);
    }

    private void LoadNetworkFields()
    {
        NetworkSettings network = _services.Settings.Network;
        _proxyModeIndex = (int)network.ProxyMode;
        _proxyHost = network.ProxyHost;
        _proxyPort = network.ProxyPort > 0 ? network.ProxyPort.ToString(CultureInfo.InvariantCulture) : string.Empty;
        _proxyUserName = network.ProxyUserName;
        _proxyPassword = network.ProxyPassword;
        _proxyForImages = network.ProxyForImages;
        _useDoh = network.DnsOverHttps;
        _dohEndpoint = network.DohEndpoint;
        _hostsOverride = network.HostsOverride;
    }

    private string NetworkSnapshot() => string.Join("|",
        _proxyModeIndex, _proxyHost, _proxyPort, _proxyUserName, _proxyPassword,
        _proxyForImages, _useDoh, _dohEndpoint, _hostsOverride);

    [RelayCommand]
    private void ApplyNetwork()
    {
        ApplyNetworkSettings(save: true);
        NetworkStatus = "网络设置已应用";
    }

    [RelayCommand]
    private async Task TestNetworkAsync()
    {
        NetworkStatus = "测试中…";
        ApplyNetworkSettings(save: true);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            EhGalleryListPage page = await _services.Online
                .GetFrontPageAsync(0, CancellationToken.None)
                .ConfigureAwait(true);
            stopwatch.Stop();
            NetworkStatus = $"连接正常 · {stopwatch.ElapsedMilliseconds} ms · 首页 {page.Items.Count} 项";
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            NetworkStatus = "连接失败：" + ex.Message;
        }
    }

    private void ApplyNetworkSettings(bool save)
    {
        NetworkSettings network = _services.Settings.Network;
        network.ProxyMode = (ProxyMode)_proxyModeIndex;
        network.ProxyHost = ProxyHost.Trim();
        network.ProxyPort = int.TryParse(ProxyPort, out int port) ? Math.Clamp(port, 0, 65535) : 0;
        network.ProxyUserName = ProxyUserName.Trim();
        network.ProxyPassword = ProxyPassword;
        network.ProxyForImages = ProxyForImages;
        network.DnsOverHttps = UseDoh;
        network.DohEndpoint = DohEndpoint.Trim();
        network.HostsOverride = HostsOverride;
        network.Normalize();

        string snapshot = NetworkSnapshot();
        bool changed = !string.Equals(snapshot, _networkSnapshot, StringComparison.Ordinal);
        _networkSnapshot = snapshot;

        if (save) _services.TrySaveSettings(out _);
        if (changed)
        {
            _services.ReloadNetwork();
            NetworkStatus = "网络栈已重建";
        }
    }

    // -------------------------------------------------------------- online

    public bool ShowTagTranslations
    {
        get => _showTagTranslations;
        set => SetProperty(ref _showTagTranslations, value);
    }

    public string TagTranslationUrl
    {
        get => _tagTranslationUrl;
        set => SetProperty(ref _tagTranslationUrl, value);
    }

    public string TagTranslationStatus
    {
        get => _tagTranslationStatus;
        private set => SetProperty(ref _tagTranslationStatus, value);
    }

    public bool UpdatingTags
    {
        get => _updatingTags;
        private set => SetProperty(ref _updatingTags, value);
    }

    /// <summary>Index into <see cref="ImageQualityOptions"/>; shared by reader and downloads.</summary>
    public int ImageQualityIndex
    {
        get => _imageQualityIndex;
        set
        {
            if (SetProperty(ref _imageQualityIndex, value)) OnPropertyChanged(nameof(ImageQualityHint));
        }
    }

    public IReadOnlyList<FeedOption> ImageQualityOptions { get; } =
    [
        new FeedOption("orig", "原图（最大尺寸，需登录）"),
        new FeedOption("res", "站点压缩图（默认）"),
    ];

    public string[] ResampledWidths { get; } = ["780 px", "980 px", "1280 px", "1600 px", "2400 px"];
    public int ResampledWidthIndex { get => _resampledWidthIndex; set { if (SetProperty(ref _resampledWidthIndex, Math.Clamp(value, 0, 4))) OnPropertyChanged(nameof(ImageQualityHint)); } }
    private int SelectedResampledWidth => new[] { 780, 980, 1280, 1600, 2400 }[_resampledWidthIndex];
    public string ImageQualityHint =>
        _imageQualityIndex == 0
            ? "原图每页单独解析，流量最大；未登录或原图不可用时自动回退到站点压缩图。"
            : $"在线阅读与新下载默认请求 {SelectedResampledWidth}px 宽的压缩图。1600/2400px 是否可用取决于站点权限。";

    public string FilterStatus
    {
        get => _filterStatus;
        private set => SetProperty(ref _filterStatus, value);
    }

    // ------------------------------------------------------- online cache

    /// <summary>On-disk budget for online pages and thumbnails (MiB).</summary>
    public double OnlineCacheMegabytes
    {
        get => _onlineCacheMegabytes;
        set
        {
            if (SetProperty(ref _onlineCacheMegabytes, Math.Round(value))) OnPropertyChanged(nameof(OnlineCacheLabel));
        }
    }

    public string OnlineCacheLabel => $"{_onlineCacheMegabytes:F0} MB";

    /// <summary>Days an unused cache entry is kept; 0 means no time limit.</summary>
    public double OnlineCacheDays
    {
        get => _onlineCacheDays;
        set
        {
            if (SetProperty(ref _onlineCacheDays, Math.Round(value))) OnPropertyChanged(nameof(OnlineCacheDaysLabel));
        }
    }

    public string OnlineCacheDaysLabel => _onlineCacheDays <= 0 ? "不限制" : $"{_onlineCacheDays:F0} 天";

    public string CacheUsageText
    {
        get => _cacheUsageText;
        private set => SetProperty(ref _cacheUsageText, value);
    }

    /// <summary>Recomputes the directory size; called when the page is shown.</summary>
    public void RefreshCacheUsage()
    {
        CacheUsage usage = _services.OnlineImages.GetCacheUsage();
        CacheUsageText = "当前占用：" + usage.Text;
    }

    // ----------------------------------------------------- library rescan

    public string RescanStatus
    {
        get => _rescanStatus;
        private set => SetProperty(ref _rescanStatus, value);
    }

    public bool IsRescanning
    {
        get => _isRescanning;
        private set
        {
            if (SetProperty(ref _isRescanning, value)) OnPropertyChanged(nameof(CanStartRescan));
        }
    }

    public bool CanStartRescan => !_isRescanning && !IsSaving;

    /// <summary>Raised by the settings page; the shell runs the actual scan.</summary>
    public event EventHandler? RescanRequested;

    /// <summary>Raised by "取消扫描"; the shell cancels the running library scan.</summary>
    public event EventHandler? CancelRescanRequested;

    public void BeginRescan()
    {
        if (!CanStartRescan) return;
        IsRescanning = true;
        RescanStatus = "正在重新扫描全部来源…";
        RescanRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void StartRescan() => BeginRescan();

    public void CompleteRescan(string? error)
    {
        IsRescanning = false;
        RescanStatus = error is null ? "扫描完成" : "扫描未完成：" + error;
    }

    [RelayCommand]
    private void CancelRescan()
    {
        if (!IsRescanning) return;
        CancelRescanRequested?.Invoke(this, EventArgs.Empty);
        RescanStatus = "正在取消扫描…";
    }

    public string FavoriteSlotStatus
    {
        get => _favoriteSlotStatus;
        private set => SetProperty(ref _favoriteSlotStatus, value);
    }

    public string HathStatus
    {
        get => _hathStatus;
        private set => SetProperty(ref _hathStatus, value);
    }

    private void LoadOnlineFields()
    {
        OnlineSettings online = _services.Settings.Online;
        _showTagTranslations = online.ShowTagTranslations;
        _tagTranslationUrl = online.TagTranslationUrl;
        _imageQualityIndex = _services.Settings.ImageQuality == ImageQuality.Original ? 0 : 1;
        _resampledWidthIndex = Array.IndexOf(new[] { 780, 980, 1280, 1600, 2400 }, _services.Settings.ResampledWidth);
        _onlineCacheMegabytes = _services.Settings.OnlineCacheMegabytes;
        _onlineCacheDays = _services.Settings.OnlineCacheDays;
        // Do not parse the 2.5 MB database just to render the settings page; the
        // stored summary is enough until the user opens a gallery or updates it.
        _tagTranslationStatus = online.TagTranslationCount > 0
            ? $"{online.TagTranslationCount:N0} 条 · v{online.TagTranslationVersion}"
            : "未下载标签翻译库";

        Filters.Clear();
        foreach (FilterRule rule in online.Filters) Filters.Add(new FilterRuleViewModel(rule.Clone()));
        _filterStatus = Filters.Count == 0 ? "没有过滤规则" : $"{Filters.Count} 条规则";

        FavoriteSlots.Clear();
        for (int slot = 0; slot < 10; slot++)
        {
            FavoriteSlots.Add(new FavoriteSlotRowViewModel(slot, _services.Settings.Account.FavoriteSlotName(slot)));
        }

        _favoriteSlotStatus = "留空则显示默认名称（分类 0–9）";
        _hathStatus = "归档下载由站点生成；大文件归档需要账号已分配 H@H 客户端。";
    }

    [RelayCommand]
    private async Task UpdateTagTranslationsAsync()
    {
        if (UpdatingTags) return;
        UpdatingTags = true;
        TagTranslationStatus = "正在下载…";
        try
        {
            bool ok = await _services.TagTranslation
                .UpdateAsync(TagTranslationUrl, CancellationToken.None)
                .ConfigureAwait(true);
            TagTranslationStatus = _services.TagTranslation.StatusText;
            if (ok)
            {
                _services.Settings.Online.ShowTagTranslations = ShowTagTranslations;
                _services.TrySaveSettings(out _);
                AccountChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            UpdatingTags = false;
        }
    }

    [RelayCommand]
    private void AddFilter()
    {
        var rule = new FilterRule { Text = string.Empty, Target = FilterTarget.Tag };
        Filters.Add(new FilterRuleViewModel(rule));
        OnPropertyChanged(nameof(HasFilters));
        FilterStatus = "已添加规则，填写内容后保存";
    }

    [RelayCommand]
    private void RemoveFilter(FilterRuleViewModel? row)
    {
        if (row is null) return;
        Filters.Remove(row);
        OnPropertyChanged(nameof(HasFilters));
        FilterStatus = "已移除规则，保存后生效";
    }

    [RelayCommand]
    private void OpenHathPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = EhUrls.HathClient(_services.Online.Site),
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            HathStatus = "无法打开页面：" + ex.Message;
        }
    }

    // ----------------------------------------------------------- download

    public string DownloadRoot
    {
        get => _downloadRoot;
        set { if (SetProperty(ref _downloadRoot, value)) { OnPropertyChanged(nameof(IsDownloadRootChanged)); OnPropertyChanged(nameof(SaveLabel)); } }
    }

    public bool DownloadAddAsSource
    {
        get => _downloadAddAsSource;
        set => SetProperty(ref _downloadAddAsSource, value);
    }

    public double MaxConcurrentTasks
    {
        get => _maxTasks;
        set
        {
            if (SetProperty(ref _maxTasks, Math.Round(value))) OnPropertyChanged(nameof(MaxTasksLabel));
        }
    }

    public string MaxTasksLabel => $"{_maxTasks:F0} 个画廊";

    public double MaxConcurrentPages
    {
        get => _maxPages;
        set
        {
            if (SetProperty(ref _maxPages, Math.Round(value))) OnPropertyChanged(nameof(MaxPagesLabel));
        }
    }

    public string MaxPagesLabel => $"{_maxPages:F0} 页";

    public bool WriteThumb
    {
        get => _writeThumb;
        set => SetProperty(ref _writeThumb, value);
    }

    public bool WriteHeader
    {
        get => _writeHeader;
        set => SetProperty(ref _writeHeader, value);
    }

    public bool AutoStart
    {
        get => _autoStart;
        set => SetProperty(ref _autoStart, value);
    }

    public bool RefreshAfterDownload
    {
        get => _refreshAfterDownload;
        set => SetProperty(ref _refreshAfterDownload, value);
    }

    public string DefaultDownloadLabel
    {
        get => _defaultDownloadLabel;
        set => SetProperty(ref _defaultDownloadLabel, value);
    }

    public string DownloadStatus
    {
        get => _downloadStatus;
        private set => SetProperty(ref _downloadStatus, value);
    }

    private void LoadDownloadFields()
    {
        DownloadSettings download = _services.Settings.Download;
        _downloadRoot = _services.Downloads.RootPath;
        _downloadAddAsSource = download.AddAsSource;
        _maxTasks = download.MaxConcurrentTasks;
        _maxPages = download.MaxConcurrentPages;
        _writeThumb = download.WriteThumb;
        _writeHeader = download.WriteEhviewerHeader;
        _autoStart = download.AutoStart;
        _refreshAfterDownload = download.RefreshLibraryOnComplete;
        _defaultDownloadLabel = download.DefaultLabel;
    }

    public void SetDownloadRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        DownloadRoot = Path.GetFullPath(path);
        DownloadStatus = "目录已选择；按所选的变更方式点击上方保存后生效";
    }

    [RelayCommand]
    private Task ApplyDownload() => ApplyAsync();

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        try
        {
            string root = DownloadRoot.Length > 0 ? DownloadRoot : _services.Downloads.RootPath;
            Directory.CreateDirectory(root);
            EhViewerForPc.Services.PlatformLauncher.Open(root);
        }
        catch (Exception ex)
        {
            DownloadStatus = "无法打开目录：" + ex.Message;
        }
    }

    private void ApplyDownloadSettings(bool save)
    {
        DownloadSettings download = _services.Settings.Download;
        if (IsDownloadRootChanged) throw new InvalidOperationException("更改下载目录请使用保存并搬迁下载操作");
        _editBaseline["Download.RootPath"] = DownloadRoot;
        ApplyEdited("Download.AddAsSource", DownloadAddAsSource, value => download.AddAsSource = value);
        ApplyEdited("Download.MaxConcurrentTasks", (int)MaxConcurrentTasks, value => download.MaxConcurrentTasks = value);
        ApplyEdited("Download.MaxConcurrentPages", (int)MaxConcurrentPages, value => download.MaxConcurrentPages = value);
        ApplyEdited("Download.WriteThumb", WriteThumb, value => download.WriteThumb = value);
        ApplyEdited("Download.WriteEhviewerHeader", WriteHeader, value => download.WriteEhviewerHeader = value);
        ApplyEdited("Download.AutoStart", AutoStart, value => download.AutoStart = value);
        ApplyEdited("Download.RefreshLibraryOnComplete", RefreshAfterDownload, value => download.RefreshLibraryOnComplete = value);
        ApplyEdited("Download.DefaultLabel", DefaultDownloadLabel.Trim(), value => download.DefaultLabel = value);
        download.Normalize();

        if (save) _services.TrySaveSettings(out _);
        if (download.AddAsSource) _services.EnsureDownloadSource();
    }

    // ------------------------------------------------------------ sources

    public void AddSource(string path, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string normalized = AppSettings.NormalizeRoot(path);
        LibrarySource source = _services.Settings.Sources.FirstOrDefault(s => EhViewerForPc.Core.Storage.PathIdentity.Comparer.Equals(s.RootPath, normalized))
            ?? new LibrarySource { Id = "src-" + EhViewerForPc.Core.Format.StableIdFactory.PathHash(normalized), RootPath = normalized, Name = name };
        _removedSources.Remove(source.Id);
        SourcesChanged = true;
        OnPropertyChanged(nameof(HasSourceChanges));
        SourceRowViewModel? row = Sources.FirstOrDefault(r =>
            string.Equals(r.Source.Id, source.Id, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            Sources.Add(new SourceRowViewModel(source) { Enabled = source.Enabled, IndexImages = _services.Settings.ImageSearch.SourceIds.Count == 0 || _services.Settings.ImageSearch.SourceIds.Contains(source.Id) });
        }
        else if (source.Enabled)
        {
            row.Enabled = true;
        }

        OnPropertyChanged(nameof(HasSources));
    }

    public void RemoveSource(SourceRowViewModel row)
    {
        if (row is null) return;
        Sources.Remove(row);
        _removedSources.Add(row.Source.Id);
        SourcesChanged = true;
        OnPropertyChanged(nameof(HasSources));
    }

    public void OpenSource(SourceRowViewModel row)
    {
        if (row is null || !row.Source.Exists) return;
        try
        {
            EhViewerForPc.Services.PlatformLauncher.Open(row.Source.RootPath);
        }
        catch (Exception ex)
        {
            _services.Log.Warn("Open source folder failed", ex);
        }
    }

    /// <summary>
    /// The resampled width (780/980/1280/1600/2400) is an account preference on
    /// the site; there is no per-request parameter, so open its settings page.
    /// </summary>
    [RelayCommand]
    private void OpenSiteImageSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Core.Online.EhUrls.Base(_services.Online.Site) + "uconfig.php",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _services.Log.Warn("Open site image settings failed", ex);
        }
    }

    public void OpenDataDirectory()
    {
        try
        {
            EhViewerForPc.Services.PlatformLauncher.Open(_services.Paths.BaseDirectory);
        }
        catch (Exception ex)
        {
            _services.Log.Warn("Open data directory failed", ex);
        }
    }

    public void ClearOnlineCache()
    {
        int removed = _services.OnlineImages.ClearCache();
        DownloadStatus = $"已清除 {removed} 个在线缓存文件";
        RefreshCacheUsage();
    }

    public void ClearCaches()
    {
        foreach (LibrarySource source in _services.Settings.Sources)
        {
            _services.CacheStore.Delete(source.Id);
        }

        DownloadStatus = "扫描缓存已清除";
    }

    // -------------------------------------------------------------- apply

    public async Task<bool> ApplyAsync(Func<bool, Task>? applied = null)
    {
        if (IsSaving) return false;
        bool rootChanged = IsDownloadRootChanged;
        bool sourcesChanged = HasSourceChanges || rootChanged;
        IsSaving = true;
        string? movedStatus = null;
        LastMovedRoot = null;
        PreviousDownloadRoot = null;
        try
        {
            if (rootChanged && DownloadDirectoryMode == 1)
            {
                _moveCancellation = new CancellationTokenSource();
                OnPropertyChanged(nameof(CanCancelMove));
                string oldRoot = _services.Downloads.RootPath;
                var progress = new Progress<DownloadMoveProgress>(p =>
                {
                    if (!IsSaving || _moveCancellation is null) return;
                    DownloadStatus = p.TotalBytes > 0 ? $"{p.Phase} · {p.Percent:F0}% · {DownloadTask.FormatSize(p.Bytes)} / {DownloadTask.FormatSize(p.TotalBytes)}" : p.Phase;
                });
                bool cleaned = await _services.RelocateDownloadsAsync(DownloadRoot, progress, _moveCancellation.Token);
                _moveCancellation.Dispose(); _moveCancellation = null;
                OnPropertyChanged(nameof(CanCancelMove));
                LastMovedRoot = oldRoot;
                PreviousDownloadRoot = oldRoot;
                DownloadRoot = _services.Downloads.RootPath;
                movedStatus = cleaned ? "下载文件与本地库目录已同步迁移" : "迁移完成；旧目录中被占用或变化的文件已保留";
                foreach (var row in Sources) row.NotifyPathChanged();
                RefreshSources();
            }
            else if (rootChanged)
            {
                string previous = _services.Downloads.RootPath;
                await _services.ChangeDownloadRootWithoutMovingAsync(DownloadRoot, DownloadAddAsSource);
                PreviousDownloadRoot = previous;
                DownloadRoot = _services.Downloads.RootPath;
                movedStatus = "新下载位置已保存；旧文件、旧任务和旧数据源保留原位置";
                RefreshSources();
            }
            Apply();
            if (rootChanged) DownloadStatus = "下载位置已保存，正在更新本地库索引…";
            if (applied is not null) await applied(sourcesChanged);
            DownloadStatus = rootChanged ? movedStatus! : "设置已保存";
            return true;
        }
        catch (OperationCanceledException)
        {
            DownloadStatus = PreviousDownloadRoot is null ? "已取消迁移，原目录与下载记录保持不变；用户原有的目标文件夹已保留"
                : "目录设置已保存，本地库索引更新已取消，可稍后手动刷新";
            return PreviousDownloadRoot is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { DownloadStatus = "保存失败：" + ex.Message; return false; }
        finally { _moveCancellation?.Dispose(); _moveCancellation = null; RefreshSources(); IsSaving = false; }
    }

    public void Apply()
    {
        AppSettings settings = _services.Settings;
        string imageDirectory = string.IsNullOrWhiteSpace(ImageSearchDirectory) ? Path.Combine(_services.Paths.BaseDirectory, "image-search") : ImageSearchDirectory.Trim();
        if (ImageSearchEnabled) LocalImageSearchService.ValidateStorage(imageDirectory, Sources.Select(row => row.Source));
        if (ImageSearchEnabled && Sources.Count > 0 && !Sources.Any(row => row.IndexImages)) throw new InvalidOperationException("请至少选择一个图片检索数据源，或关闭以图搜图。");

        foreach (var row in Sources)
        {
            var source = settings.Sources.FirstOrDefault(s => s.Id == row.Source.Id) ?? settings.AddSource(row.Source.RootPath, row.Source.Name);
            if (!_enabledSnapshot.TryGetValue(row.Source.Id, out bool before) || before != row.Enabled) source.Enabled = row.Enabled;
        }
        foreach (string id in _removedSources) settings.RemoveSource(id);
        ApplyEdited("ImageSearch.Enabled", ImageSearchEnabled, value => settings.ImageSearch.Enabled = value);
        ApplyEdited("ImageSearch.StorageDirectory", ImageSearchDirectory.Trim(), value => settings.ImageSearch.StorageDirectory = value);
        ApplyEdited("ImageSearch.NProbe", new[] { 4, 16, 64 }[ImageSearchProbeIndex], value => settings.ImageSearch.NProbe = value);
        ApplyEdited("ImageSearch.AutoUpdate", ImageSearchAutoUpdate, value => settings.ImageSearch.AutoUpdateAfterDownload = value);
        ApplyEdited("ImageSearch.SourceIds", ImageSearchSourceSelection, _ => settings.ImageSearch.SourceIds = Sources.All(row => row.IndexImages) ? [] : Sources.Where(row => row.IndexImages).Select(row => row.Source.Id).ToList());
        _removedSources.Clear();
        ApplyEdited("Theme", (AppTheme)_themeIndex, value => settings.Theme = value);
        ApplyEdited("CloseToTray", CloseToTray, value => settings.CloseToTray = value);
        ApplyEdited("DefaultReadingMode", (ReadingMode)_modeIndex, value => settings.DefaultReadingMode = value);
        ApplyEdited("DefaultFitMode", (FitMode)_fitIndex, value => settings.DefaultFitMode = value);
        ApplyEdited("ReaderBackground", (ReaderBackground)_backgroundIndex, value => settings.ReaderBackground = value);
        ApplyEdited("RightToLeft", _rightToLeft, value => settings.RightToLeft = value);
        ApplyEdited("MaxDecodeMegapixels", (int)_maxMegapixels, value => settings.MaxDecodeMegapixels = value);
        ApplyEdited("PageCacheMegabytes", (int)_pageCacheMegabytes, value => settings.PageCacheMegabytes = value);
        ApplyEdited("CoverDecodeWidth", (int)_coverDecodeWidth, value => settings.CoverDecodeWidth = value);
        ApplyEdited("Account.Site", (EhSite)_siteIndex, value => settings.Account.Site = value);
        ApplyEdited("favoriteSlots", string.Join("\0", FavoriteSlots.Select(f => f.Name.Trim())),
            _ => settings.Account.FavoriteSlotNames = FavoriteSlots.Select(f => f.Name.Trim()).ToList());
        settings.Account.Normalize();

        ApplyEdited("Online.ShowTagTranslations", _showTagTranslations, value => settings.Online.ShowTagTranslations = value);
        ApplyEdited("Online.TagTranslationUrl", TagTranslationUrl.Trim(), value => settings.Online.TagTranslationUrl = value);

        int cacheMegabytes = (int)Math.Clamp(_onlineCacheMegabytes, 128, 32768);
        int cacheDays = (int)Math.Clamp(_onlineCacheDays, 1, 90);
        bool cachePolicyChanged =
            !_editBaseline.TryGetValue("Online.CacheMegabytes", out object? baseMb) || !Equals(baseMb, cacheMegabytes) ||
            !_editBaseline.TryGetValue("Online.CacheDays", out object? baseDays) || !Equals(baseDays, cacheDays);
        ApplyEdited("Online.CacheMegabytes", cacheMegabytes, value => settings.OnlineCacheMegabytes = value);
        ApplyEdited("Online.CacheDays", cacheDays, value => settings.OnlineCacheDays = value);
        if (cachePolicyChanged) _services.OnlineImages.ApplyPolicy();
        ApplyEdited("ResampledWidth", SelectedResampledWidth, value => settings.ResampledWidth = value);
        ApplyEdited("ImageQuality", ImageQualityIndex == 0 ? ImageQuality.Original : ImageQuality.Resampled, value => settings.ImageQuality = value);
        settings.Online.Filters = Filters.Select(f => f.Rule.Clone()).ToList();
        settings.Online.Normalize();

        ApplyNetworkSettings(save: false);
        ApplyDownloadSettings(save: false);
        _services.TrySaveSettings(out _);
        App.ApplyTheme(settings.Theme);

        FilterStatus = settings.Online.Filters.Count == 0
            ? "没有过滤规则"
            : $"{settings.Online.Filters.Count} 条规则已保存";
        FavoriteSlotStatus = "已保存，收藏页与详情窗口立即生效";

        // Saved state becomes the new baseline: a second "保存" without further
        // edits must not trigger another full library rescan.
        SourcesChanged = false;
        _enabledSnapshot.Clear();
        foreach (SourceRowViewModel row in Sources)
        {
            _enabledSnapshot[row.Source.Id] = row.Enabled;
        }

        OnPropertyChanged(nameof(HasSourceChanges));
    }

    private void ApplyEdited<T>(string key, T value, Action<T> apply)
    {
        if (!_editBaseline.TryGetValue(key, out object? baseline) || !Equals(baseline, value)) apply(value);
        _editBaseline[key] = value;
    }

    private void CaptureBaseline()
    {
        _editBaseline["ImageSearch.Enabled"] = ImageSearchEnabled;
        _editBaseline["ImageSearch.StorageDirectory"] = ImageSearchDirectory;
        _editBaseline["ImageSearch.NProbe"] = new[] { 4, 16, 64 }[ImageSearchProbeIndex];
        _editBaseline["ImageSearch.AutoUpdate"] = ImageSearchAutoUpdate;
        _editBaseline["ImageSearch.SourceIds"] = ImageSearchSourceSelection;
        _editBaseline["Download.RootPath"] = DownloadRoot;
        _editBaseline["Download.DefaultLabel"] = _defaultDownloadLabel.Trim();
        _editBaseline["Download.RefreshLibraryOnComplete"] = _refreshAfterDownload;
        _editBaseline["Download.AutoStart"] = _autoStart;
        _editBaseline["Download.WriteEhviewerHeader"] = _writeHeader;
        _editBaseline["Download.WriteThumb"] = _writeThumb;
        _editBaseline["Download.MaxConcurrentPages"] = (int)_maxPages;
        _editBaseline["Download.MaxConcurrentTasks"] = (int)_maxTasks;
        _editBaseline["Download.AddAsSource"] = _downloadAddAsSource;
        _editBaseline["favoriteSlots"] = string.Join("\0", FavoriteSlots.Select(f => f.Name.Trim()));
        _editBaseline["Theme"] = (AppTheme)_themeIndex;
        _editBaseline["CloseToTray"] = CloseToTray;
        _editBaseline["DefaultReadingMode"] = (ReadingMode)_modeIndex;
        _editBaseline["DefaultFitMode"] = (FitMode)_fitIndex;
        _editBaseline["ReaderBackground"] = (ReaderBackground)_backgroundIndex;
        _editBaseline["RightToLeft"] = _rightToLeft;
        _editBaseline["MaxDecodeMegapixels"] = (int)_maxMegapixels;
        _editBaseline["PageCacheMegabytes"] = (int)_pageCacheMegabytes;
        _editBaseline["CoverDecodeWidth"] = (int)_coverDecodeWidth;
        _editBaseline["Account.Site"] = (EhSite)_siteIndex;
        _editBaseline["Online.ShowTagTranslations"] = _showTagTranslations;
        _editBaseline["Online.TagTranslationUrl"] = TagTranslationUrl.Trim();
        _editBaseline["Online.CacheMegabytes"] = (int)Math.Clamp(_onlineCacheMegabytes, 128, 32768);
        _editBaseline["Online.CacheDays"] = (int)Math.Clamp(_onlineCacheDays, 1, 90);
        _editBaseline["ResampledWidth"] = SelectedResampledWidth;
        _editBaseline["ImageQuality"] = ImageQualityIndex == 0 ? ImageQuality.Original : ImageQuality.Resampled;
    }

    public void RefreshSources()
    {
        RefreshUnedited("CloseToTray", CloseToTray, _services.Settings.CloseToTray, value => CloseToTray = value);
        RefreshUnedited("ImageSearch.Enabled", ImageSearchEnabled, _services.Settings.ImageSearch.Enabled, value => ImageSearchEnabled = value);
        RefreshUnedited("ImageSearch.StorageDirectory", ImageSearchDirectory, _services.Settings.ImageSearch.StorageDirectory, value => ImageSearchDirectory = value);
        RefreshUnedited("ImageSearch.NProbe", new[] { 4, 16, 64 }[ImageSearchProbeIndex], _services.Settings.ImageSearch.NProbe, value => ImageSearchProbeIndex = Array.IndexOf(new[] { 4, 16, 64 }, value));
        RefreshUnedited("ImageSearch.AutoUpdate", ImageSearchAutoUpdate, _services.Settings.ImageSearch.AutoUpdateAfterDownload, value => ImageSearchAutoUpdate = value);
        // A cached page follows effective values until its draft is edited.
        RefreshUnedited("ImageQuality", ImageQualityIndex == 0 ? ImageQuality.Original : ImageQuality.Resampled,
            _services.Settings.ImageQuality, value => ImageQualityIndex = value == ImageQuality.Original ? 0 : 1);
        RefreshUnedited("ResampledWidth", SelectedResampledWidth, _services.Settings.ResampledWidth,
            value => ResampledWidthIndex = Array.IndexOf(new[] { 780, 980, 1280, 1600, 2400 }, value));
        RefreshUnedited("Download.RootPath", DownloadRoot, _services.Downloads.RootPath, value => DownloadRoot = value);
        RefreshUnedited("Download.AddAsSource", DownloadAddAsSource, _services.Settings.Download.AddAsSource, value => DownloadAddAsSource = value);
        RefreshUnedited("Online.CacheMegabytes", (int)_onlineCacheMegabytes, _services.Settings.OnlineCacheMegabytes,
            value => OnlineCacheMegabytes = value);
        RefreshUnedited("Online.CacheDays", (int)_onlineCacheDays, _services.Settings.OnlineCacheDays,
            value => OnlineCacheDays = value);
        foreach (var source in _services.Settings.Sources)
        {
            if (_removedSources.Contains(source.Id)) continue;
            var existing = Sources.FirstOrDefault(r => r.Source.Id == source.Id);
            if (existing is not null) existing.UpdateSource(source);
            else { Sources.Add(new SourceRowViewModel(source) { IndexImages = _services.Settings.ImageSearch.SourceIds.Count == 0 || _services.Settings.ImageSearch.SourceIds.Contains(source.Id) }); _enabledSnapshot[source.Id] = source.Enabled; }
        }
        foreach (var stale in Sources.Where(r => _enabledSnapshot.ContainsKey(r.Source.Id) && !_services.Settings.Sources.Any(s => s.Id == r.Source.Id)).ToArray()) Sources.Remove(stale);
        OnPropertyChanged(nameof(HasSources));
        RefreshCacheUsage();
    }

    private void RefreshUnedited<T>(string key, T draft, T effective, Action<T> update)
    {
        if (!_editBaseline.TryGetValue(key, out var baseline) || !Equals(baseline, draft) || Equals(draft, effective)) return;
        update(effective);
        _editBaseline[key] = effective;
    }

    public void Dispose()
    {
        // Settings live in memory; nothing to release.
    }
}

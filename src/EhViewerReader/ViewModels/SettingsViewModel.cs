using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Online;
using EhViewerReader.Services;

namespace EhViewerReader.ViewModels;

/// <summary>One editable row in the settings source list.</summary>
public sealed class SourceRowViewModel : ObservableObject
{
    private bool _enabled;

    public SourceRowViewModel(LibrarySource source)
    {
        Source = source;
        _enabled = source.Enabled;
    }

    public LibrarySource Source { get; }
    public string DisplayName => Source.DisplayName;
    public string RootPath => Source.RootPath;
    public bool IsMissing => !Source.Exists;
    public string StatusText => Source.Exists ? string.Empty : "目录不可用";

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value)) return;
            Source.Enabled = value;
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
    private int _themeIndex;
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
    private string _accountStatus = "推荐使用网页登录：站点对普通 HTTP 登录启用了 Cloudflare 校验";
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
    private bool _downloadOriginal;
    private bool _writeThumb;
    private bool _writeHeader;
    private bool _autoStart;
    private bool _refreshAfterDownload;
    private string _defaultDownloadLabel = string.Empty;
    private string _downloadStatus = string.Empty;

    private string _networkSnapshot;
    private readonly Dictionary<string, bool> _enabledSnapshot = new(StringComparer.OrdinalIgnoreCase);

    private bool _showTagTranslations;
    private string _tagTranslationUrl = string.Empty;
    private string _tagTranslationStatus = string.Empty;
    private bool _updatingTags;

    private bool _readOriginalImages;
    private string _filterStatus = string.Empty;

    private string _favoriteSlotStatus = string.Empty;
    private string _hathStatus = string.Empty;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        AppSettings settings = services.Settings;
        _themeIndex = (int)settings.Theme;
        _modeIndex = (int)settings.DefaultReadingMode;
        _fitIndex = (int)settings.DefaultFitMode;
        _backgroundIndex = (int)settings.ReaderBackground;
        _rightToLeft = settings.RightToLeft;
        _maxMegapixels = settings.MaxDecodeMegapixels;
        _pageCacheMegabytes = settings.PageCacheMegabytes;
        _coverDecodeWidth = settings.CoverDecodeWidth;

        foreach (LibrarySource source in settings.Sources)
        {
            Sources.Add(new SourceRowViewModel(source));
            _enabledSnapshot[source.Id] = source.Enabled;
        }

        LoadAccountFields();
        LoadNetworkFields();
        LoadDownloadFields();
        LoadOnlineFields();
        _networkSnapshot = NetworkSnapshot();
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

    public bool ReadOriginalImages
    {
        get => _readOriginalImages;
        set => SetProperty(ref _readOriginalImages, value);
    }

    public string FilterStatus
    {
        get => _filterStatus;
        private set => SetProperty(ref _filterStatus, value);
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
        _readOriginalImages = online.ReadOriginalImages;
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
        set => SetProperty(ref _downloadRoot, value);
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

    public bool DownloadOriginal
    {
        get => _downloadOriginal;
        set => SetProperty(ref _downloadOriginal, value);
    }

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
        _downloadOriginal = download.DownloadOriginal;
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
        DownloadStatus = "下载目录已更新，保存后生效";
    }

    [RelayCommand]
    private void ApplyDownload()
    {
        ApplyDownloadSettings(save: true);
        DownloadStatus = "下载设置已保存（进行中的任务继续使用原目录）";
    }

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        try
        {
            string root = DownloadRoot.Length > 0 ? DownloadRoot : _services.Downloads.RootPath;
            Directory.CreateDirectory(root);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + root + "\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            DownloadStatus = "无法打开目录：" + ex.Message;
        }
    }

    private void ApplyDownloadSettings(bool save)
    {
        DownloadSettings download = _services.Settings.Download;
        download.RootPath = DownloadRoot.Trim();
        download.AddAsSource = DownloadAddAsSource;
        download.MaxConcurrentTasks = (int)MaxConcurrentTasks;
        download.MaxConcurrentPages = (int)MaxConcurrentPages;
        download.DownloadOriginal = DownloadOriginal;
        download.WriteThumb = WriteThumb;
        download.WriteEhviewerHeader = WriteHeader;
        download.AutoStart = AutoStart;
        download.RefreshLibraryOnComplete = RefreshAfterDownload;
        download.DefaultLabel = DefaultDownloadLabel.Trim();
        download.Normalize();

        if (save) _services.TrySaveSettings(out _);
        if (DownloadAddAsSource) _services.EnsureDownloadSource();
    }

    // ------------------------------------------------------------ sources

    public void AddSource(string path, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        LibrarySource source = _services.Settings.AddSource(path, name);
        SourcesChanged = true;
        OnPropertyChanged(nameof(HasSourceChanges));
        SourceRowViewModel? row = Sources.FirstOrDefault(r =>
            string.Equals(r.Source.Id, source.Id, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            Sources.Add(new SourceRowViewModel(source) { Enabled = source.Enabled });
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
        SourcesChanged = true;
        OnPropertyChanged(nameof(HasSources));
    }

    public void OpenSource(SourceRowViewModel row)
    {
        if (row is null || !row.Source.Exists) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + row.Source.RootPath + "\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _services.Log.Warn("Open source folder failed", ex);
        }
    }

    public void OpenDataDirectory()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + _services.Paths.BaseDirectory + "\"",
                UseShellExecute = true,
            });
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

    public void Apply()
    {
        AppSettings settings = _services.Settings;

        // The rows hold the same LibrarySource instances, so enabling/disabling
        // is already applied. Removing a row must also drop it from settings and
        // from the per-work preferred-source map.
        var keep = new HashSet<string>(Sources.Select(s => s.Source.Id), StringComparer.OrdinalIgnoreCase);
        foreach (LibrarySource source in settings.Sources.ToArray())
        {
            if (!keep.Contains(source.Id))
            {
                settings.RemoveSource(source.Id);
            }
        }

        settings.Theme = (AppTheme)_themeIndex;
        settings.DefaultReadingMode = (ReadingMode)_modeIndex;
        settings.DefaultFitMode = (FitMode)_fitIndex;
        settings.ReaderBackground = (ReaderBackground)_backgroundIndex;
        settings.RightToLeft = _rightToLeft;
        settings.MaxDecodeMegapixels = (int)_maxMegapixels;
        settings.PageCacheMegabytes = (int)_pageCacheMegabytes;
        settings.CoverDecodeWidth = (int)_coverDecodeWidth;
        settings.Account.Site = (EhSite)_siteIndex;
        settings.Account.FavoriteSlotNames = FavoriteSlots.Select(f => f.Name.Trim()).ToList();
        settings.Account.Normalize();

        settings.Online.ShowTagTranslations = _showTagTranslations;
        settings.Online.TagTranslationUrl = TagTranslationUrl.Trim();
        settings.Online.ReadOriginalImages = _readOriginalImages;
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

    public void Dispose()
    {
        // Settings live in memory; nothing to release.
    }
}

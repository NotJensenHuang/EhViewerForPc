using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Services;

namespace EhViewerForPc.ViewModels;

/// <summary>
/// Drives the browser-based sign-in: launches Edge/Chrome on the forums login
/// page, waits for the auth cookies to appear and hands them to the client.
/// Manual cookie paste stays available as a fallback (no Chromium installed,
/// corporate policy blocking the debug port, …).
/// </summary>
public sealed partial class WebLoginViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly CancellationTokenSource _lifetime = new();

    private BrowserLoginSession? _session;
    private CancellationTokenSource? _attempt;
    private Task? _runTask;
    private string _lastTitle = string.Empty;
    private string _lastRejected = string.Empty;
    private bool _disposed;

    private string _statusText = "准备启动浏览器…";
    private string _browserText = "正在查找 Edge / Chrome…";
    private bool _isWaiting;
    private bool _showManual;
    private string _manualCookie = string.Empty;
    private string _manualStatus = string.Empty;
    private bool _isImporting;

    public WebLoginViewModel(AppServices services)
    {
        _services = services;
        LoginUrl = BrowserLoginSession.EhentaiLoginPage;
    }

    /// <summary>Login page the browser opens.</summary>
    public string LoginUrl { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string BrowserText
    {
        get => _browserText;
        private set => SetProperty(ref _browserText, value);
    }

    public bool IsWaiting
    {
        get => _isWaiting;
        private set => SetProperty(ref _isWaiting, value);
    }

    public bool ShowManual
    {
        get => _showManual;
        set => SetProperty(ref _showManual, value);
    }

    public string ManualCookie
    {
        get => _manualCookie;
        set => SetProperty(ref _manualCookie, value);
    }

    public string ManualStatus
    {
        get => _manualStatus;
        private set => SetProperty(ref _manualStatus, value);
    }

    public bool IsImporting
    {
        get => _isImporting;
        private set => SetProperty(ref _isImporting, value);
    }

    public string SiteLabel => EhUrls.SiteName(_services.Online.Site);

    /// <summary>Raised once the cookies are valid; the window closes on it.</summary>
    public event EventHandler<EhLoginResult>? Completed;

    public void SetStatus(string text) => StatusText = text;

    public void Start()
    {
        if (_runTask is not null) return;
        _runTask = RunAttemptAsync();
    }

    [RelayCommand]
    private async Task OpenBrowserAsync()
    {
        if (_session is { HasExited: false } session)
        {
            await session.BringToFrontAsync(_lifetime.Token).ConfigureAwait(true);
            return;
        }

        await RestartAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        await RestartAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ImportManualAsync()
    {
        if (IsImporting) return;
        IsImporting = true;
        ManualStatus = "正在验证…";
        try
        {
            Dictionary<string, string> cookies = EhSession.ParseCookieString(ManualCookie);
            if (cookies.Count == 0)
            {
                ManualStatus = "没有解析出 Cookie，请粘贴 ipb_member_id=…; ipb_pass_hash=…";
                return;
            }

            EhLoginResult result = await _services.Online
                .CompleteBrowserSignInAsync(cookies, _lifetime.Token)
                .ConfigureAwait(true);

            ManualStatus = result.Message;
            if (result.Success) Finish(result);
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException or OperationCanceledException)
        {
            ManualStatus = "导入失败：" + ex.Message;
        }
        finally
        {
            IsImporting = false;
        }
    }

    private async Task RestartAsync()
    {
        _attempt?.Cancel();
        if (_runTask is not null)
        {
            try
            {
                await _runTask.ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
            {
                // Replaced by the new attempt.
            }
        }

        _session?.Dispose();
        _session = null;
        _lastTitle = string.Empty;
        _lastRejected = string.Empty;
        ShowManual = false;
        _runTask = RunAttemptAsync();
    }

    private async Task RunAttemptAsync()
    {
        var attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _attempt = attempt;
        CancellationToken token = attempt.Token;

        IsWaiting = true;
        StatusText = "正在启动浏览器…";

        try
        {
            BrowserLoginSession session = await BrowserLoginSession
                .LaunchAsync(new BrowserLoginOptions { Url = LoginUrl, ProxyServer = BuildProxyServer() }, token)
                .ConfigureAwait(true);

            _session = session;
            BrowserText = session.BrowserName + " · 独立临时配置";
            StatusText = "浏览器已打开，请在窗口中完成安全验证并登录；成功后会自动导入，无需复制 Cookie。";

            while (!token.IsCancellationRequested)
            {
                if (session.HasExited)
                {
                    StatusText = "浏览器已关闭。重新打开后可继续登录，或展开下方手动粘贴 Cookie。";
                    ShowManual = true;
                    return;
                }

                string? title = await session.TryGetTitleAsync(token).ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(title) && title != _lastTitle)
                {
                    _lastTitle = title;
                    StatusText = $"当前页面：{title} · 等待登录…";
                }

                Dictionary<string, string> cookies = await session
                    .ReadAuthCookiesAsync(token)
                    .ConfigureAwait(true);

                if (cookies.ContainsKey("ipb_member_id") && cookies.ContainsKey("ipb_pass_hash"))
                {
                    string signature = string.Join(
                        ";",
                        cookies.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                            .Select(p => p.Key + "=" + p.Value));

                    // Do not re-verify the same rejected cookie set every second.
                    if (signature != _lastRejected)
                    {
                        StatusText = "已检测到登录凭据，正在向站点验证…";
                        EhLoginResult result = await _services.Online
                            .CompleteBrowserSignInAsync(cookies, token)
                            .ConfigureAwait(true);

                        if (result.Success)
                        {
                            Finish(result);
                            return;
                        }

                        _lastRejected = signature;
                        StatusText = result.Message;
                        ShowManual = true;
                    }
                }

                await Task.Delay(1200, token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Window closing or a restart request.
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            if (_session is null) BrowserText = "未找到可用的 Edge / Chrome";
            StatusText = "无法自动获取登录状态：" + ex.Message + "（可展开下方手动粘贴 Cookie）";
            ShowManual = true;
        }
        finally
        {
            if (ReferenceEquals(_attempt, attempt)) IsWaiting = false;
            attempt.Dispose();
        }
    }

    private void Finish(EhLoginResult result)
    {
        IsWaiting = false;
        StatusText = result.Message;
        Completed?.Invoke(this, result);
    }

    /// <summary>Honours the app's explicit proxy so the browser matches the client.</summary>
    private string? BuildProxyServer()
    {
        NetworkSettings network = _services.Settings.Network;
        if (network.ProxyMode == ProxyMode.System) return null;
        if (network.ProxyMode is not (ProxyMode.Http or ProxyMode.Socks5)) return null;
        if (network.ProxyHost.Length == 0 || network.ProxyPort <= 0) return null;

        string scheme = network.ProxyMode == ProxyMode.Socks5 ? "socks5" : "http";
        return $"{scheme}://{network.ProxyHost}:{network.ProxyPort}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _attempt?.Cancel();
        _lifetime.Cancel();
        _session?.Dispose();
        _session = null;
        _lifetime.Dispose();
    }
}

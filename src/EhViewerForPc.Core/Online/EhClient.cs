using System.Net;
using System.Buffers;
using EhViewerForPc.Core.Format;
using System.Text;
using EhViewerForPc.Core.Models;

namespace EhViewerForPc.Core.Online;

public sealed record EhResolvedImage(string Url, bool IsOriginal);

public sealed class EhException : Exception
{
    public EhException(string message, bool requiresSignIn = false, Exception? inner = null, bool quotaExceeded = false)
        : base(message, inner)
    {
        RequiresSignIn = requiresSignIn;
        QuotaExceeded = quotaExceeded;
    }

    public bool RequiresSignIn { get; }

    /// <summary>站点图片配额已用尽或被节点限流（两者都用 509 表达）。
    /// 这类失败重试只会继续消耗配额，调用方应立即停下而不是逐页重试。</summary>
    public bool QuotaExceeded { get; }
}

/// <summary>
/// E-Hentai / ExHentai client: authentication, gallery listings, search,
/// favourites, page lists and image resolution.
///
/// All requests share one cookie jar. Page-viewer requests are serialised with
/// a small delay because the site throttles them per account/IP and answers
/// with a quota error when hit in parallel.
/// </summary>
public sealed class EhClient : IDisposable
{
    private const int ViewerThrottleMilliseconds = 260;

    private readonly AppSettings _settings;
    private readonly Action<string>? _log;
    private readonly EhSession _session = new();
    private readonly SemaphoreSlim _viewerResolutionGate = new(1, 1);
    private readonly SemaphoreSlim _viewerGate = new(1, 1);
    private readonly Dictionary<string, EhResolvedImage> _imageUrlCache = new(StringComparer.Ordinal);
    private readonly object _cacheGate = new();

    private HttpClient _web;
    private HttpClient _images;
    private DateTime _lastViewerRequestUtc = DateTime.MinValue;
    private bool _disposed;

    public EhClient(AppSettings settings, Action<string>? log = null)
    {
        _settings = settings;
        _log = log;
        _session.Import(settings.Account.Cookies);
        _web = EhHttp.Create(settings.Network, _session.Cookies, forImages: false, log);
        _images = EhHttp.Create(settings.Network, _session.Cookies, forImages: true, log);
    }

    internal EhClient(AppSettings settings, HttpClient webClient) : this(settings)
    { _web.Dispose(); _web = webClient; }

    internal EhClient(AppSettings settings, HttpClient webClient, HttpClient imageClient) : this(settings, webClient)
    { _images.Dispose(); _images = imageClient; }

    public EhSession Session => _session;
    public int ResampledWidth => _settings.ResampledWidth;

    public EhSite Site => _settings.Account.Site;

    public bool IsSignedIn => _session.HasCredentials;

    /// <summary>
    /// Identity of the browsing context (site + account) used to scope on-disk
    /// image caches. Reading the same gallery while signed in as another account
    /// must not reuse the previous account's resampled/original bytes.
    /// </summary>
    public string CacheScope => (Site == EhSite.ExHentai ? "ex" : "eh") + "|" +
                                (IsSignedIn ? "u:" + _settings.Account.LastUserName : "anon");

    public bool ExAccess => _settings.Account.ExAccess;

    public string UserName => _settings.Account.LastUserName;

    /// <summary>
    /// Reads the account's image limits. The page only exists on the main site
    /// (exhentai.org/home.php answers 404), and the auth cookies are installed
    /// on both hosts, so ExHentai sessions read it from e-hentai.org. Returns
    /// null when signed out, blocked, or the page shape changed — the reading is
    /// diagnostic only and must never fail a caller.
    /// </summary>
    public async Task<EhImageLimits?> GetImageLimitsAsync(CancellationToken cancellationToken)
    {
        if (!IsSignedIn) return null;
        var urls = new List<string>(2) { EhUrls.HomePage(EhSite.EHentai) };
        if (Site != EhSite.EHentai) urls.Add(EhUrls.HomePage(Site));

        foreach (string url in urls)
        {
            try
            {
                string html = await GetStringAsync(url, EhUrls.Base(EhSite.EHentai) + "/", cancellationToken)
                    .ConfigureAwait(false);
                if (EhParser.TryParseImageLimits(html, out EhImageLimits limits)) return limits;
                _log?.Invoke("Image limits label missing on " + url);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is EhException or HttpRequestException or OperationCanceledException)
            {
                _log?.Invoke($"Image limits unavailable ({url}): {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>Rebuilds both handlers after proxy/DNS settings changed.</summary>
    public void ReloadNetwork()
    {
        if (_disposed) return;
        HttpClient oldWeb = _web;
        HttpClient oldImages = _images;
        _web = EhHttp.Create(_settings.Network, _session.Cookies, forImages: false, _log);
        _images = EhHttp.Create(_settings.Network, _session.Cookies, forImages: true, _log);
        oldWeb.Dispose();
        oldImages.Dispose();
        lock (_cacheGate) _imageUrlCache.Clear();
    }

    /// <summary>Applies settings.Account.Site, rebuilding neither handler.</summary>
    public void SwitchSite(EhSite site)
    {
        _settings.Account.Site = site;
    }

    // ---------------------------------------------------------------- account

    public async Task<EhLoginResult> SignInAsync(string userName, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
        {
            return new EhLoginResult { Success = false, Message = "请输入用户名和密码" };
        }

        try
        {
            var form = new Dictionary<string, string>
            {
                ["UserName"] = userName.Trim(),
                ["PassWord"] = password,
                ["CookieDate"] = "1",
                ["submit"] = "Log me in",
            };

            string body = await PostFormAsync(
                    EhUrls.Login,
                    form,
                    "https://forums.e-hentai.org/index.php?act=Login&CODE=00",
                    cancellationToken)
                .ConfigureAwait(false);
            if (body.Contains("Username or password incorrect", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("You failed to log in", StringComparison.OrdinalIgnoreCase))
            {
                return new EhLoginResult { Success = false, Message = "用户名或密码错误" };
            }

            _session.MirrorAuthCookies();

            if (!_session.HasCredentials)
            {
                return new EhLoginResult { Success = false, Message = "登录未返回凭据（站点可能要求验证码或两步验证）" };
            }

            // Loading the gallery index hands out the igneous cookie that
            // exhentai.org requires.
            try
            {
                await GetStringAsync(EhUrls.Home(EhSite.EHentai), null, cancellationToken).ConfigureAwait(false);
            }
            catch (EhException ex)
            {
                _log?.Invoke("Post-login cookie warm-up failed: " + ex.Message);
            }

            _session.MirrorAuthCookies();
            SaveSessionToSettings();
            _settings.Account.LastUserName = userName.Trim();
            _settings.Account.LastSignInUtc = DateTime.UtcNow.ToString("O");

            (bool exAccess, bool known) = await ProbeExAccessAsync(cancellationToken).ConfigureAwait(false);
            _settings.Account.ExAccess = exAccess;
            SaveSessionToSettings();

            return new EhLoginResult
            {
                Success = true,
                Message = exAccess || !known ? "登录成功" : "登录成功，但当前账号无法访问 ExHentai",
                ExAccess = exAccess,
                ExAccessKnown = known,
            };
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException or TaskCanceledException)
        {
            _log?.Invoke("Sign-in failed: " + ex.Message);
            bool challenge = ex.Message.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase) ||
                             ex.Message.Contains("人机验证", StringComparison.OrdinalIgnoreCase);
            return new EhLoginResult
            {
                Success = false,
                Message = challenge ? ex.Message : "登录失败：" + ex.Message,
                RequiresBrowserLogin = challenge,
            };
        }
    }

    /// <summary>
    /// Finishes a browser (web-view style) sign-in: installs the cookies the
    /// real browser received, proves they work by loading home.php, then probes
    /// ExHentai. forums.e-hentai.org is behind a Cloudflare challenge that a
    /// plain HTTP client cannot pass, so this is the only reliable way in.
    /// </summary>
    public async Task<EhLoginResult> CompleteBrowserSignInAsync(
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken cancellationToken)
    {
        var auth = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in cookies)
        {
            if (pair.Value.Length == 0) continue;
            if (!EhSession.AuthCookieNames.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)) continue;
            auth[pair.Key] = pair.Value;
        }

        if (!auth.ContainsKey("ipb_member_id") || !auth.ContainsKey("ipb_pass_hash"))
        {
            return new EhLoginResult
            {
                Success = false,
                Message = "浏览器的登录 Cookie 不完整（缺少 ipb_member_id / ipb_pass_hash），请确认已在浏览器中登录成功",
                RequiresBrowserLogin = true,
            };
        }

        // Replace, do not merge: mixing two accounts produces impossible states
        // (member id from one, pass hash from another).
        _session.Clear();
        _session.Import(auth);
        _session.MirrorAuthCookies();

        bool valid;
        try
        {
            valid = await VerifySignedInAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException or TaskCanceledException)
        {
            _log?.Invoke("Browser sign-in verification failed: " + ex.Message);
            return new EhLoginResult
            {
                Success = false,
                Message = "无法验证登录状态：" + ex.Message,
                RequiresBrowserLogin = true,
            };
        }

        if (!valid)
        {
            _session.Clear();
            return new EhLoginResult
            {
                Success = false,
                Message = "浏览器中的登录状态无效或已过期，请在浏览器里重新登录",
                RequiresBrowserLogin = true,
            };
        }

        // home.php also hands out igneous on the way through bounce_login.
        _session.MirrorAuthCookies();
        SaveSessionToSettings();
        _settings.Account.LastUserName = auth.GetValueOrDefault("ipb_member_id", string.Empty);
        _settings.Account.LastSignInUtc = DateTime.UtcNow.ToString("O");

        (bool exAccess, bool known) = await ProbeExAccessAsync(cancellationToken).ConfigureAwait(false);
        _settings.Account.ExAccess = exAccess;
        SaveSessionToSettings();

        return new EhLoginResult
        {
            Success = true,
            Message = known && !exAccess ? "登录成功，但当前账号无法访问 ExHentai" : "登录成功",
            ExAccess = exAccess,
            ExAccessKnown = known,
        };
    }

    /// <summary>
    /// True when the cookie jar carries a working session. An anonymous request
    /// for home.php is bounced to <c>bounce_login.php</c>; a signed-in one is
    /// served directly, so the redirect target is the proof.
    /// </summary>
    public async Task<bool> VerifySignedInAsync(CancellationToken cancellationToken)
    {
        // Two attempts on purpose: the first request to home.php is bounced to
        // bounce_login.php, which hands out the igneous cookie. If that hop ends
        // in a meta refresh instead of a 302 the first pass stops there, so the
        // second pass (now carrying igneous) is the authoritative answer.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, EhUrls.Home(EhSite.EHentai) + "home.php");
            using HttpResponseMessage response = await _web
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            Uri? final = response.RequestMessage?.RequestUri;
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new EhException("站点要求 Cloudflare 人机验证（HTTP 403），请稍后重试，或先在浏览器中打开一次 e-hentai.org");
            }

            if (!response.IsSuccessStatusCode || final is null) return false;

            // Signed in: served at /home.php. Anonymous: /bounce_login.php?b=…
            // (a 200 page that says "This page requires you to log on.").
            if (final.AbsolutePath.EndsWith("/home.php", StringComparison.OrdinalIgnoreCase)) return true;

            _session.MirrorAuthCookies();
        }

        return false;
    }

    public void SignOut()
    {
        _session.Clear();
        _settings.Account.Cookies.Clear();
        _settings.Account.ExAccess = false;
        _settings.Account.LastUserName = string.Empty;
        lock (_cacheGate) _imageUrlCache.Clear();
    }

    private void SaveSessionToSettings()
    {
        Dictionary<string, string> snapshot = _session.Snapshot();
        _settings.Account.Cookies = new Dictionary<string, string>(snapshot, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<(bool Access, bool Known)> ProbeExAccessAsync(CancellationToken cancellationToken)
    {
        try
        {
            string html = await GetStringAsync(EhUrls.Home(EhSite.ExHentai), null, cancellationToken).ConfigureAwait(false);
            if (html.Contains("Sad Panda", StringComparison.OrdinalIgnoreCase)) return (false, true);
            HtmlLite.Parse(html);
            return (true, true);
        }
        catch (EhException ex) when (ex.Message.Contains("403", StringComparison.Ordinal))
        {
            return (false, true);
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException or TaskCanceledException)
        {
            _log?.Invoke("ExHentai probe failed: " + ex.Message);
            return (false, false);
        }
    }

    // ------------------------------------------------------------- listings

    public Task<EhGalleryListPage> GetFrontPageAsync(int page, CancellationToken cancellationToken) =>
        GetListAsync(page <= 0 ? EhUrls.Home(Site) : $"{EhUrls.Home(Site)}?page={page}", page, cancellationToken);

    public Task<EhGalleryListPage> GetPopularAsync(int page, CancellationToken cancellationToken) =>
        GetListAsync(EhUrls.Popular(Site, page), page, cancellationToken);

    public Task<EhGalleryListPage> GetWatchedAsync(int page, CancellationToken cancellationToken) =>
        GetListAsync(EhUrls.Watched(Site, page), page, cancellationToken);

    public Task<EhGalleryListPage> GetHistoryAsync(int page, CancellationToken cancellationToken) =>
        GetListAsync(EhUrls.History(Site, page), page, cancellationToken);

    public Task<EhGalleryListPage> GetToplistAsync(int toplistIndex, int page, CancellationToken cancellationToken) =>
        GetListAsync(EhUrls.Toplist(Site, toplistIndex, page), page, cancellationToken);

    public Task<EhGalleryListPage> SearchAsync(EhSearchRequest request, int page, CancellationToken cancellationToken) =>
        GetListAsync(EhUrls.Search(Site, request, page), page, cancellationToken);

    public Task<EhGalleryListPage> GetFavoritesAsync(int slot, EhSearchRequest? request, int page, CancellationToken cancellationToken) =>
        GetListAsync(EhUrls.Favorites(Site, slot, request, page), page, cancellationToken);

    public async Task<EhGalleryListPage> GetListAsync(string url, int pageIndex, CancellationToken cancellationToken)
    {
        string html = await GetStringAsync(url, null, cancellationToken).ConfigureAwait(false);
        EhGalleryListPage page = EhParser.ParseGalleryList(html, Site, pageIndex);
        if (page.Items.Count == 0 && page.StatusMessage is null)
        {
            page.StatusMessage = EhParser.DetectMessage(html) ?? "没有结果";
        }

        return page;
    }

    /// <summary>Uploads a JPEG using the upstream multipart image-search protocol and parses its results.</summary>
    public async Task<EhGalleryListPage> SearchImageAsync(byte[] jpeg, bool similar, bool coversOnly, bool expunged, CancellationToken cancellationToken)
    {
        if (jpeg.Length is < 1 or > 20 * 1024 * 1024) throw new ArgumentException("查询图片需介于 1 字节和 20 MiB 之间。", nameof(jpeg));
        using var content = new MultipartFormDataContent();
        var image = new ByteArrayContent(jpeg); image.Headers.ContentType = new("image/jpeg");
        content.Add(image, "sfile", "query.jpg");
        if (similar) content.Add(new StringContent("on"), "fs_similar");
        if (coversOnly) content.Add(new StringContent("on"), "fs_covers");
        if (expunged) content.Add(new StringContent("on"), "fs_exp");
        content.Add(new StringContent("File Search"), "f_sfile");
        using var request = new HttpRequestMessage(HttpMethod.Post, EhUrls.ImageSearch(Site)) { Content = content };
        request.Headers.Referrer = new Uri(EhUrls.Home(Site));
        request.Headers.TryAddWithoutValidation("Origin", EhUrls.Base(Site));
        using var response = await _web.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.Headers.Contains("CF-Mitigated") || body.Contains("challenges.cloudflare.com", StringComparison.OrdinalIgnoreCase))
            throw new EhException("站点要求人机验证，请先使用网页登录完成验证。", requiresSignIn: true);
        if (!response.IsSuccessStatusCode) throw new EhException(EhParser.DetectMessage(body) ?? $"图片搜索失败：HTTP {(int)response.StatusCode}");
        var page = EhParser.ParseGalleryList(body, Site, 0);
        page.SourceUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? EhUrls.Home(Site);
        if (page.Items.Count == 0) page.StatusMessage = EhParser.DetectMessage(body) ?? "没有匹配图片";
        return page;
    }

    // -------------------------------------------------------------- details

    public async Task<EhGalleryDetails> GetDetailsAsync(long gid, string token, CancellationToken cancellationToken)
    {
        string url = EhUrls.Detail(Site, gid, token);
        string html = await GetStringAsync(url, null, cancellationToken).ConfigureAwait(false);
        EhGalleryDetails details = EhParser.ParseGalleryDetail(html, Site, gid, token);
        if (details.Title.Length == 0 && details.JapaneseTitle.Length == 0)
        {
            throw new EhException(EhParser.DetectMessage(html) ?? "无法解析画廊详情", requiresSignIn: !IsSignedIn);
        }

        if (details.Pages <= 0)
        {
            details.Pages = await TryReadPageCountFromApiAsync(gid, token, cancellationToken).ConfigureAwait(false);
        }

        return details;
    }

    private async Task<int> TryReadPageCountFromApiAsync(long gid, string token, CancellationToken cancellationToken)
    {
        try
        {
            string json = $"{{\"method\":\"gdata\",\"gidlist\":[[{gid},\"{token}\"]],\"namespace\":1}}";
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _web.PostAsync(EhUrls.Api(Site), content, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return 0;

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = System.Text.Json.JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("gmetadata", out var metadata) ||
                metadata.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return 0;
            }

            foreach (var item in metadata.EnumerateArray())
            {
                if (item.TryGetProperty("filecount", out var count) && count.TryGetInt32(out int pages)) return pages;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _log?.Invoke("gdata page count failed: " + ex.Message);
        }

        return 0;
    }

    /// <summary>
    /// Reads the addfav popup: the selected category and the stored note. The
    /// popup's default selection alone does not prove the gallery is favourited;
    /// callers must combine it with <see cref="EhGalleryDetails.IsFavorited"/>.
    /// </summary>
    public async Task<EhFavoritePopup?> GetFavoritePopupAsync(long gid, string token, CancellationToken cancellationToken)
    {
        try
        {
            string url = EhUrls.GalleryPopup(Site, gid, token, "addfav");
            string html = await GetStringAsync(url, EhUrls.Detail(Site, gid, token), cancellationToken).ConfigureAwait(false);
            return new EhFavoritePopup(EhParser.ParseFavoriteSlot(html), EhParser.ParseFavoriteNote(html));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException or TaskCanceledException)
        {
            _log?.Invoke("Favorite popup failed: " + ex.Message);
            return null;
        }
    }

    public async Task<int> GetFavoriteSlotAsync(long gid, string token, CancellationToken cancellationToken) =>
        (await GetFavoritePopupAsync(gid, token, cancellationToken).ConfigureAwait(false))?.Slot ?? -1;

    /// <summary>
    /// Adds, moves or removes a favourite and then reads the gallery state back.
    /// The POST answer alone is not trusted: the site can apply the change and
    /// still return the popup form (the historic "reported failure but the
    /// gallery was favourited" case), so a single read-only verification decides
    /// between success, rejection and "unknown" (no retry, no false failure).
    /// </summary>
    public async Task<FavoriteUpdateResult> SetFavoriteAsync(
        long gid,
        string token,
        int slot,
        bool add,
        string note,
        CancellationToken cancellationToken)
    {
        // The site always posts the addfav form; removal is favcat=favdel
        // (there is no separate deletefav endpoint, despite the popup name).
        string url = EhUrls.GalleryPopup(Site, gid, token, "addfav");
        var form = new Dictionary<string, string>
        {
            ["favcat"] = add ? slot.ToString(System.Globalization.CultureInfo.InvariantCulture) : "favdel",
            ["favnote"] = note ?? string.Empty,
            ["submit"] = "Apply Changes",
            ["update"] = "1",
        };

        bool postApplied;
        try
        {
            string body = await PostFormAsync(url, form, EhUrls.Detail(Site, gid, token), cancellationToken)
                .ConfigureAwait(false);

            if (body.Contains("requires you to log on", StringComparison.OrdinalIgnoreCase))
            {
                _log?.Invoke("Favorite update rejected: session expired");
                return new FavoriteUpdateResult(FavoriteUpdateOutcome.Rejected, "登录已失效，请重新登录", false, -1, false);
            }

            // The popup answers with a tiny confirmation page; a page that still
            // shows the form is inconclusive (the change may already be applied).
            postApplied = !body.Contains("<form", StringComparison.OrdinalIgnoreCase) ||
                          body.Contains("favorites.php", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException)
        {
            _log?.Invoke("Favorite update interrupted: " + ex.Message);
            return FavoriteUpdateResult.Unknown("提交中断，结果未知；可重新检查收藏状态");
        }
        catch (EhException ex)
        {
            _log?.Invoke("Favorite update failed: " + ex.Message);
            return FavoriteUpdateResult.Unknown("提交结果未知：" + ex.Message);
        }

        (bool Favorited, int Slot)? state = await TryReadFavoriteStateAsync(gid, token, cancellationToken)
            .ConfigureAwait(false);
        if (state is { } verified)
        {
            if (!add)
            {
                return verified.Favorited
                    ? new FavoriteUpdateResult(FavoriteUpdateOutcome.Rejected, "服务器仍显示已收藏", true,
                        verified.Slot, true)
                    : new FavoriteUpdateResult(FavoriteUpdateOutcome.Succeeded, "已取消收藏", false, -1, true);
            }

            if (!verified.Favorited)
            {
                return new FavoriteUpdateResult(FavoriteUpdateOutcome.Rejected, "服务器未应用收藏", false, -1, true);
            }

            // Applied. The site may have stored a different slot than requested
            // (for example when the requested slot no longer exists).
            string message = verified.Slot >= 0 && verified.Slot != slot
                ? $"已收藏到分类 {verified.Slot}"
                : "已收藏";
            return new FavoriteUpdateResult(FavoriteUpdateOutcome.Succeeded, message, true, verified.Slot, true);
        }

        return postApplied
            ? new FavoriteUpdateResult(FavoriteUpdateOutcome.Succeeded, "已提交（未能核验分类）", add, add ? slot : -1, false)
            : FavoriteUpdateResult.Unknown("提交结果未知；可重新检查收藏状态");
    }

    /// <summary>
    /// Read-only favourite state: <c>#favoritelink</c> proves whether the gallery
    /// is in a slot, the popup names the slot. Returns null when the page could
    /// not be read (network error, login expired), never a guessed state.
    /// </summary>
    public async Task<(bool Favorited, int Slot)?> TryReadFavoriteStateAsync(
        long gid,
        string token,
        CancellationToken cancellationToken)
    {
        try
        {
            string html = await GetStringAsync(EhUrls.Detail(Site, gid, token), null, cancellationToken)
                .ConfigureAwait(false);
            EhGalleryDetails details = EhParser.ParseGalleryDetail(html, Site, gid, token);
            if (details.Title.Length == 0 && details.JapaneseTitle.Length == 0) return null;

            int slot = details.FavoriteSlot;
            if (details.IsFavorited && slot < 0)
            {
                slot = await GetFavoriteSlotAsync(gid, token, cancellationToken).ConfigureAwait(false);
            }

            return (details.IsFavorited, slot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException or TaskCanceledException)
        {
            _log?.Invoke("Favorite state check failed: " + ex.Message);
            return null;
        }
    }

    // -------------------------------------------------- comments & rating

    /// <summary>Loads every comment (the site only renders a few by default, ?hc=1 shows all).</summary>
    public async Task<EhCommentList> GetCommentsAsync(long gid, string token, CancellationToken cancellationToken)
    {
        string url = EhUrls.Detail(Site, gid, token) + "?hc=1";
        string html = await GetStringAsync(url, null, cancellationToken).ConfigureAwait(false);

        var details = new EhGalleryDetails { Gid = gid, Token = token };
        EhParser.ParseComments(HtmlLite.Parse(html), details);

        var list = new EhCommentList
        {
            HasMore = details.HasMoreComments,
            CanPost = details.CanPostComment,
        };
        list.Comments.AddRange(details.Comments);
        return list;
    }

    /// <summary>Posts a new comment, or edits one when <paramref name="editId"/> is set.</summary>
    public async Task<EhCommentActionResult> PostCommentAsync(
        long gid,
        string token,
        string text,
        long? editId,
        CancellationToken cancellationToken)
    {
        if (!IsSignedIn) return new EhCommentActionResult { Message = "需要登录后才能评论" };
        if (string.IsNullOrWhiteSpace(text)) return new EhCommentActionResult { Message = "评论内容不能为空" };

        string detailUrl = EhUrls.Detail(Site, gid, token);
        var form = new Dictionary<string, string>();
        if (editId is null)
        {
            form["commenttext_new"] = text;
        }
        else
        {
            form["commenttext_edit"] = text;
            form["edit_comment"] = editId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        string body = await PostFormAsync(detailUrl, form, detailUrl, cancellationToken).ConfigureAwait(false);
        if (body.Contains("requires you to log on", StringComparison.OrdinalIgnoreCase))
        {
            return new EhCommentActionResult { Message = "登录已失效，请重新登录" };
        }

        HtmlNode document = HtmlLite.Parse(body);
        string? error = ExtractCommentError(document);
        if (error is not null) return new EhCommentActionResult { Message = error };

        return new EhCommentActionResult { Success = true, Message = editId is null ? "评论已发布" : "评论已更新" };
    }

    /// <summary>Reads the site's inline error (the paragraph right after #chd).</summary>
    private static string? ExtractCommentError(HtmlNode document)
    {
        HtmlNode? header = document.FirstDescendantById("chd");
        if (header?.Parent is not { } parent) return null;

        int index = parent.Children.IndexOf(header);
        if (index < 0) return null;

        for (int i = index + 1; i < parent.Children.Count; i++)
        {
            HtmlNode sibling = parent.Children[i];
            if (sibling.IsText) continue;
            if (!string.Equals(sibling.Name, "p", StringComparison.OrdinalIgnoreCase)) continue;
            string text = sibling.InnerText.Trim();
            return text.Length > 0 ? text : null;
        }

        return null;
    }

    /// <summary>Votes a comment (+1 / -1 / 0 clears) through the site's JSON API.</summary>
    public async Task<EhCommentActionResult> VoteCommentAsync(
        EhGalleryDetails details,
        long commentId,
        int vote,
        CancellationToken cancellationToken)
    {
        if (!details.CanUseApi) return new EhCommentActionResult { Message = "需要登录后才能投票" };

        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["method"] = "votecomment",
            ["apiuid"] = details.ApiUid,
            ["apikey"] = details.ApiKey,
            ["gid"] = details.Gid,
            ["token"] = details.Token,
            ["comment_id"] = commentId,
            ["comment_vote"] = vote,
        };

        string body = await PostJsonAsync(
                EhUrls.Api(Site), json.ToJsonString(), EhUrls.Detail(Site, details.Gid, details.Token), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            System.Text.Json.JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out System.Text.Json.JsonElement error))
            {
                return new EhCommentActionResult { Message = error.ToString() };
            }

            int score = root.TryGetProperty("comment_score", out System.Text.Json.JsonElement s) ? s.GetInt32() : 0;
            return new EhCommentActionResult { Success = true, Score = score, Message = "投票已记录" };
        }
        catch (System.Text.Json.JsonException)
        {
            return new EhCommentActionResult { Message = "投票失败：站点返回了无法识别的内容" };
        }
    }

    /// <summary>Rates the gallery 1–5 stars (the API takes 2–10).</summary>
    public async Task<(double Average, int Count, string? Error)> RateGalleryAsync(
        EhGalleryDetails details,
        int stars,
        CancellationToken cancellationToken)
    {
        if (!details.CanUseApi) return (0, 0, "需要登录后才能评分");

        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["method"] = "rategallery",
            ["apiuid"] = details.ApiUid,
            ["apikey"] = details.ApiKey,
            ["gid"] = details.Gid,
            ["token"] = details.Token,
            ["rating"] = Math.Clamp(stars, 1, 5) * 2,
        };

        string body = await PostJsonAsync(
                EhUrls.Api(Site), json.ToJsonString(), EhUrls.Detail(Site, details.Gid, details.Token), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            System.Text.Json.JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out System.Text.Json.JsonElement error))
            {
                return (0, 0, error.ToString());
            }

            double average = root.TryGetProperty("rating_avg", out System.Text.Json.JsonElement avg) ? avg.GetDouble() : 0;
            int count = root.TryGetProperty("rating_cnt", out System.Text.Json.JsonElement cnt) ? cnt.GetInt32() : 0;
            return (average, count, null);
        }
        catch (System.Text.Json.JsonException)
        {
            return (0, 0, "评分失败：站点返回了无法识别的内容");
        }
    }

    // ------------------------------------------------- torrents & archive

    public async Task<EhTorrentList> GetTorrentsAsync(EhGalleryDetails details, CancellationToken cancellationToken)
    {
        string url = details.TorrentUrl.Length > 0
            ? details.TorrentUrl
            : EhUrls.Torrents(Site, details.Gid, details.Token);
        string html = await GetStringAsync(url, EhUrls.Detail(Site, details.Gid, details.Token), cancellationToken)
            .ConfigureAwait(false);
        return EhParser.ParseTorrents(html, Site);
    }

    /// <summary>Fetches the .torrent file itself (ehtracker.org answers with the raw bencoded file).</summary>
    public async Task<byte[]> DownloadTorrentAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri(EhUrls.Base(Site) + "/");

        using HttpResponseMessage response = await _web.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new EhException($"种子下载失败：HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<EhArchivePage> GetArchivePageAsync(EhGalleryDetails details, CancellationToken cancellationToken)
    {
        string url = details.ArchiveUrl.Length > 0
            ? details.ArchiveUrl
            : EhUrls.Archiver(Site, details.Gid, details.Token);
        string html = await GetStringAsync(url, EhUrls.Detail(Site, details.Gid, details.Token), cancellationToken)
            .ConfigureAwait(false);
        return EhParser.ParseArchivePage(html, Site);
    }

    /// <summary>Asks the site to build an archive; <paramref name="resource"/> is an option key.</summary>
    public async Task<EhArchiveResult> RequestArchiveAsync(
        EhGalleryDetails details,
        string or,
        string resource,
        CancellationToken cancellationToken)
    {
        if (!IsSignedIn) return new EhArchiveResult { Message = "归档下载需要登录 E-Hentai 账号" };

        string url = EhUrls.ArchiveRequest(Site, details.Gid, details.Token, or);
        var form = new Dictionary<string, string> { ["hathdl_xres"] = resource };
        string body = await PostFormAsync(url, form, EhUrls.Detail(Site, details.Gid, details.Token), cancellationToken)
            .ConfigureAwait(false);
        return EhParser.ParseArchiveResult(body, Site);
    }

    public Task DownloadArchiveToFileAsync(
        string url,
        string targetPath,
        IProgress<long>? progress,
        CancellationToken cancellationToken) =>
        DownloadToFileCoreAsync(_web, url, EhUrls.Base(Site) + "/", targetPath,
            progress is null ? null : (received, _) => progress.Report(received), cancellationToken);

    // ------------------------------------------------------------ page list

    public async Task<EhPageList> GetPageListAsync(long gid, string token, int pageIndex, CancellationToken cancellationToken)
    {
        string url = EhUrls.PageList(Site, gid, token, pageIndex);
        string html = await GetStringAsync(url, null, cancellationToken).ConfigureAwait(false);
        if (!EhParser.TryParsePageList(html, Site, pageIndex, out EhPageList list, out string? error))
        {
            throw new EhException(error ?? "无法解析页面列表", requiresSignIn: !IsSignedIn);
        }

        return list;
    }

    /// <summary>
    /// Walks the preview pagination until <paramref name="expectedCount"/> page
    /// references (or the end of the gallery) were collected.
    /// </summary>
    public async Task<IReadOnlyList<EhPageRef>> GetAllPagesAsync(
        long gid,
        string token,
        int expectedCount,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var pages = new List<EhPageRef>(Math.Max(expectedCount, 20));
        var seen = new HashSet<int>();
        for (int index = 0; index < 500; index++)
        {
            EhPageList list = await GetPageListAsync(gid, token, index, cancellationToken).ConfigureAwait(false);
            foreach (EhPageRef page in list.Pages)
            {
                if (seen.Add(page.Page)) pages.Add(page);
            }

            progress?.Report(pages.Count);
            if (!list.HasMore || list.Pages.Count == 0) break;
            if (expectedCount > 0 && pages.Count >= expectedCount) break;
        }

        pages.Sort((a, b) => a.Page.CompareTo(b.Page));
        return pages;
    }

    // --------------------------------------------------------------- images

    /// <summary>
    /// Resolves the direct image URL of one viewer page. <paramref name="original"/>
    /// follows the site's fullimg.php link to the full-size file; otherwise the
    /// resampled image is returned.
    /// </summary>
    public async Task<string> GetImageUrlAsync(EhPageRef page, bool original, CancellationToken cancellationToken) =>
        (await ResolveImageAsync(page, original, cancellationToken).ConfigureAwait(false)).Url;

    public async Task<EhResolvedImage> ResolveImageAsync(EhPageRef page, bool original, CancellationToken cancellationToken, int? resampledWidth = null, bool refresh = false)
    {
        string pageUrl = page.PageUrl.Length > 0 ? page.PageUrl : EhUrls.ImagePage(Site, page);
        int width = resampledWidth ?? ResampledWidth;
        string key = pageUrl + (original ? "|orig" : "|res") + "|" + width;
        lock (_cacheGate) { if (!refresh && _imageUrlCache.TryGetValue(key, out var cached)) return cached; }

        string html = await FetchViewerHtmlAsync(pageUrl, width, cancellationToken).ConfigureAwait(false);
        bool parsed = EhParser.TryParseImagePage(html, out string imageUrl, out string? nlKey, out string? error);

        // 站点为图片加载失败提供了换源键（页面里的 nl(...)）。解析不到图片或拿到
        // 509 占位图时带它重请求一次，让站点把这一页重新分配到可用的图床节点；
        // 只重试一次，避免在真正的账号配额耗尽时放大请求。
        if ((!parsed || IsQuotaPlaceholder(imageUrl)) && nlKey is { Length: > 0 })
        {
            string retryHtml = await FetchViewerHtmlAsync(AppendRetryKey(pageUrl, nlKey), width, cancellationToken).ConfigureAwait(false);
            if (EhParser.TryParseImagePage(retryHtml, out string retryImage, out _, out string? retryError))
            {
                html = retryHtml;
                imageUrl = retryImage;
                parsed = true;
            }
            else
            {
                error = retryError ?? error;
            }
        }

        if (!parsed) throw new EhException(error ?? "无法解析图片页");
        if (IsQuotaPlaceholder(imageUrl))
            throw new EhException("站点返回 509（图片配额已用尽或节点限流）", quotaExceeded: true);

        bool actualOriginal = false;
        if (original)
        {
            HtmlNode document = HtmlLite.Parse(html);
            string? link = document.Descendants("a").Select(n => n.GetAttribute("href"))
                .FirstOrDefault(h => h.Contains("fullimg.php", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(link) && Uri.TryCreate(new Uri(pageUrl), link, out var fullUri))
            {
                string? full = await ResolveFullImageAsync(fullUri.ToString(), pageUrl, cancellationToken).ConfigureAwait(false);
                if (full is not null) { imageUrl = full; actualOriginal = true; }
            }
        }

        var resolved = new EhResolvedImage(imageUrl, actualOriginal);
        lock (_cacheGate)
        {
            if (_imageUrlCache.Count >= 512) _imageUrlCache.Clear();
            _imageUrlCache[key] = resolved;
        }
        return resolved;
    }

    /// <summary>视图页请求：串行化解析并按固定间隔节流，与阅读和下载共用。</summary>
    private async Task<string> FetchViewerHtmlAsync(string url, int width, CancellationToken cancellationToken)
    {
        await _viewerResolutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ThrottleViewerAsync(cancellationToken).ConfigureAwait(false);
            _session.SetImageSize(width);
            return await GetStringAsync(url, null, cancellationToken).ConfigureAwait(false);
        }
        finally { _viewerResolutionGate.Release(); }
    }

    private static string AppendRetryKey(string pageUrl, string nlKey)
    {
        string separator = pageUrl.Contains('?') ? "&" : "?";
        return pageUrl + separator + "nl=" + Uri.EscapeDataString(nlKey);
    }

    /// <summary>509.gif 是站点的图片占位响应。只认这一条路径，避免把
    /// 正常图床路径里的数字误判成配额错误。</summary>
    private static bool IsQuotaPlaceholder(string url) =>
        url.Contains("/509.gif", StringComparison.OrdinalIgnoreCase) ||
        url.EndsWith("/509", StringComparison.OrdinalIgnoreCase);

    private async Task<string?> ResolveFullImageAsync(string originalUrl, string pageUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, originalUrl);
            request.Headers.Referrer = new Uri(pageUrl);
            // Stop after headers: resolving an original must never buffer its pixels.
            using HttpResponseMessage response = await _web.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                return new Uri(new Uri(originalUrl), location).ToString();
            if (!response.IsSuccessStatusCode) return null;
            string final = response.RequestMessage?.RequestUri?.ToString() ?? originalUrl;
            if (LooksLikeImage(final) || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.Ordinal) == true)
                return final;
            string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string? src = HtmlLite.Parse(html).FirstDescendantById("img")?.GetAttribute("src");
            return string.IsNullOrEmpty(src) ? null : new Uri(new Uri(originalUrl), src).ToString();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is EhException or HttpRequestException or TaskCanceledException)
        {
            _log?.Invoke("Original image unavailable; using resampled image: " + ex.Message);
            return null;
        }
    }

    private static bool LooksLikeImage(string url) =>
        url.Contains(".jpg", StringComparison.OrdinalIgnoreCase) ||
        url.Contains(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        url.Contains(".png", StringComparison.OrdinalIgnoreCase) ||
        url.Contains(".gif", StringComparison.OrdinalIgnoreCase) ||
        url.Contains(".webp", StringComparison.OrdinalIgnoreCase);

    public Task DownloadToFileAsync(
        string url,
        string referer,
        string targetPath,
        IProgress<long>? progress,
        CancellationToken cancellationToken) =>
        DownloadToFileAsync(url, referer, targetPath,
            progress is null ? null : new Progress<Imaging.ImageLoadStatus>(s => progress.Report(s.ReceivedBytes)),
            cancellationToken);

    /// <summary>
    /// Streams <paramref name="url"/> to <paramref name="targetPath"/> and reports
    /// received bytes together with the server's Content-Length (null when the
    /// response has no trustworthy length). The file only appears at the target
    /// path once the transfer is complete and the signature is verified.
    /// </summary>
    public Task DownloadToFileAsync(
        string url,
        string referer,
        string targetPath,
        IProgress<Imaging.ImageLoadStatus>? progress,
        CancellationToken cancellationToken) =>
        DownloadToFileCoreAsync(_images, url, referer, targetPath,
            progress is null ? null : (received, total) =>
                progress.Report(new Imaging.ImageLoadStatus(Imaging.ImageLoadPhase.Downloading, received, total)),
            cancellationToken, imageAccept: true);

    private static async Task DownloadToFileCoreAsync(
        HttpClient client,
        string url,
        string referer,
        string targetPath,
        Action<long, long?>? progress,
        CancellationToken cancellationToken,
        bool imageAccept = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer.Length > 0 && Uri.TryCreate(referer, UriKind.Absolute, out Uri? refererUri))
        {
            request.Headers.Referrer = refererUri;
        }

        request.Headers.TryAddWithoutValidation(
            "Accept",
            imageAccept ? "image/webp,image/png,image/jpeg,image/gif,*/*;q=0.8" : "*/*");

        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string host = EhUrls.HostOf(url);
            throw new EhException(host.Length > 0
                ? $"下载失败：HTTP {(int)response.StatusCode}（{host}）"
                : $"下载失败：HTTP {(int)response.StatusCode}");
        }

        long? contentLength = response.Content.Headers.ContentLength;
        string tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".part";
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long total = 0;
            long lastReport = Environment.TickCount64;
            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, 64 * 1024), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    total += read;
                    long now = Environment.TickCount64;
                    if (now - lastReport >= 100) { progress?.Invoke(total, contentLength); lastReport = now; }
                }
            }
            progress?.Invoke(total, contentLength);
            cancellationToken.ThrowIfCancellationRequested();
            if (contentLength is long expected && total != expected)
                throw new IOException("下载数据不完整，请重试");
            if (total == 0)
                throw new EhException("下载结果为空，请重试");
            if (imageAccept && !ImageSignature.IsValidFile(tempPath))
                throw new EhException($"下载结果不是完整的受支持图片（{ImageSignature.DescribeFile(tempPath)}）");
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            try { File.Delete(tempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public async Task<byte[]> DownloadBytesAsync(string url, string referer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer.Length > 0 && Uri.TryCreate(referer, UriKind.Absolute, out Uri? refererUri))
        {
            request.Headers.Referrer = refererUri;
        }

        using HttpResponseMessage response = await _images.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new EhException($"HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ThrottleViewerAsync(CancellationToken cancellationToken)
    {
        await _viewerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TimeSpan elapsed = DateTime.UtcNow - _lastViewerRequestUtc;
            if (elapsed < TimeSpan.FromMilliseconds(ViewerThrottleMilliseconds))
            {
                await Task.Delay(ViewerThrottleMilliseconds - (int)elapsed.TotalMilliseconds, cancellationToken)
                    .ConfigureAwait(false);
            }

            _lastViewerRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            _viewerGate.Release();
        }
    }

    // ----------------------------------------------------------------- http

    public async Task<string> GetStringAsync(string url, string? referer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        if (!string.IsNullOrEmpty(referer)) request.Headers.Referrer = new Uri(referer);

        try
        {
            using HttpResponseMessage response = await _web
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string message = EhParser.DetectMessage(body) ?? $"HTTP {(int)response.StatusCode}";
                throw new EhException(message, requiresSignIn: response.StatusCode is HttpStatusCode.Forbidden &&
                                                              !IsSignedIn);
            }

            return body;
        }
        catch (HttpRequestException ex)
        {
            throw new EhException("网络请求失败：" + ex.Message, inner: ex);
        }
    }

    private async Task<string> PostFormAsync(
        string url,
        IReadOnlyDictionary<string, string> form,
        string? referer,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? target))
        {
            request.Headers.TryAddWithoutValidation("Origin", target.GetLeftPart(UriPartial.Authority));
        }

        if (!string.IsNullOrEmpty(referer)) request.Headers.Referrer = new Uri(referer);

        try
        {
            using HttpResponseMessage response = await _web.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Cloudflare's managed challenge answers 403 with an HTML
                // interstitial. Nothing in the request can satisfy it; only a
                // real browser can, so surface that instead of a bare status.
                bool challenge =
                    response.Headers.Contains("CF-Mitigated") ||
                    body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("challenges.cloudflare.com", StringComparison.OrdinalIgnoreCase);
                throw new EhException(challenge
                    ? "站点要求 Cloudflare 人机验证，无法直接提交表单；请使用「网页登录」，在弹出的浏览器中完成登录"
                    : $"HTTP {(int)response.StatusCode}");
            }

            return body;
        }
        catch (HttpRequestException ex)
        {
            throw new EhException("网络请求失败：" + ex.Message, inner: ex);
        }
    }

    private async Task<string> PostJsonAsync(
        string url,
        string json,
        string? referer,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? target))
        {
            request.Headers.TryAddWithoutValidation("Origin", target.GetLeftPart(UriPartial.Authority));
        }

        if (!string.IsNullOrEmpty(referer)) request.Headers.Referrer = new Uri(referer);

        try
        {
            using HttpResponseMessage response = await _web.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new EhException($"HTTP {(int)response.StatusCode}");
            }

            return body;
        }
        catch (HttpRequestException ex)
        {
            throw new EhException("网络请求失败：" + ex.Message, inner: ex);
        }
    }

    /// <summary>
    /// Downloads an arbitrary URL through the configured proxy stack (used by the
    /// tag-translation database, which lives on a third-party host).
    /// </summary>
    public async Task<byte[]> DownloadExternalAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json,application/gzip,*/*;q=0.8");

        using HttpResponseMessage response = await _web
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new EhException($"下载失败：HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _web.Dispose();
        _images.Dispose();
        _viewerGate.Dispose();
    }
}

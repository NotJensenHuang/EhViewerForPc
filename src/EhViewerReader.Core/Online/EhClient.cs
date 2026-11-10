using System.Net;
using System.Text;
using EhViewerReader.Core.Models;

namespace EhViewerReader.Core.Online;

public sealed class EhException : Exception
{
    public EhException(string message, bool requiresSignIn = false, Exception? inner = null)
        : base(message, inner)
    {
        RequiresSignIn = requiresSignIn;
    }

    public bool RequiresSignIn { get; }
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
    private readonly SemaphoreSlim _viewerGate = new(1, 1);
    private readonly Dictionary<string, string> _imageUrlCache = new(StringComparer.Ordinal);
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

    public EhSession Session => _session;

    public EhSite Site => _settings.Account.Site;

    public bool IsSignedIn => _session.HasCredentials;

    public bool ExAccess => _settings.Account.ExAccess;

    public string UserName => _settings.Account.LastUserName;

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
            return new EhLoginResult { Success = false, Message = "登录失败：" + ex.Message };
        }
    }

    /// <summary>Installs cookies pasted from a browser ("Cookie login").</summary>
    public bool ImportCookies(string cookieText)
    {
        Dictionary<string, string> cookies = EhSession.ParseCookieString(cookieText);
        if (cookies.Count == 0) return false;

        _session.Import(cookies);
        _session.MirrorAuthCookies();
        if (!_session.HasCredentials) return false;

        SaveSessionToSettings();
        _settings.Account.LastUserName = cookies.GetValueOrDefault("ipb_member_id", string.Empty);
        _settings.Account.LastSignInUtc = DateTime.UtcNow.ToString("O");
        return true;
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

    public async Task<int> GetFavoriteSlotAsync(long gid, string token, CancellationToken cancellationToken)
    {
        try
        {
            string url = EhUrls.GalleryPopup(Site, gid, token, "popup");
            string html = await GetStringAsync(url, EhUrls.Detail(Site, gid, token), cancellationToken).ConfigureAwait(false);
            return EhParser.ParseFavoriteSlot(html);
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException or TaskCanceledException)
        {
            _log?.Invoke("Favorite popup failed: " + ex.Message);
            return -1;
        }
    }

    public async Task<bool> SetFavoriteAsync(
        long gid,
        string token,
        int slot,
        bool add,
        string note,
        CancellationToken cancellationToken)
    {
        string action = add ? "addfav" : "deletefav";
        string url = EhUrls.GalleryPopup(Site, gid, token, action);
        var form = new Dictionary<string, string>
        {
            ["favcat"] = slot.ToString(),
            ["favnote"] = note ?? string.Empty,
            ["apply"] = "Apply Changes",
            ["update"] = "1",
        };

        string body = await PostFormAsync(url, form, EhUrls.Detail(Site, gid, token), cancellationToken)
            .ConfigureAwait(false);
        return !body.Contains("error", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("favorites.php", StringComparison.OrdinalIgnoreCase);
    }

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
    /// follows the site's "nl" link to the full-size file; otherwise the
    /// resampled image is returned.
    /// </summary>
    public async Task<string> GetImageUrlAsync(EhPageRef page, bool original, CancellationToken cancellationToken)
    {
        string pageUrl = page.PageUrl.Length > 0 ? page.PageUrl : EhUrls.ImagePage(Site, page);
        string key = pageUrl + (original ? "|orig" : "|res");
        lock (_cacheGate)
        {
            if (_imageUrlCache.TryGetValue(key, out string? cached)) return cached;
        }

        await ThrottleViewerAsync(cancellationToken).ConfigureAwait(false);
        string html = await GetStringAsync(pageUrl, null, cancellationToken).ConfigureAwait(false);
        if (!EhParser.TryParseImagePage(html, out string imageUrl, out string? nlKey, out string? error))
        {
            throw new EhException(error ?? "无法解析图片页");
        }

        if (original && nlKey is not null)
        {
            string? full = await ResolveFullImageAsync(pageUrl, nlKey, cancellationToken).ConfigureAwait(false);
            if (full is not null) imageUrl = full;
        }

        lock (_cacheGate)
        {
            if (_imageUrlCache.Count > 512) _imageUrlCache.Clear();
            _imageUrlCache[key] = imageUrl;
        }

        return imageUrl;
    }

    /// <summary>
    /// Asks the site for the full-size file. <paramref name="nlKey"/> comes from
    /// the viewer page's <c>onerror="nl('...')"</c> hook; the returned page then
    /// points <c>img#img</c> at the original image.
    /// </summary>
    private async Task<string?> ResolveFullImageAsync(string pageUrl, string nlKey, CancellationToken cancellationToken)
    {
        try
        {
            string separator = pageUrl.Contains('?') ? "&" : "?";
            string absolute = pageUrl + separator + "nl=" + Uri.EscapeDataString(nlKey);
            using var request = new HttpRequestMessage(HttpMethod.Get, absolute);
            request.Headers.Referrer = new Uri(pageUrl);
            using HttpResponseMessage response = await _web
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            Uri? final = response.RequestMessage?.RequestUri;
            if (final is not null && LooksLikeImage(final.ToString()))
            {
                return final.ToString();
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length == 0) return null;
            HtmlNode document = HtmlLite.Parse(body);
            HtmlNode? image = document.FirstDescendantById("img");
            string src = image?.GetAttribute("src") ?? string.Empty;
            return src.Length > 0 ? src : null;
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException or TaskCanceledException)
        {
            _log?.Invoke("Original image resolution failed: " + ex.Message);
            return null;
        }
    }

    private static bool LooksLikeImage(string url) =>
        url.Contains(".jpg", StringComparison.OrdinalIgnoreCase) ||
        url.Contains(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        url.Contains(".png", StringComparison.OrdinalIgnoreCase) ||
        url.Contains(".gif", StringComparison.OrdinalIgnoreCase) ||
        url.Contains(".webp", StringComparison.OrdinalIgnoreCase);

    public async Task DownloadToFileAsync(
        string url,
        string referer,
        string targetPath,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer.Length > 0 && Uri.TryCreate(referer, UriKind.Absolute, out Uri? refererUri))
        {
            request.Headers.Referrer = refererUri;
        }

        request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");

        using HttpResponseMessage response = await _images
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new EhException($"图片下载失败：HTTP {(int)response.StatusCode}");
        }

        string tempPath = targetPath + ".part";
        await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            byte[] buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total += read;
                progress?.Report(total);
            }
        }

        File.Move(tempPath, targetPath, overwrite: true);
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
                throw new EhException($"HTTP {(int)response.StatusCode}");
            }

            return body;
        }
        catch (HttpRequestException ex)
        {
            throw new EhException("网络请求失败：" + ex.Message, inner: ex);
        }
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

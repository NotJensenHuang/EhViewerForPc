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
            // The favourite popup is the addfav form; it preselects the slot
            // the gallery currently sits in (only meaningful when favourited).
            string url = EhUrls.GalleryPopup(Site, gid, token, "addfav");
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

        string body = await PostFormAsync(url, form, EhUrls.Detail(Site, gid, token), cancellationToken)
            .ConfigureAwait(false);

        if (body.Contains("requires you to log on", StringComparison.OrdinalIgnoreCase))
        {
            _log?.Invoke("Favorite update rejected: session expired");
            return false;
        }

        // The popup answers with a tiny confirmation page; anything that still
        // shows the form means the request was not applied.
        return !body.Contains("<form", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("favorites.php", StringComparison.OrdinalIgnoreCase);
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
        DownloadToFileCoreAsync(_web, url, EhUrls.Base(Site) + "/", targetPath, progress, cancellationToken);

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

    public Task DownloadToFileAsync(
        string url,
        string referer,
        string targetPath,
        IProgress<long>? progress,
        CancellationToken cancellationToken) =>
        DownloadToFileCoreAsync(_images, url, referer, targetPath, progress, cancellationToken, imageAccept: true);

    private static async Task DownloadToFileCoreAsync(
        HttpClient client,
        string url,
        string referer,
        string targetPath,
        IProgress<long>? progress,
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
            imageAccept ? "image/avif,image/webp,image/apng,image/*,*/*;q=0.8" : "*/*");

        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new EhException($"下载失败：HTTP {(int)response.StatusCode}");
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

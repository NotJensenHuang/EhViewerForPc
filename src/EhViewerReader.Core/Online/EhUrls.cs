using System.Text;

namespace EhViewerReader.Core.Online;

/// <summary>URL builders for every E-Hentai page the client talks to.</summary>
public static class EhUrls
{
    public const string EHentaiBase = "https://e-hentai.org";
    public const string ExHentaiBase = "https://exhentai.org";
    public const string ForumsBase = "https://forums.e-hentai.org";

    public static string Base(EhSite site) => site == EhSite.ExHentai ? ExHentaiBase : EHentaiBase;

    public static string SiteName(EhSite site) => site == EhSite.ExHentai ? "ExHentai" : "E-Hentai";

    public static string Login => ForumsBase + "/index.php?act=Login&CODE=01";

    public static string Home(EhSite site) => Base(site) + "/";

    public static string Popular(EhSite site, int page) =>
        Base(site) + "/popular" + (page > 0 ? "?page=" + page : string.Empty);

    public static string Watched(EhSite site, int page) =>
        Base(site) + "/watched" + (page > 0 ? "?page=" + page : string.Empty);

    public static string History(EhSite site, int page) =>
        Base(site) + "/history.php" + (page > 0 ? "?page=" + page : string.Empty);

    public static string Toplist(EhSite site, int toplistIndex, int page)
    {
        var builder = new StringBuilder(Base(site));
        builder.Append("/toplist.php?tl=").Append(toplistIndex);
        if (page > 0) builder.Append("&page=").Append(page);
        return builder.ToString();
    }

    public static string Search(EhSite site, EhSearchRequest request, int page)
    {
        var builder = new StringBuilder(Base(site));
        builder.Append("/?f_search=").Append(Uri.EscapeDataString(request.EffectiveQuery));

        // f_cats is the mask of categories to *exclude*.
        int exclude = EhCategories.All & ~request.CategoryMask;
        if (exclude != 0) builder.Append("&f_cats=").Append(exclude);

        if (request.MinPages > 0) builder.Append("&f_spf=").Append(request.MinPages);
        if (request.MaxPages > 0) builder.Append("&f_spt=").Append(request.MaxPages);

        // Advanced form is only sent when a non-default scope/option is used;
        // without it the site searches titles and tags by default.
        bool advanced = !request.SearchName || !request.SearchTags || request.SearchDescription ||
                        request.OnlyTorrents || request.OnlyExpunged;
        if (advanced)
        {
            builder.Append("&advsearch=1");
            if (request.SearchName) builder.Append("&f_sname=on");
            if (request.SearchTags) builder.Append("&f_stags=on");
            if (request.SearchDescription) builder.Append("&f_sdesc=on");
            if (request.OnlyTorrents) builder.Append("&f_storr=on");
            if (request.OnlyExpunged) builder.Append("&f_sto=on");
        }

        if (page > 0) builder.Append("&page=").Append(page);
        return builder.ToString();
    }

    public static string Favorites(EhSite site, int slot, EhSearchRequest? request, int page)
    {
        var builder = new StringBuilder(Base(site));
        builder.Append("/favorites.php?favcat=").Append(slot < 0 ? "favorites" : slot.ToString());
        if (page > 0) builder.Append("&page=").Append(page);
        if (request is not null && request.Query.Trim().Length > 0)
        {
            builder.Append("&f_search=").Append(Uri.EscapeDataString(request.Query.Trim()));
        }

        return builder.ToString();
    }

    public static string Detail(EhSite site, long gid, string token) =>
        $"{Base(site)}/g/{gid}/{token}/";

    public static string PageList(EhSite site, long gid, string token, int pageIndex) =>
        pageIndex <= 0
            ? $"{Base(site)}/g/{gid}/{token}/"
            : $"{Base(site)}/g/{gid}/{token}/?p={pageIndex}";

    public static string ImagePage(EhSite site, EhPageRef page) =>
        $"{Base(site)}/s/{page.Hash}/{page.Gid}-{page.Page}";

    public static string GalleryPopup(EhSite site, long gid, string token, string action) =>
        $"{Base(site)}/gallerypopups.php?gid={gid}&t={token}&act={action}";

    public static string Torrents(EhSite site, long gid, string token) =>
        $"{Base(site)}/gallerytorrents.php?gid={gid}&t={token}";

    public static string Archiver(EhSite site, long gid, string token) =>
        $"{Base(site)}/archiver.php?gid={gid}&token={token}";

    /// <summary>Archiver endpoint for one option; the option key is echoed as <c>or</c>.</summary>
    public static string ArchiveRequest(EhSite site, long gid, string token, string or) =>
        $"{Base(site)}/archiver.php?gid={gid}&token={token}&or={Uri.EscapeDataString(or)}";

    /// <summary>The site's H@H client management page.</summary>
    public static string HathClient(EhSite site) => Base(site) + "/hathdler.php";

    public static string Api(EhSite site) => Base(site) + "/api.php";

    /// <summary>Absolute URL for a possibly protocol-relative or relative href.</summary>
    public static string Absolute(EhSite site, string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return string.Empty;
        if (href.StartsWith("//", StringComparison.Ordinal)) return "https:" + href;
        if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return href;
        if (href.StartsWith('/')) return Base(site) + href;
        return Base(site) + "/" + href;
    }

    public static string HostOf(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return uri.Host;
        return string.Empty;
    }

    /// <summary>Returns (gid, token) when the href addresses a gallery page.</summary>
    public static bool TryParseGalleryHref(string href, out long gid, out string token)
    {
        gid = 0;
        token = string.Empty;
        if (string.IsNullOrEmpty(href)) return false;

        int marker = href.IndexOf("/g/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return false;

        ReadOnlySpan<char> rest = href.AsSpan(marker + 3);
        int slash = rest.IndexOf('/');
        if (slash <= 0) return false;

        if (!long.TryParse(rest[..slash], out gid)) return false;
        ReadOnlySpan<char> tail = rest[(slash + 1)..];
        int end = tail.IndexOfAny('/', '?', '#');
        if (end < 0) end = tail.Length;
        token = tail[..end].ToString();
        return token.Length > 0 && gid > 0;
    }

    /// <summary>Returns the page hash/page number when the href addresses an image viewer.</summary>
    public static bool TryParseImageHref(string href, out string hash, out long gid, out int page)
    {
        hash = string.Empty;
        gid = 0;
        page = 0;
        if (string.IsNullOrEmpty(href)) return false;

        int marker = href.IndexOf("/s/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return false;

        ReadOnlySpan<char> rest = href.AsSpan(marker + 3);
        int slash = rest.IndexOf('/');
        if (slash <= 0) return false;
        hash = rest[..slash].ToString();

        ReadOnlySpan<char> tail = rest[(slash + 1)..];
        int end = tail.IndexOfAny('/', '?', '#');
        if (end < 0) end = tail.Length;
        tail = tail[..end];

        // Viewer URLs are /s/{hash}/{gid}-{page} (gid first).
        int dash = tail.IndexOf('-');
        if (dash <= 0) return false;
        return long.TryParse(tail[..dash], out gid) &&
               int.TryParse(tail[(dash + 1)..], out page) &&
               gid > 0 && page > 0;
    }
}

using System.Text.Json.Serialization;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.Core.Online;

namespace EhViewerForPc.Core.Models;

public enum AppTheme
{
    System = 0,
    Dark = 1,
    Light = 2,
}

public enum ProxyMode
{
    None = 0,
    System = 1,
    Http = 2,
    Socks5 = 3,
    EhDirect = 4,
    Auto = 5,
}

public enum ReadingMode
{
    Single = 0,
    Continuous = 1,
    Double = 2,
}

public enum FitMode
{
    FitPage = 0,
    FitWidth = 1,
    FitHeight = 2,
    Original = 3,
}

public enum ReaderBackground
{
    Black = 0,
    DarkGray = 1,
    Gray = 2,
    White = 3,
}

public enum LibrarySort
{
    RecentlyRead = 0,
    RecentlyModified = 1,
    Title = 2,
    PageCount = 3,
    Gid = 4,
}

/// <summary>
/// Image size requested from the site. E-Hentai only exposes two sizes to a
/// client: the full-size file (per image) and the resampled one, whose width is
/// the account preference (780/980/1280/1600/2400, default 1280). The app
/// honours both for reading and downloading.
/// </summary>
public enum ImageQuality
{
    /// <summary>Full-size original; needs a signed-in account for most galleries.</summary>
    Original = 0,

    /// <summary>Site-resampled image; width follows the account setting (~1280).</summary>
    Resampled = 1,
}

public enum LibraryFilter
{
    All = 0,
    Reading = 1,
    Unread = 2,
    Completed = 3,
    Favorite = 4,
    Incomplete = 5,

    /// <summary>Works that exist in more than one local copy. Appended last so
    /// persisted values from older versions keep their meaning.</summary>
    Duplicates = 6,
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 7;

    /// <summary>Gets or sets optional on-device image retrieval preferences.</summary>
    public LocalImageSearchSettings ImageSearch { get; set; } = new();
    public string MangaTranslationModelDirectory { get; set; } = string.Empty;
    public string MangaTranslationModelId { get; set; } = string.Empty;
    public string MangaTranslationTargetLanguage { get; set; } = string.Empty;

    /// <summary>Legacy single-root field; migrated into <see cref="Sources"/> on load.</summary>
    public string? LibraryRoot { get; set; }

    public List<LibrarySource> Sources { get; set; } = [];

    public AccountSettings Account { get; set; } = new();

    public NetworkSettings Network { get; set; } = new();

    public OnlineSettings Online { get; set; } = new();

    public DownloadSettings Download { get; set; } = new();

    /// <summary>
    /// Image size shared by the online reader and the download engine; the
    /// gallery detail window can override it for one gallery.
    /// </summary>
    public ImageQuality ImageQuality { get; set; } = ImageQuality.Resampled;
    public int ResampledWidth { get; set; } = 1280;

    /// <summary>
    /// Work id -> preferred source id. Only populated for works that exist in
    /// more than one source and where the user picked a copy explicitly.
    /// </summary>
    public Dictionary<string, string> PreferredSourceByWork { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public ReadingMode DefaultReadingMode { get; set; } = ReadingMode.Single;
    public FitMode DefaultFitMode { get; set; } = FitMode.FitPage;
    public bool RightToLeft { get; set; } = true;
    public bool SeparateFirstPageInDoubleMode { get; set; } = true;
    public ReaderBackground ReaderBackground { get; set; } = ReaderBackground.Black;
    public int MaxDecodeMegapixels { get; set; } = 24;
    public int MaxDecodeDimension { get; set; } = 16000;

    /// <summary>Decoded-bitmap memory budget (not the online disk cache).</summary>
    public int PageCacheMegabytes { get; set; } = 256;

    /// <summary>
    /// On-disk budget for online pages and thumbnails. Separate from
    /// <see cref="PageCacheMegabytes"/>, which only covers decoded bitmaps.
    /// </summary>
    public int OnlineCacheMegabytes { get; set; } = 512;

    /// <summary>
    /// Days an unused online cache entry is kept before pruning; 0 means no
    /// time limit, so entries are only evicted when the disk budget is exceeded.
    /// </summary>
    public int OnlineCacheDays { get; set; }

    public int CoverDecodeWidth { get; set; } = 360;
    public LibrarySort Sort { get; set; } = LibrarySort.RecentlyRead;
    public LibraryFilter Filter { get; set; } = LibraryFilter.All;
    public string? LanguageFilter { get; set; }

    /// <summary>Recent online search terms (newest first).</summary>
    public List<string> SearchHistory { get; set; } = [];

    /// <summary>User-saved searches, including category mask and page range.</summary>
    public List<SavedSearchEntry> SavedSearches { get; set; } = [];
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 700;
    public bool SidebarCollapsed { get; set; }
    public bool WindowMaximized { get; set; }
    public bool CloseToTray { get; set; } = true;
    public string? LastOpenedStableId { get; set; }
    public bool ShowChromeInReader { get; set; } = true;
    public string? LastScanCompletedUtc { get; set; }

    public AppSettings Normalize()
    {
        ImageSearch ??= new();
        ImageSearch.Normalize();
        MangaTranslationModelDirectory ??= string.Empty;
        MangaTranslationModelId ??= string.Empty;
        MangaTranslationTargetLanguage ??= string.Empty;
        if (SchemaVersion < 5) { ImageQuality = ImageQuality.Resampled; SchemaVersion = 5; }

        if (SchemaVersion < 6)
        {
            // New online-cache defaults: 512 MiB and no retention limit. Only
            // values still equal to the old defaults are rewritten, so an
            // explicit choice made by the user survives the upgrade.
            if (OnlineCacheMegabytes == 2048) OnlineCacheMegabytes = 512;
            if (OnlineCacheDays == 14) OnlineCacheDays = 0;
            SchemaVersion = 6;
        }
        if (ResampledWidth is not (780 or 980 or 1280 or 1600 or 2400)) ResampledWidth = 1280;
        if (SchemaVersion < 7) SchemaVersion = 7;
        MaxDecodeMegapixels = Math.Clamp(MaxDecodeMegapixels, 4, 96);
        MaxDecodeDimension = Math.Clamp(MaxDecodeDimension, 2048, 16384);
        PageCacheMegabytes = Math.Clamp(PageCacheMegabytes, 64, 1536);
        OnlineCacheMegabytes = Math.Clamp(OnlineCacheMegabytes, 128, 32768);
        OnlineCacheDays = Math.Clamp(OnlineCacheDays, 0, 90);
        CoverDecodeWidth = Math.Clamp(CoverDecodeWidth, 160, 1024);
        WindowWidth = Math.Clamp(WindowWidth, 540, 10000);
        WindowHeight = Math.Clamp(WindowHeight, 420, 10000);
        if (!Enum.IsDefined(Theme)) Theme = AppTheme.Dark;
        if (!Enum.IsDefined(DefaultReadingMode)) DefaultReadingMode = ReadingMode.Single;
        if (!Enum.IsDefined(DefaultFitMode)) DefaultFitMode = FitMode.FitPage;
        if (!Enum.IsDefined(ReaderBackground)) ReaderBackground = ReaderBackground.Black;
        if (!Enum.IsDefined(Sort)) Sort = LibrarySort.RecentlyRead;
        if (!Enum.IsDefined(Filter)) Filter = LibraryFilter.All;
        if (!Enum.IsDefined(ImageQuality)) ImageQuality = ImageQuality.Resampled;
        Account ??= new AccountSettings();
        Account.Normalize();
        Network ??= new NetworkSettings();
        Network.Normalize();
        Online ??= new OnlineSettings();
        Online.Normalize();
        Download ??= new DownloadSettings();
        Download.Normalize();
        SearchHistory ??= [];
        SavedSearches ??= [];
        SavedSearches.RemoveAll(s => s is null || string.IsNullOrWhiteSpace(s.Name));
        NormalizeSources();
        PreferredSourceByWork = new Dictionary<string, string>(
            PreferredSourceByWork ?? [], StringComparer.OrdinalIgnoreCase);
        return this;
    }

    private void NormalizeSources()
    {
        Sources ??= [];

        // Schema 1 stored a single root; adopt it as the first source. Existence
        // is not required: an unplugged backup drive must survive a restart
        // instead of silently dropping out of the source list.
        if (Sources.Count == 0 && !string.IsNullOrWhiteSpace(LibraryRoot))
        {
            Sources.Add(new LibrarySource
            {
                Id = NewSourceId(LibraryRoot),
                RootPath = Path.GetFullPath(LibraryRoot),
            });
        }

        var seenPaths = new HashSet<string>(PathIdentity.Comparer);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<LibrarySource>(Sources.Count);
        foreach (LibrarySource source in Sources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.RootPath)) continue;

            string normalized;
            try
            {
                normalized = NormalizeRoot(source.RootPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!seenPaths.Add(normalized)) continue;
            source.RootPath = normalized;

            if (string.IsNullOrWhiteSpace(source.Id) || !seenIds.Add(source.Id))
            {
                source.Id = NewSourceId(normalized);
                seenIds.Add(source.Id);
            }

            kept.Add(source);
        }

        // Keep the original list instance where possible so callers holding a
        // reference (e.g. the sources editor) observe the cleanup.
        Sources.Clear();
        Sources.AddRange(kept);

        foreach (LibrarySource source in Sources)
        {
            if (source.Id.Length == 0) source.Id = NewSourceId(source.RootPath);
        }
    }

    /// <summary>Absolute path without a trailing separator, so "C://lib//" and "C://lib" match.</summary>
    public static string NormalizeRoot(string rootPath) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath.Trim()));

    private static string NewSourceId(string rootPath) =>
        "src-" + Format.StableIdFactory.PathHash(rootPath);

    public LibrarySource AddSource(string rootPath, string? name = null)
    {
        string full = NormalizeRoot(rootPath);
        LibrarySource? existing = Sources.FirstOrDefault(s =>
            PathIdentity.Comparer.Equals(s.RootPath, full));
        if (existing is not null)
        {
            existing.Enabled = true;
            return existing;
        }

        var source = new LibrarySource
        {
            Id = NewSourceId(full),
            RootPath = full,
            Name = name,
        };
        Sources.Add(source);
        return source;
    }

    public void RemoveSource(string id)
    {
        Sources.RemoveAll(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        foreach (string key in PreferredSourceByWork
                     .Where(p => string.Equals(p.Value, id, StringComparison.OrdinalIgnoreCase))
                     .Select(p => p.Key)
                     .ToArray())
        {
            PreferredSourceByWork.Remove(key);
        }
    }

    [JsonIgnore]
    public IReadOnlyList<LibrarySource> ActiveSources =>
        Sources.Where(s => s.Enabled).ToList();
}

/// <summary>
/// E-Hentai / ExHentai account state. Only cookies are persisted: the client
/// never stores the account password, same as the original Android app.
/// </summary>
public sealed class AccountSettings
{
    /// <summary>Site the user browses by default.</summary>
    public EhSite Site { get; set; } = EhSite.EHentai;

    /// <summary>Cookie name → value (ipb_member_id, ipb_pass_hash, igneous, sk).</summary>
    public Dictionary<string, string> Cookies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string LastUserName { get; set; } = string.Empty;

    /// <summary>Result of the last ExHentai access probe.</summary>
    public bool ExAccess { get; set; }

    public string LastSignInUtc { get; set; } = string.Empty;

    /// <summary>User-defined names for the ten favourite slots (index 0–9).</summary>
    public List<string> FavoriteSlotNames { get; set; } = [];

    public void Normalize()
    {
        Cookies ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Cookies = new Dictionary<string, string>(Cookies, StringComparer.OrdinalIgnoreCase);
        if (!Enum.IsDefined(Site)) Site = EhSite.EHentai;

        FavoriteSlotNames ??= [];
        for (int slot = 0; slot < 10; slot++)
        {
            string name = slot < FavoriteSlotNames.Count ? FavoriteSlotNames[slot]?.Trim() ?? string.Empty : string.Empty;
            if (name.Length == 0) name = "分类 " + slot;
            if (name.Length > 32) name = name[..32];

            if (slot < FavoriteSlotNames.Count) FavoriteSlotNames[slot] = name;
            else FavoriteSlotNames.Add(name);
        }

        if (FavoriteSlotNames.Count > 10) FavoriteSlotNames.RemoveRange(10, FavoriteSlotNames.Count - 10);
    }

    public string FavoriteSlotName(int slot) =>
        slot >= 0 && slot < FavoriteSlotNames.Count && FavoriteSlotNames[slot].Length > 0
            ? FavoriteSlotNames[slot]
            : "分类 " + slot;

    [JsonIgnore]
    public bool HasCredentials =>
        Cookies.TryGetValue("ipb_member_id", out string? id) && id.Length > 0 &&
        Cookies.TryGetValue("ipb_pass_hash", out string? hash) && hash.Length > 0;

    [JsonIgnore]
    public string CookieHeader => string.Join("; ",
        Cookies.Where(p => p.Value.Length > 0).Select(p => p.Key + "=" + p.Value));
}

/// <summary>Proxy / DNS configuration shared by browsing, covers and downloads.</summary>
public sealed class NetworkSettings
{
    public ProxyMode ProxyMode { get; set; } = ProxyMode.Auto;
    public string ProxyHost { get; set; } = string.Empty;
    public int ProxyPort { get; set; }
    public string ProxyUserName { get; set; } = string.Empty;
    public string ProxyPassword { get; set; } = string.Empty;

    /// <summary>When false, image hosts bypass the proxy (direct connection).</summary>
    public bool ProxyForImages { get; set; } = true;

    /// <summary>Resolve host names over HTTPS instead of the system resolver.</summary>
    public bool DnsOverHttps { get; set; }

    public string DohEndpoint { get; set; } = "https://cloudflare-dns.com/dns-query";

    /// <summary>Extra host → IP lines (Windows hosts syntax, both orders accepted).</summary>
    public string HostsOverride { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 40;

    public void Normalize()
    {
        if (!Enum.IsDefined(ProxyMode)) ProxyMode = ProxyMode.Auto;
        ProxyHost = ProxyHost?.Trim() ?? string.Empty;
        ProxyUserName ??= string.Empty;
        ProxyPassword ??= string.Empty;
        DohEndpoint = string.IsNullOrWhiteSpace(DohEndpoint) ? "https://cloudflare-dns.com/dns-query" : DohEndpoint.Trim();
        HostsOverride ??= string.Empty;
        ProxyPort = Math.Clamp(ProxyPort, 0, 65535);
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 8, 300);
    }

    public bool HasProxy =>
        ProxyMode is ProxyMode.Http or ProxyMode.Socks5 && ProxyHost.Length > 0 && ProxyPort > 0;
}

/// <summary>
/// Online browsing preferences: tag translation, list filters and the quality
/// used by the online reader.
/// </summary>
public sealed class OnlineSettings
{
    /// <summary>Community translation database (compressed JSON, ~1.3 MB).</summary>
    public const string DefaultTagTranslationUrl =
        "https://github.com/EhTagTranslation/Database/releases/latest/download/db.text.json.gz";

    public bool ShowTagTranslations { get; set; } = true;

    public string TagTranslationUrl { get; set; } = DefaultTagTranslationUrl;

    public string TagTranslationVersion { get; set; } = string.Empty;
    public string TagTranslationUpdatedUtc { get; set; } = string.Empty;
    public int TagTranslationCount { get; set; }

    /// <summary>Include/exclude rules applied to every online listing.</summary>
    public List<FilterRule> Filters { get; set; } = [];

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(TagTranslationUrl)) TagTranslationUrl = DefaultTagTranslationUrl;
        TagTranslationUrl = TagTranslationUrl.Trim();
        Filters ??= [];
        Filters.RemoveAll(f => f is null);
        foreach (FilterRule rule in Filters)
        {
            rule.Text = rule.Text?.Trim() ?? string.Empty;
            if (!Enum.IsDefined(rule.Target)) rule.Target = FilterTarget.Tag;
        }

        Filters.RemoveAll(f => f.Text.Length == 0);
    }

    public bool HasFilters => Filters.Any(f => f.Enabled && f.Text.Length > 0);
}

/// <summary>
/// Download engine configuration. Downloads are written in EhViewer's folder
/// layout, so the folder can be registered as an ordinary library source and
/// stays readable by the Android app.
/// </summary>
public sealed class DownloadSettings
{
    /// <summary>Empty means the app's default download directory.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Register the download root as a library source automatically.</summary>
    public bool AddAsSource { get; set; } = true;

    /// <summary>Label → sub folder, mirroring E-Hentai's favorite-style grouping.</summary>
    public string DefaultLabel { get; set; } = string.Empty;

    public int MaxConcurrentTasks { get; set; } = 2;
    public int MaxConcurrentPages { get; set; } = 2;

    public bool WriteThumb { get; set; } = true;
    public bool WriteEhviewerHeader { get; set; } = true;
    public bool AutoStart { get; set; } = true;

    /// <summary>Automatically rescan the library after a task completes.</summary>
    public bool RefreshLibraryOnComplete { get; set; } = true;

    public void Normalize()
    {
        RootPath = RootPath?.Trim() ?? string.Empty;
        DefaultLabel = DefaultLabel?.Trim() ?? string.Empty;
        MaxConcurrentTasks = Math.Clamp(MaxConcurrentTasks, 1, 8);
        MaxConcurrentPages = Math.Clamp(MaxConcurrentPages, 1, 8);
    }
}

/// <summary>Serialisable form of a saved search.</summary>
public sealed class SavedSearchEntry
{
    /// <summary>Gets or sets the stable identity used by the bookmark editor.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Gets or sets the upstream ordering value.</summary>
    public long Time { get; set; }
    /// <summary>Gets or sets the complete request; null preserves older settings documents.</summary>
    public EhViewerForPc.Core.Online.EhSearchRequest? Search { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public int CategoryMask { get; set; } = 1023;
    public int MinPages { get; set; }
    public int MaxPages { get; set; }
    public string Language { get; set; } = string.Empty;

    /// <summary>Creates an independent request, including migration from older saved searches.</summary>
    public EhViewerForPc.Core.Online.EhSearchRequest ToRequest() => Search?.Clone() ?? new()
    { Query = Query, CategoryMask = CategoryMask, MinPages = MinPages, MaxPages = MaxPages, Language = Language };

    /// <summary>Creates a bookmark retaining every search option.</summary>
    public static SavedSearchEntry Create(string name, EhViewerForPc.Core.Online.EhSearchRequest request) => new()
    { Name = name, Search = request.Clone(), Query = request.Query, CategoryMask = request.CategoryMask,
        MinPages = request.MinPages, MaxPages = request.MaxPages, Language = request.Language,
        Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
}

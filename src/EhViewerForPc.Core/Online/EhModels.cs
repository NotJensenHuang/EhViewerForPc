using System.Text;

namespace EhViewerForPc.Core.Online;

public enum EhSite
{
    EHentai = 0,
    ExHentai = 1,
}

/// <summary>E-Hentai categories. The value is the bit index used by <c>f_cats</c>.</summary>
public enum EhCategory
{
    Misc = 0,
    Doujinshi = 1,
    Manga = 2,
    ArtistCG = 3,
    GameCG = 4,
    ImageSet = 5,
    Cosplay = 6,
    AsianPorn = 7,
    NonH = 8,
    Western = 9,
}

public static class EhCategories
{
    public const int All = 1023;

    public static readonly EhCategory[] AllCategories =
    [
        EhCategory.Misc, EhCategory.Doujinshi, EhCategory.Manga, EhCategory.ArtistCG, EhCategory.GameCG,
        EhCategory.ImageSet, EhCategory.Cosplay, EhCategory.AsianPorn, EhCategory.NonH, EhCategory.Western,
    ];

    public static string GetDisplayName(EhCategory category) => category switch
    {
        EhCategory.Misc => "杂项",
        EhCategory.Doujinshi => "同人志",
        EhCategory.Manga => "漫画",
        EhCategory.ArtistCG => "画集",
        EhCategory.GameCG => "游戏 CG",
        EhCategory.ImageSet => "图集",
        EhCategory.Cosplay => "Cosplay",
        EhCategory.AsianPorn => "亚洲",
        EhCategory.NonH => "非 H",
        EhCategory.Western => "欧美",
        _ => category.ToString(),
    };

    /// <summary>Maps a category cell/class ("Doujinshi", "doujinshi", "Artist CG") to the enum.</summary>
    public static EhCategory? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string key = text.Trim().Replace(" ", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
        return key switch
        {
            "misc" => EhCategory.Misc,
            "doujinshi" => EhCategory.Doujinshi,
            "manga" => EhCategory.Manga,
            "artistcg" => EhCategory.ArtistCG,
            "gamecg" => EhCategory.GameCG,
            "imageset" => EhCategory.ImageSet,
            "cosplay" => EhCategory.Cosplay,
            "asianporn" => EhCategory.AsianPorn,
            "nonh" => EhCategory.NonH,
            "western" => EhCategory.Western,
            _ => null,
        };
    }

    public static string GetColor(EhCategory category) => category switch
    {
        EhCategory.Misc => "#8C94A3",
        EhCategory.Doujinshi => "#E2556B",
        EhCategory.Manga => "#E0A64B",
        EhCategory.ArtistCG => "#4FA3E3",
        EhCategory.GameCG => "#7C6BE3",
        EhCategory.ImageSet => "#5AD1A0",
        EhCategory.Cosplay => "#E36BA8",
        EhCategory.AsianPorn => "#D96A5A",
        EhCategory.NonH => "#9BB35A",
        EhCategory.Western => "#5AB9C9",
        _ => "#8C94A3",
    };

    public static int MaskOf(IEnumerable<EhCategory> categories)
    {
        int mask = 0;
        foreach (EhCategory category in categories) mask |= 1 << (int)category;
        return mask;
    }
}

public sealed class EhTag
{
    public string Namespace { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Search fragment that reproduces this tag ("female:sole female").</summary>
    public string SearchTerm => Namespace.Length == 0 ? Name : Namespace + ":" + Name;

    public override string ToString() => SearchTerm;
}

public sealed class EhGallerySummary
{
    public long Gid { get; init; }
    public string Token { get; init; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string CategoryKey { get; set; } = string.Empty;
    public string CategoryColor { get; set; } = "#8C94A3";
    public string ThumbUrl { get; set; } = string.Empty;
    public string Uploader { get; set; } = string.Empty;
    public int Pages { get; set; }
    public string Posted { get; set; } = string.Empty;
    public double Rating { get; set; }
    public bool HasRating => Rating > 0;
    public string RatingText => Rating > 0 ? Rating.ToString("F2") : "—";

    public string TokenUrl => $"/g/{Gid}/{Token}/";
    public string PagesText => Pages > 0 ? $"{Pages} 页" : "—";
    public string CategoryDisplay => Category.Length > 0 ? Category : "—";

    /// <summary>Tag chips rendered inside the list row ("female:sole female").</summary>
    public List<string> Tags { get; } = [];
    public string TagsText => string.Join(" ", Tags);
}

/// <summary>A gallery's full detail page.</summary>
public sealed class EhGalleryDetails
{
    public long Gid { get; init; }
    public string Token { get; init; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string JapaneseTitle { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string CategoryKey { get; set; } = string.Empty;
    public string CategoryColor { get; set; } = "#8C94A3";
    public string Uploader { get; set; } = string.Empty;
    public string Posted { get; set; } = string.Empty;
    public string Parent { get; set; } = string.Empty;
    public string Visible { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string FileSize { get; set; } = string.Empty;
    public int Pages { get; set; }
    public string ThumbUrl { get; set; } = string.Empty;
    public double Rating { get; set; }
    public int FavoriteSlot { get; set; } = -1;

    /// <summary>
    /// True when the detail page's #favoritelink no longer reads
    /// "Add to Favorites". This is the authoritative signal: the favourite
    /// slot is only exposed by the popup, which can fail independently.
    /// </summary>
    public bool IsFavorited { get; set; }

    /// <summary>Favourite category name shown on the detail page ("Favorite 3" or a custom name).</summary>
    public string FavoriteName { get; set; } = string.Empty;

    /// <summary>Favourite count text from #favcount ("28 times").</summary>
    public string Favorited { get; set; } = string.Empty;

    public bool IsFavorite => IsFavorited || FavoriteSlot >= 0;

    /// <summary>Number of votes behind <see cref="Rating"/> (#rating_count).</summary>
    public int RatingCount { get; set; }

    /// <summary>Absolute gallerytorrents.php URL taken from #gd5 (empty when absent).</summary>
    public string TorrentUrl { get; set; } = string.Empty;
    public int TorrentCount { get; set; }

    /// <summary>Absolute archiver.php URL taken from #gd5 (empty when absent).</summary>
    public string ArchiveUrl { get; set; } = string.Empty;

    /// <summary>
    /// API identity embedded in the detail page. Rating and comment voting are
    /// signed with it; anonymous pages carry <c>apiuid = -1</c>.
    /// </summary>
    public long ApiUid { get; set; } = -1;
    public string ApiKey { get; set; } = string.Empty;
    public bool CanUseApi => ApiUid > 0 && ApiKey.Length > 0;

    /// <summary>Comments visible on the detail page (newest first).</summary>
    public List<EhComment> Comments { get; } = [];
    public bool HasMoreComments { get; set; }
    public bool CanPostComment { get; set; }

    public List<EhTag> Tags { get; } = [];
    public DateTime FetchedUtc { get; set; } = DateTime.UtcNow;

    public string DisplayTitle => Title.Length > 0 ? Title : JapaneseTitle;
    public string RatingText => Rating > 0 ? Rating.ToString("F2") : "—";

    public string TagSummary
    {
        get
        {
            if (Tags.Count == 0) return string.Empty;
            var builder = new StringBuilder();
            foreach (EhTag tag in Tags)
            {
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(tag.SearchTerm);
                if (builder.Length > 200) break;
            }

            return builder.ToString();
        }
    }
}

/// <summary>One image page reference inside a gallery (the /s/… viewer page).</summary>
public sealed class EhPageRef
{
    public required string Hash { get; init; }
    public required long Gid { get; init; }
    /// <summary>1-based page number.</summary>
    public required int Page { get; init; }
    public string PageUrl { get; init; } = string.Empty;
    public string ThumbUrl { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
}

public sealed class EhPageList
{
    public List<EhPageRef> Pages { get; } = [];
    public bool HasMore { get; set; }
    public bool RequiresHath { get; set; }

    /// <summary>Last preview-pagination index advertised by the page links, -1
    /// when the site renders no pagination. Lets the reader size the page slider
    /// for galleries whose detail page omitted the page count.</summary>
    public int LastPageIndex { get; set; } = -1;
}

/// <summary>A parsed gallery listing (front page, search, favorites, …).</summary>
public sealed class EhGalleryListPage
{
    /// <summary>Gets or sets the final response URL, including an image-search session URL.</summary>
    public string SourceUrl { get; set; } = string.Empty;
    public List<EhGallerySummary> Items { get; } = [];
    public bool HasMore { get; set; }
    public int PageIndex { get; set; }
    public string? StatusMessage { get; set; }

    /// <summary>Absolute href of the site's own "next page" link, when present.</summary>
    public string NextUrl { get; set; } = string.Empty;
}

/// <summary>Identifies an upstream list/search mode; values match Android QuickSearch exports.</summary>
public enum EhSearchMode
{
    /// <summary>Searches the public index.</summary>
    Normal = 0,
    /// <summary>Browses one uploader.</summary>
    Uploader = 1,
    /// <summary>Browses one exact tag.</summary>
    Tag = 2,
    /// <summary>Browses popular galleries.</summary>
    Popular = 3,
    /// <summary>Searches subscribed tags.</summary>
    Subscription = 5,
    /// <summary>Runs the upstream filter query.</summary>
    Filter = 6,
    /// <summary>Browses a gallery leaderboard.</summary>
    Toplist = 7,
}

/// <summary>Advanced search parameters, mirroring EhViewer's search screen.</summary>
public sealed class EhSearchRequest
{
    /// <summary>Gets or sets the search destination.</summary>
    public EhSearchMode Mode { get; set; }
    /// <summary>Gets or sets whether advanced values are sent to the site.</summary>
    public bool AdvancedEnabled { get; set; } = true;
    /// <summary>Gets or sets the gallery leaderboard period (11, 12, 13 or 15).</summary>
    public int ToplistIndex { get; set; } = 15;
    public string Query { get; set; } = string.Empty;
    public int CategoryMask { get; set; } = EhCategories.All;
    public int MinPages { get; set; }
    public int MaxPages { get; set; }
    public bool SearchName { get; set; } = true;
    public bool SearchTags { get; set; } = true;
    public bool SearchDescription { get; set; }
    /// <summary>Gets or sets whether torrent filenames are searched.</summary>
    public bool SearchTorrentNames { get; set; }
    public bool OnlyTorrents { get; set; }
    public bool OnlyExpunged { get; set; }
    /// <summary>Gets or sets whether low-power tags are searched.</summary>
    public bool SearchLowPowerTags { get; set; }
    /// <summary>Gets or sets whether downvoted tags are searched.</summary>
    public bool SearchDownvotedTags { get; set; }
    /// <summary>Gets or sets whether the site's language exclusions are bypassed.</summary>
    public bool DisableLanguageFilter { get; set; }
    /// <summary>Gets or sets whether the site's uploader exclusions are bypassed.</summary>
    public bool DisableUploaderFilter { get; set; }
    /// <summary>Gets or sets whether the site's tag exclusions are bypassed.</summary>
    public bool DisableTagFilter { get; set; }
    /// <summary>Gets or sets the minimum rating, or zero to disable it.</summary>
    public int MinRating { get; set; }
    public string Language { get; set; } = string.Empty;

    public bool IsEmpty =>
        Query.Trim().Length == 0 && CategoryMask == EhCategories.All && MinPages <= 0 && MaxPages <= 0 &&
        Mode == EhSearchMode.Normal && SearchName && SearchTags && !SearchDescription &&
        !SearchTorrentNames && !OnlyTorrents && !OnlyExpunged && !SearchLowPowerTags &&
        !SearchDownvotedTags && !DisableLanguageFilter && !DisableUploaderFilter && !DisableTagFilter &&
        MinRating == 0 && Language.Length == 0;

    public string EffectiveQuery
    {
        get
        {
            string query = Query.Trim();
            if (Language.Length > 0 && Mode is EhSearchMode.Normal or EhSearchMode.Subscription or EhSearchMode.Filter)
            {
                string tag = "language:" + Language;
                query = query.Length == 0 ? tag : query + " " + tag;
            }

            return query;
        }
    }

    public EhSearchRequest Clone() => new()
    {
        Mode = Mode,
        AdvancedEnabled = AdvancedEnabled,
        ToplistIndex = ToplistIndex,
        Query = Query,
        CategoryMask = CategoryMask,
        MinPages = MinPages,
        MaxPages = MaxPages,
        SearchName = SearchName,
        SearchTags = SearchTags,
        SearchDescription = SearchDescription,
        SearchTorrentNames = SearchTorrentNames,
        OnlyTorrents = OnlyTorrents,
        OnlyExpunged = OnlyExpunged,
        SearchLowPowerTags = SearchLowPowerTags,
        SearchDownvotedTags = SearchDownvotedTags,
        DisableLanguageFilter = DisableLanguageFilter,
        DisableUploaderFilter = DisableUploaderFilter,
        DisableTagFilter = DisableTagFilter,
        MinRating = MinRating,
        Language = Language,
    };
}

public sealed class EhLoginResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool ExAccess { get; init; }
    public bool ExAccessKnown { get; init; }

    /// <summary>
    /// The server answered with a Cloudflare challenge (or another bot check),
    /// which no amount of HTTP tweaking can pass: the caller must log in through
    /// a real browser window and hand the cookies back.
    /// </summary>
    public bool RequiresBrowserLogin { get; init; }
}

/// <summary>One .torrent offered on the gallerytorrents.php page.</summary>
public sealed class EhTorrent
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Posted { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public string Uploader { get; set; } = string.Empty;
    public int Seeds { get; set; }
    public int Peers { get; set; }
    public int Downloads { get; set; }

    public string HealthText => $"{Seeds} 做种 / {Peers} 下载";
    public string StatsText => string.Join(" · ",
        new[] { Size, HealthText, Downloads > 0 ? Downloads + " 次下载" : string.Empty }
            .Where(s => s.Length > 0));
}

public sealed class EhTorrentList
{
    public List<EhTorrent> Items { get; } = [];
    public string Message { get; set; } = string.Empty;
    public bool RequiresSignIn { get; set; }
}

/// <summary>One comment on the detail page.</summary>
public sealed class EhComment
{
    public long Id { get; set; }
    public string User { get; set; } = string.Empty;
    public string Posted { get; set; } = string.Empty;
    public int Score { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool IsUploader { get; set; }
    public bool IsEdited { get; set; }

    /// <summary>Vote breakdown from .c7 ("Base +23, Epherion +12").</summary>
    public string VoteDetail { get; set; } = string.Empty;
    public bool CanVoteUp { get; set; }
    public bool CanVoteDown { get; set; }
    public bool CanEdit { get; set; }

    public string ScoreText => Score > 0 ? "+" + Score : Score.ToString();
    public string PostedText => Posted.Length > 0 ? Posted : "—";
}

/// <summary>Result of a comment post / vote.</summary>
public sealed class EhCommentActionResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public int Score { get; init; }
    public string VoteDetail { get; init; } = string.Empty;
}

/// <summary>Comments loaded from the detail page (optionally ?hc=1 = all).</summary>
public sealed class EhCommentList
{
    public List<EhComment> Comments { get; } = [];
    public bool HasMore { get; set; }
    public bool CanPost { get; set; }
}

/// <summary>High-level outcome of a favourite change.</summary>
public enum FavoriteUpdateOutcome
{
    /// <summary>The site (or a following read-only check) confirms the change.</summary>
    Succeeded = 0,

    /// <summary>The site did not apply the change.</summary>
    Rejected = 1,

    /// <summary>The request did not produce a verifiable answer; do not retry blindly.</summary>
    Unknown = 2,
}

/// <summary>
/// Result of adding/removing a favourite. <see cref="IsFavorited"/> and
/// <see cref="Slot"/> carry the state read back from the site; they are only
/// meaningful when <see cref="Verified"/> is true.
/// </summary>
public sealed record FavoriteUpdateResult(
    FavoriteUpdateOutcome Outcome,
    string Message,
    bool IsFavorited,
    int Slot,
    bool Verified)
{
    public bool SlotKnown => Slot >= 0;

    public static FavoriteUpdateResult Unknown(string message) => new(FavoriteUpdateOutcome.Unknown, message, false, -1, false);
}

/// <summary>Favourite popup state: selected category and the stored note.</summary>
public sealed record EhFavoritePopup(int Slot, string Note);

/// <summary>One archive flavour offered by archiver.php.</summary>
public sealed class EhArchiveOption
{
    public required string Key { get; init; }
    public required string Label { get; init; }
}

/// <summary>Parsed archiver.php form (login required by the site).</summary>
public sealed class EhArchivePage
{
    public string Or { get; set; } = string.Empty;
    public List<EhArchiveOption> Options { get; } = [];
    public string Message { get; set; } = string.Empty;
    public bool RequiresSignIn { get; set; }
    public bool Generated { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
}

/// <summary>Outcome of asking the site to build an archive.</summary>
public sealed class EhArchiveResult
{
    public bool Success { get; init; }
    public bool NeedsHathClient { get; init; }
    public bool Queued { get; init; }
    public string DownloadUrl { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

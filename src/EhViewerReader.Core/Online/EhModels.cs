using System.Text;

namespace EhViewerReader.Core.Online;

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

    /// <summary>Favourite count text from #favcount ("28 times").</summary>
    public string Favorited { get; set; } = string.Empty;
    public bool IsFavorite => FavoriteSlot >= 0;
    public string Torrents { get; set; } = string.Empty;
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
}

/// <summary>A parsed gallery listing (front page, search, favorites, …).</summary>
public sealed class EhGalleryListPage
{
    public List<EhGallerySummary> Items { get; } = [];
    public bool HasMore { get; set; }
    public int PageIndex { get; set; }
    public string? StatusMessage { get; set; }

    /// <summary>Absolute href of the site's own "next page" link, when present.</summary>
    public string NextUrl { get; set; } = string.Empty;
}

/// <summary>Advanced search parameters, mirroring EhViewer's search screen.</summary>
public sealed class EhSearchRequest
{
    public string Query { get; set; } = string.Empty;
    public int CategoryMask { get; set; } = EhCategories.All;
    public int MinPages { get; set; }
    public int MaxPages { get; set; }
    public bool SearchName { get; set; } = true;
    public bool SearchTags { get; set; } = true;
    public bool SearchDescription { get; set; }
    public bool OnlyTorrents { get; set; }
    public bool OnlyExpunged { get; set; }
    public string Language { get; set; } = string.Empty;

    public bool IsEmpty =>
        Query.Trim().Length == 0 && CategoryMask == EhCategories.All && MinPages <= 0 && MaxPages <= 0 &&
        !SearchDescription && !OnlyTorrents && Language.Length == 0;

    public string EffectiveQuery
    {
        get
        {
            string query = Query.Trim();
            if (Language.Length > 0)
            {
                string tag = "language:" + Language;
                query = query.Length == 0 ? tag : query + " " + tag;
            }

            return query;
        }
    }

    public EhSearchRequest Clone() => new()
    {
        Query = Query,
        CategoryMask = CategoryMask,
        MinPages = MinPages,
        MaxPages = MaxPages,
        SearchName = SearchName,
        SearchTags = SearchTags,
        SearchDescription = SearchDescription,
        OnlyTorrents = OnlyTorrents,
        OnlyExpunged = OnlyExpunged,
        Language = Language,
    };
}

public sealed class EhLoginResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool ExAccess { get; init; }
    public bool ExAccessKnown { get; init; }
}

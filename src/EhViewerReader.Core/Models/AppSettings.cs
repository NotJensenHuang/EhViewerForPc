namespace EhViewerReader.Core.Models;

public enum AppTheme
{
    System = 0,
    Dark = 1,
    Light = 2,
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

public enum LibraryFilter
{
    All = 0,
    Reading = 1,
    Unread = 2,
    Completed = 3,
    Favorite = 4,
    Incomplete = 5,
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;

    /// <summary>Legacy single-root field; migrated into <see cref="Sources"/> on load.</summary>
    public string? LibraryRoot { get; set; }

    public List<LibrarySource> Sources { get; set; } = [];

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
    public int PageCacheMegabytes { get; set; } = 256;
    public int CoverDecodeWidth { get; set; } = 360;
    public LibrarySort Sort { get; set; } = LibrarySort.RecentlyRead;
    public LibraryFilter Filter { get; set; } = LibraryFilter.All;
    public string? LanguageFilter { get; set; }
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 700;
    public bool WindowMaximized { get; set; }
    public string? LastOpenedStableId { get; set; }
    public bool ShowChromeInReader { get; set; } = true;
    public string? LastScanCompletedUtc { get; set; }

    public AppSettings Normalize()
    {
        MaxDecodeMegapixels = Math.Clamp(MaxDecodeMegapixels, 4, 96);
        MaxDecodeDimension = Math.Clamp(MaxDecodeDimension, 2048, 16384);
        PageCacheMegabytes = Math.Clamp(PageCacheMegabytes, 64, 1536);
        CoverDecodeWidth = Math.Clamp(CoverDecodeWidth, 160, 1024);
        WindowWidth = Math.Clamp(WindowWidth, 960, 10000);
        WindowHeight = Math.Clamp(WindowHeight, 640, 10000);
        if (!Enum.IsDefined(Theme)) Theme = AppTheme.Dark;
        if (!Enum.IsDefined(DefaultReadingMode)) DefaultReadingMode = ReadingMode.Single;
        if (!Enum.IsDefined(DefaultFitMode)) DefaultFitMode = FitMode.FitPage;
        if (!Enum.IsDefined(ReaderBackground)) ReaderBackground = ReaderBackground.Black;
        if (!Enum.IsDefined(Sort)) Sort = LibrarySort.RecentlyRead;
        if (!Enum.IsDefined(Filter)) Filter = LibraryFilter.All;
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

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            string.Equals(s.RootPath, full, StringComparison.OrdinalIgnoreCase));
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

    public IReadOnlyList<LibrarySource> ActiveSources =>
        Sources.Where(s => s.Enabled).ToList();
}

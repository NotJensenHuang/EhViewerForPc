namespace EhViewerForPc.Core.Models;

public sealed class LibraryCache
{
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string? RootPath { get; set; }
    public DateTime SavedUtc { get; set; }
    public Dictionary<string, GalleryCacheEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class GalleryCacheEntry
{
    public required string FolderName { get; set; }
    public long FolderMtimeTicks { get; set; }
    public long EhviewerLength { get; set; }
    public long EhviewerMtimeTicks { get; set; }
    public long ThumbLength { get; set; }
    public long ThumbMtimeTicks { get; set; }
    public int PageFileCount { get; set; }
    public long PageLengthSum { get; set; }
    public long PageMtimeMaxTicks { get; set; }
    public long? DirectoryGid { get; set; }
    public long? HeaderGid { get; set; }
    public string Token { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public int MaxPageNumber { get; set; }
    public int? ExpectedPages { get; set; }
    public bool HeaderPageCountOutdated { get; set; }
    public bool HasGidConflict { get; set; }
    public List<PageRange> MissingRanges { get; set; } = [];
    public bool HasEhviewer { get; set; }
    public bool HasThumb { get; set; }
    public string? CoverPath { get; set; }
    public string? CoverKind { get; set; }
    public string? CoverSource { get; set; }
    public string[] Languages { get; set; } = [];
    public string[] Tags { get; set; } = [];
    public string ExtensionSummary { get; set; } = string.Empty;
    public int ZeroByteFiles { get; set; }
    public string? HeaderError { get; set; }

    public GalleryCacheEntry Clone() => (GalleryCacheEntry)MemberwiseClone();
}

public sealed class ScanProgress
{
    public string Phase { get; init; } = string.Empty;
    public int Processed { get; init; }
    public int Total { get; init; }
    public string? CurrentItem { get; init; }
    public bool FromCache { get; init; }
}

public sealed class ScanResult
{
    public required string RootPath { get; init; }
    public required IReadOnlyList<GalleryInfo> Galleries { get; init; }
    public int IgnoredDirectoryCount { get; init; }
    public int CacheHitCount { get; init; }
    public int ScannedDirectoryCount { get; init; }
    public TimeSpan Elapsed { get; init; }
    public required LibraryCache Cache { get; init; }
}

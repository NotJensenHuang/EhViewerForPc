namespace EhViewerForPc.Core.Models;

public sealed class GalleryInfo
{
    /// <summary>Per-copy identity (unique inside one source, qualified when several sources are active).</summary>
    public required string StableId { get; set; }

    /// <summary>
    /// Cross-source identity of the same work: the gallery id when the folder
    /// carries one, otherwise a hash of the folder path. Reading progress and
    /// favourites are stored against this key so a work keeps its state when the
    /// user switches to another copy.
    /// </summary>
    public string WorkId { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;

    public long? Gid { get; set; }
    public string Token { get; set; } = string.Empty;
    public long? DirectoryGid { get; set; }
    public long? HeaderGid { get; set; }
    public required string Title { get; set; }
    public required string FolderName { get; set; }
    public required string FolderPath { get; set; }
    public int PageCount { get; set; }
    public int MaxPageNumber { get; set; }
    public int? ExpectedPages { get; set; }
    public bool IsPageCountKnown => ExpectedPages.HasValue;
    public IReadOnlyList<PageRange> MissingRanges { get; set; } = [];
    public bool HasMissingPages => MissingRanges.Count > 0;
    public bool HasHeaderPageCountOutdated { get; set; }
    public bool IsDuplicateGid { get; set; }
    public bool HasGidConflict { get; set; }
    public bool IsIncomplete { get; set; }
    public bool HasEhviewer { get; set; }
    public bool HasThumb { get; set; }
    public bool IsEmpty => PageCount == 0;
    public string? CoverPath { get; set; }
    public string? CoverKind { get; set; }
    public string[] Languages { get; set; } = [];
    public string[] Tags { get; set; } = [];
    public DateTime LastWriteUtc { get; set; }
    public string ExtensionSummary { get; set; } = string.Empty;
    public int ZeroByteFiles { get; set; }
    public string? HeaderError { get; set; }
    public string? CoverSource { get; set; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? FolderName : Title;
}

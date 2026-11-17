namespace EhViewerForPc.Core.Models;

public enum PageState
{
    Ok = 0,
    Empty = 1,
    Corrupt = 2,
    Unreadable = 3,
}

public sealed class PageCandidate
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string Extension { get; init; }
    public long Length { get; init; }
    public long MtimeTicks { get; init; }

    public override string ToString() => $"{FileName} ({Length} bytes)";
}

public sealed class GalleryPage
{
    public int Number { get; init; }
    public int DisplayIndex { get; init; }
    public IReadOnlyList<PageCandidate> Candidates { get; init; } = [];
    public PageState State { get; set; } = PageState.Ok;

    public PageCandidate? Primary => Candidates.Count == 0 ? null : Candidates[0];

    public bool IsReadableCandidate => Candidates.Any(c => c.Length > 0);

    /// <summary>Page is present but no candidate parses as a complete image.</summary>
    public bool IsCorrupt => State == PageState.Corrupt;
}

public readonly record struct PageRange(int Start, int End)
{
    public int Count => End - Start + 1;

    public override string ToString() => Start == End ? Start.ToString() : $"{Start}-{End}";
}

public sealed class PageScanResult
{
    public bool HasNaturalPageNames { get; init; }
    public IReadOnlyList<GalleryPage> Pages { get; init; } = [];
    public IReadOnlyList<PageRange> MissingRanges { get; init; } = [];
    public int ExpectedPages { get; init; }
    public bool HasExpectedPages { get; init; }
    public int MaxPageNumber { get; init; }
    public int MinPageNumber { get; init; }
    public string ExtensionSummary { get; init; } = string.Empty;
    public int ZeroByteFiles { get; init; }

    /// <summary>Pages whose files exist but whose container check failed. Only
    /// filled in when the scan ran with container validation requested.</summary>
    public int CorruptPages { get; init; }

    public bool HeaderPageCountOutdated { get; init; }

    public int PageCount => Pages.Count;
    public bool HasMissingPages => MissingRanges.Count > 0;
    public bool HasCorruptPages => CorruptPages > 0;
    public bool HasZeroByteFiles => ZeroByteFiles > 0;
    /// <summary>需要补页或替换损坏页。损坏页不改变页数，但同样不可读。</summary>
    public bool IsIncomplete => PageCount == 0 || HasMissingPages || HasCorruptPages;
}

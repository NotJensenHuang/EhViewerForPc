namespace EhViewerReader.Core.Models;

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
}

public readonly record struct PageRange(int Start, int End)
{
    public int Count => End - Start + 1;

    public override string ToString() => Start == End ? Start.ToString() : $"{Start}-{End}";
}

public sealed class PageScanResult
{
    public IReadOnlyList<GalleryPage> Pages { get; init; } = [];
    public IReadOnlyList<PageRange> MissingRanges { get; init; } = [];
    public int ExpectedPages { get; init; }
    public bool HasExpectedPages { get; init; }
    public int MaxPageNumber { get; init; }
    public int MinPageNumber { get; init; }
    public string ExtensionSummary { get; init; } = string.Empty;
    public int ZeroByteFiles { get; init; }
    public bool HeaderPageCountOutdated { get; init; }

    public int PageCount => Pages.Count;
    public bool HasMissingPages => MissingRanges.Count > 0;
    public bool HasZeroByteFiles => ZeroByteFiles > 0;
    public bool IsIncomplete => PageCount == 0 || HasMissingPages || HeaderPageCountOutdated || HasZeroByteFiles;
}

namespace EhViewerForPc.Services;

// 像素坐标对应应用方向校正后的输入，区块已经按阅读顺序排列。
internal sealed record MangaOcrRegion(int X, int Y, int Width, int Height, string Text)
{
    public int[] SourceRegionIds { get; init; } = [];
}

internal readonly record struct MangaOcrPoint(float X, float Y);

internal sealed record MangaOcrDetection(int Id, MangaOcrPoint[] Polygon, float Confidence);

internal enum MangaOcrDirection { Horizontal, Vertical, Inclined }
internal enum MangaOcrRegionType { Unclassified }

internal sealed record MangaOcrObservation(int Id, MangaOcrPoint[] Polygon, float DetectionConfidence,
    string OriginalText, float OriginalConfidence, string? CorrectedText, string FinalText, float RecognitionConfidence,
    MangaOcrDirection Direction, MangaOcrRegionType Type, bool RequiresReview)
{
    public int[] OriginalDetectionIds { get; init; } = [];
}

internal sealed record MangaOcrResult(int Width, int Height, IReadOnlyList<MangaOcrRegion> Regions, string Provider)
{
    public MangaOcrDetection[] RawDetections { get; init; } = [];
    public MangaOcrObservation[] Observations { get; init; } = [];
}

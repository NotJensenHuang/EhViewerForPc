namespace EhViewerForPc.Services;

// 像素坐标对应应用方向校正后的输入，区块已经按阅读顺序排列。
internal sealed record MangaOcrRegion(int X, int Y, int Width, int Height, string Text);

internal sealed record MangaOcrResult(int Width, int Height, IReadOnlyList<MangaOcrRegion> Regions, string Provider);

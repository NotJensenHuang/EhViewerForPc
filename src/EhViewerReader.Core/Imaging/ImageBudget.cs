namespace EhViewerReader.Core.Imaging;

/// <summary>
/// Computes a safe decode size for a source image. The rules are shared by the
/// reader and by the self-test so the documented pixel budget cannot drift.
/// </summary>
public static class ImageBudget
{
    public const int DefaultMaxMegapixels = 24;
    public const int DefaultMaxDimension = 16000;

    /// <summary>
    /// Scale = min(1, targetWidth/originalWidth, targetHeight/originalHeight,
    /// sqrt(maxPixels/(w*h)), maxDimension/max(w,h)). Never upscales.
    /// </summary>
    public static (int Width, int Height) Compute(
        int originalWidth,
        int originalHeight,
        int? targetWidth = null,
        int? targetHeight = null,
        int maxMegapixels = DefaultMaxMegapixels,
        int maxDimension = DefaultMaxDimension)
    {
        if (originalWidth <= 0 || originalHeight <= 0)
        {
            return (1, 1);
        }

        double scale = 1.0;
        if (targetWidth is > 0)
        {
            scale = Math.Min(scale, targetWidth.Value / (double)originalWidth);
        }

        if (targetHeight is > 0)
        {
            scale = Math.Min(scale, targetHeight.Value / (double)originalHeight);
        }

        double pixelBudget = Math.Max(1, maxMegapixels) * 1_000_000d;
        scale = Math.Min(scale, Math.Sqrt(pixelBudget / (originalWidth * (double)originalHeight)));

        int maxSide = Math.Max(originalWidth, originalHeight);
        if (maxDimension > 0 && maxSide > maxDimension)
        {
            scale = Math.Min(scale, maxDimension / (double)maxSide);
        }

        scale = Math.Clamp(scale, 0, 1);
        int width = Math.Max(1, (int)Math.Round(originalWidth * scale));
        int height = Math.Max(1, (int)Math.Round(originalHeight * scale));
        return (width, height);
    }

}

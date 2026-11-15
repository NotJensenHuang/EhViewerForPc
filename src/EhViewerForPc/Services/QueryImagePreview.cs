using SkiaSharp;

namespace EhViewerForPc.Services;

/// <summary>查询预览保留颜色和 EXIF 方向；原始输入及灰度检索由调用者另行持有。</summary>
internal static class QueryImagePreview
{
    internal static (byte[] Png, int Width, int Height) Prepare(byte[] encoded)
    {
        if (encoded.Length is < 1 or > 50 * 1024 * 1024) throw new InvalidDataException("图片为空或超过 50 MiB。");
        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("无法识别图片格式。");
        int width = codec.Info.Width, height = codec.Info.Height;
        if (width is < 1 or > 16000 || height is < 1 or > 16000 || (long)width * height > 24_000_000 ||
            2L * encoded.Length + 5L * width * height + 4L * 2048 * 2048 > 128L * 1024 * 1024)
            throw new InvalidDataException("图片尺寸超出解码预算。");
        using var source = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (codec.GetPixels(source.Info, source.GetPixels(), new SKCodecOptions(0)) != SKCodecResult.Success)
            throw new InvalidDataException("图片像素损坏或不完整。");
        bool swap = ImageDecoder.SwapsDimensions(codec.EncodedOrigin);
        int orientedWidth = swap ? height : width, orientedHeight = swap ? width : height;
        double scale = Math.Min(1, 2048d / Math.Max(orientedWidth, orientedHeight));
        int previewWidth = Math.Max(1, (int)Math.Round(orientedWidth * scale));
        int previewHeight = Math.Max(1, (int)Math.Round(orientedHeight * scale));
        using var surface = SKSurface.Create(new SKImageInfo(previewWidth, previewHeight, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidDataException("无法分配图片预览。");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var matrix = SKMatrix.Concat(SKMatrix.CreateScale((float)scale, (float)scale),
            ImageDecoder.CreateOrientationMatrix(codec.EncodedOrigin, width, height));
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0);
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return (png.ToArray(), orientedWidth, orientedHeight);
    }
}

using System.Numerics;

namespace EhViewerForPc.Services;

internal static partial class MangaOcr
{
    private readonly record struct Bgr(float B, float G, float R)
    {
        internal int Gray => (114 * ToByte(B) + 587 * ToByte(G) + 299 * ToByte(R) + 500) / 1000;

        internal static Bgr Lerp(Bgr first, Bgr second, float t) =>
            new(first.B + (second.B - first.B) * t, first.G + (second.G - first.G) * t, first.R + (second.R - first.R) * t);
    }

    private readonly ref struct ImagePixels
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private readonly int _rowBytes;
        private readonly int _channels;
        internal int Width { get; }
        internal int Height { get; }

        internal ImagePixels(ReadOnlySpan<byte> bytes, int width, int height, int rowBytes, int channels)
        {
            _bytes = bytes;
            _rowBytes = rowBytes;
            _channels = channels;
            Width = width;
            Height = height;
        }

        internal Bgr Get(int x, int y)
        {
            int offset = y * _rowBytes + x * _channels;
            int white = _channels == 4 ? 255 - _bytes[offset + 3] : 0;
            // BGRA 页面用预乘 alpha，合成到白底后等价于 OCR PNG 路径的白色画纸。
            return new(_bytes[offset] + white, _bytes[offset + 1] + white, _bytes[offset + 2] + white);
        }

        internal Bgr Sample(float x, float y)
        {
            x = Math.Clamp(x, 0, Width - 1);
            y = Math.Clamp(y, 0, Height - 1);
            int x0 = (int)x;
            int y0 = (int)y;
            int x1 = Math.Min(x0 + 1, Width - 1);
            int y1 = Math.Min(y0 + 1, Height - 1);
            return Bgr.Lerp(Bgr.Lerp(Get(x0, y0), Get(x1, y0), x - x0),
                Bgr.Lerp(Get(x0, y1), Get(x1, y1), x - x0), y - y0);
        }

        internal Bgr Average(float left, float top, float right, float bottom)
        {
            float b = 0;
            float g = 0;
            float r = 0;
            for (int y = (int)top; y < Math.Min(Height, bottom); y++)
            {
                float wy = Math.Min(y + 1, bottom) - Math.Max(y, top);
                for (int x = (int)left; x < Math.Min(Width, right); x++)
                {
                    float weight = wy * (Math.Min(x + 1, right) - Math.Max(x, left));
                    Bgr pixel = Get(x, y);
                    b += pixel.B * weight;
                    g += pixel.G * weight;
                    r += pixel.R * weight;
                }
            }

            float area = (right - left) * (bottom - top);
            return new(b / area, g / area, r / area);
        }
    }

    private sealed record Crop(byte[] Data, int Width, int Height)
    {
        internal ImagePixels Pixels => new(Data, Width, Height, Width * 3, 3);
    }

    private static byte ToByte(float value) => (byte)Math.Clamp(MathF.Round(value), 0, 255);

    private static void WritePixel(byte[] destination, int offset, Bgr pixel)
    {
        destination[offset] = ToByte(pixel.B);
        destination[offset + 1] = ToByte(pixel.G);
        destination[offset + 2] = ToByte(pixel.R);
    }

    private static float[] PrepareDetection(ImagePixels image, int width, int height, CancellationToken token)
    {
        int plane = width * height;
        float[] data = new float[3 * plane];
        float xScale = (float)image.Width / width;
        float yScale = (float)image.Height / height;
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                Bgr pixel = image.Average(x * xScale, y * yScale, (x + 1) * xScale, (y + 1) * yScale);
                int index = y * width + x;
                data[index] = (ToByte(pixel.B) / 255f - 0.485f) / 0.229f;
                data[plane + index] = (ToByte(pixel.G) / 255f - 0.456f) / 0.224f;
                data[2 * plane + index] = (ToByte(pixel.R) / 255f - 0.406f) / 0.225f;
            }
        }
        return data;
    }

    private static float[] PrepareRecognition(Crop crop, int contentWidth, int inputWidth, CancellationToken token)
    {
        const int height = RecognitionHeight;
        int plane = inputWidth * height;
        float[] data = new float[plane * 3];
        ImagePixels pixels = crop.Pixels;
        float xScale = (float)crop.Width / contentWidth;
        float yScale = (float)crop.Height / height;
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < contentWidth; x++)
            {
                Bgr pixel = pixels.Sample((x + 0.5f) * xScale - 0.5f, (y + 0.5f) * yScale - 0.5f);
                int index = y * inputWidth + x;
                data[index] = ToByte(pixel.B) / 127.5f - 1f;
                data[plane + index] = ToByte(pixel.G) / 127.5f - 1f;
                data[2 * plane + index] = ToByte(pixel.R) / 127.5f - 1f;
            }
        }
        return data;
    }

    private static Crop CropRegion(ImagePixels image, Quad box, CancellationToken token)
    {
        Quad ordered = box.FromTopLeft();
        float width = Math.Max(Vector2.Distance(ordered.A, ordered.B), Vector2.Distance(ordered.C, ordered.D));
        float height = Math.Max(Vector2.Distance(ordered.A, ordered.D), Vector2.Distance(ordered.B, ordered.C));
        float scale = Math.Min(1, (float)MaxDetectionDimension / Math.Max(width, height));
        int outputWidth = Math.Max(2, (int)MathF.Round(width * scale));
        int outputHeight = Math.Max(2, (int)MathF.Round(height * scale));
        var crop = new Crop(new byte[3 * outputWidth * outputHeight], outputWidth, outputHeight);

        Vector2 fromRight = ordered.B - ordered.C;
        Vector2 fromLeft = ordered.D - ordered.C;
        Vector2 distortion = ordered.A - ordered.B + ordered.C - ordered.D;
        float determinant = Cross(fromRight, fromLeft);
        float g = 0;
        float h = 0;
        if (Math.Abs(determinant) > 0.0001f)
        {
            g = Cross(distortion, fromLeft) / determinant;
            h = Cross(fromRight, distortion) / determinant;
        }
        Vector2 horizontal = ordered.B * (1 + g) - ordered.A;
        Vector2 vertical = ordered.D * (1 + h) - ordered.A;
        for (int y = 0; y < outputHeight; y++)
        {
            token.ThrowIfCancellationRequested();
            float v = (float)y / (outputHeight - 1);
            for (int x = 0; x < outputWidth; x++)
            {
                float u = (float)x / (outputWidth - 1);
                float divisor = 1 + g * u + h * v;
                Vector2 point = Math.Abs(divisor) > 0.0001f
                    ? (ordered.A + horizontal * u + vertical * v) / divisor
                    : Vector2.Lerp(Vector2.Lerp(ordered.A, ordered.B, u), Vector2.Lerp(ordered.D, ordered.C, u), v);
                WritePixel(crop.Data, (y * outputWidth + x) * 3, image.Sample(point.X, point.Y));
            }
        }
        return crop;
    }

    private static Crop RotateVertical(Crop crop, bool clockwise)
    {
        int width = crop.Height;
        int height = crop.Width;
        byte[] rotated = new byte[crop.Data.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sourceX = clockwise ? y : height - 1 - y;
                int sourceY = clockwise ? width - 1 - x : x;
                crop.Data.AsSpan((sourceY * crop.Width + sourceX) * 3, 3).CopyTo(rotated.AsSpan((y * width + x) * 3, 3));
            }
        }
        return new Crop(rotated, width, height);
    }

    private static Crop? LayOutVerticalGlyphs(Crop crop)
    {
        ImagePixels pixels = crop.Pixels;
        Span<int> histogram = stackalloc int[256];
        Span<int> edgeHistogram = stackalloc int[256];
        histogram.Clear();
        edgeHistogram.Clear();
        for (int y = 0; y < crop.Height; y++)
        {
            edgeHistogram[pixels.Get(0, y).Gray]++;
            edgeHistogram[pixels.Get(crop.Width - 1, y).Gray]++;
            for (int x = 0; x < crop.Width; x++) histogram[pixels.Get(x, y).Gray]++;
        }

        int edgeTotal = 0;
        for (int i = 0; i <= 127; i++) edgeTotal += edgeHistogram[i];
        bool brightBackground = edgeTotal < crop.Height ||
            (edgeTotal == crop.Height && MiddleGray(edgeHistogram, crop.Height - 1) + MiddleGray(edgeHistogram, crop.Height) > 254);
        int threshold = Otsu(histogram, crop.Width * crop.Height);
        int startX = crop.Width / 8;
        int endX = Math.Max(startX + 1, crop.Width * 7 / 8);
        int minInk = Math.Max(1, crop.Width / 20);
        int minGap = Math.Max(1, crop.Width / 10);
        Span<(int Start, int End)> groups = stackalloc (int, int)[32];
        int groupCount = 0;
        int previous = -1;
        for (int y = 0; y < crop.Height; y++)
        {
            int ink = 0;
            for (int x = startX; x < endX; x++)
                if ((pixels.Get(x, y).Gray <= threshold) == brightBackground) ink++;
            if (ink < minInk) continue;
            if (previous < 0 || y - previous > minGap)
            {
                if (groupCount == groups.Length) return null;
                groups[groupCount++] = (y, y);
            }
            else groups[groupCount - 1].End = y;
            previous = y;
        }
        if (groupCount < 2 || groupCount > crop.Height * 2.0 / crop.Width) return null;

        int cellHeight = crop.Width;
        foreach ((int start, int end) in groups[..groupCount])
        {
            if (end - start > crop.Width * 1.3) return null;
            cellHeight = Math.Max(cellHeight, end - start + 3);
        }
        int outputWidth = crop.Width * groupCount;
        byte[] target = new byte[outputWidth * cellHeight * 3];
        if (brightBackground) Array.Fill(target, (byte)255);
        int rowBytes = crop.Width * 3;
        for (int i = 0; i < groupCount; i++)
        {
            int start = Math.Max(0, groups[i].Start - 1);
            int end = Math.Min(crop.Height, groups[i].End + 2);
            int top = (cellHeight - (end - start)) / 2;
            for (int y = start; y < end; y++)
            {
                int offset = ((y - start + top) * outputWidth + i * crop.Width) * 3;
                crop.Data.AsSpan(y * rowBytes, rowBytes).CopyTo(target.AsSpan(offset, rowBytes));
            }
        }
        return new Crop(target, outputWidth, cellHeight);
    }

    private static int MiddleGray(ReadOnlySpan<int> histogram, int rank)
    {
        for (int i = 0; i < histogram.Length; i++)
        {
            rank -= histogram[i];
            if (rank < 0) return i;
        }
        return 255;
    }

    private static int Otsu(ReadOnlySpan<int> histogram, int total)
    {
        long grayTotal = 0;
        for (int i = 0; i < histogram.Length; i++) grayTotal += (long)i * histogram[i];
        int background = 0;
        long backgroundGray = 0;
        double largestVariance = 0;
        int threshold = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            background += histogram[i];
            int foreground = total - background;
            if (foreground == 0) break;
            if (background == 0) continue;
            backgroundGray += (long)i * histogram[i];
            double difference = (double)backgroundGray / background - (double)(grayTotal - backgroundGray) / foreground;
            double variance = (double)background * foreground * difference * difference;
            if (variance > largestVariance)
            {
                largestVariance = variance;
                threshold = i;
            }
        }
        return threshold;
    }

    private static Crop? CleanBrightVerticalGlyphs(Crop crop)
    {
        int margin = (int)Math.Round(crop.Width * 0.11);
        int width = crop.Width - 2 * margin;
        ImagePixels pixels = crop.Pixels;
        int bright = 0;
        byte[] clean = new byte[width * crop.Height * 3];
        for (int y = 0; y < crop.Height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int gray = pixels.Get(x + margin, y).Gray;
                if (gray >= 200) bright++;
                clean.AsSpan((y * width + x) * 3, 3).Fill(gray > 110 ? (byte)255 : (byte)0);
            }
        }
        return bright >= width * crop.Height * 0.60 ? LayOutVerticalGlyphs(new Crop(clean, width, crop.Height)) : null;
    }
}

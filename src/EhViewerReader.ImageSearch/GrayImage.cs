using System.Runtime.InteropServices;
using SkiaSharp;

namespace EhViewerReader.ImageSearch;

/// <summary>Chooses a resizing kernel supported by the original backend.</summary>
public enum ImageInterpolation
{
    /// <summary>Integrates source pixel areas while shrinking.</summary>
    Area,
    /// <summary>Interpolates the nearest two samples per axis.</summary>
    Linear,
    /// <summary>Interpolates four samples per axis with a cubic kernel.</summary>
    Cubic,
    /// <summary>Interpolates eight samples per axis with a windowed sinc kernel.</summary>
    Lanczos4
}

/// <summary>Owns a tightly packed eight-bit grayscale image.</summary>
public sealed class GrayImage
{
    /// <summary>Initializes an image from owned pixels.</summary>
    public GrayImage(int width, int height, byte[] pixels)
    {
        if (width < 1 || height < 1 || (long)width * height != pixels.Length) throw new ArgumentException("Invalid grayscale dimensions.");
        Width = width; Height = height; Pixels = pixels;
    }
    /// <summary>Gets the image width.</summary>
    public int Width { get; }
    /// <summary>Gets the image height.</summary>
    public int Height { get; }
    /// <summary>Gets the owned row-major grayscale pixels.</summary>
    public byte[] Pixels { get; }

    /// <summary>Decodes a bounded first frame, applies EXIF orientation and an optional crop, and normalizes its size.</summary>
    public static GrayImage Decode(byte[] encoded, ImageCrop? crop = null)
    {
        if (encoded.Length is < 1 or > 50 * 1024 * 1024) throw new InvalidDataException("InputTooLarge or empty image.");
        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("UnsupportedImage.");
        int width = codec.Info.Width, height = codec.Info.Height;
        if (width is < 1 or > 16000 || height is < 1 or > 16000 || (long)width * height > 24_000_000)
            throw new InvalidDataException("InputTooLarge: decoded dimensions exceed the pixel budget.");
        // Account for encoded bytes, Skia's input copy, RGBA pixels, grayscale and normalized output.
        if (2L * encoded.Length + 5L * width * height + 4L * 2048 * 2048 > 128L * 1024 * 1024)
            throw new InvalidDataException("InputTooLarge: normalization exceeds the 128 MiB memory budget.");
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels(), new SKCodecOptions(0)) != SKCodecResult.Success)
            throw new InvalidDataException("UnsupportedImage: incomplete or corrupt pixels.");
        int origin = (int)codec.EncodedOrigin;
        bool swap = origin >= 5;
        int orientedWidth = swap ? height : width, orientedHeight = swap ? width : height;
        var rectangle = crop ?? new ImageCrop(0, 0, orientedWidth, orientedHeight);
        if (rectangle.X < 0 || rectangle.Y < 0 || rectangle.Width < 1 || rectangle.Height < 1
            || (long)rectangle.X + rectangle.Width > orientedWidth || (long)rectangle.Y + rectangle.Height > orientedHeight)
            throw new ArgumentOutOfRangeException(nameof(crop), "InvalidCrop.");
        byte[] gray = new byte[checked(rectangle.Width * rectangle.Height)];
        ReadOnlySpan<byte> rgba = bitmap.GetPixelSpan();
        for (int y = 0; y < rectangle.Height; y++)
        for (int x = 0; x < rectangle.Width; x++)
        {
            int ox = x + rectangle.X, oy = y + rectangle.Y;
            (int sx, int sy) = origin switch
            {
                2 => (width - 1 - ox, oy), 3 => (width - 1 - ox, height - 1 - oy),
                4 => (ox, height - 1 - oy), 5 => (oy, ox), 6 => (oy, height - 1 - ox),
                7 => (width - 1 - oy, height - 1 - ox), 8 => (width - 1 - oy, ox), _ => (ox, oy)
            };
            int i = sy * bitmap.RowBytes + sx * 4;
            int alpha = rgba[i + 3];
            double value = .299 * rgba[i] + .587 * rgba[i + 1] + .114 * rgba[i + 2];
            gray[y * rectangle.Width + x] = (byte)Math.Clamp((int)Math.Round((value * alpha + 255 * (255 - alpha)) / 255, MidpointRounding.AwayFromZero), 0, 255);
        }
        var image = new GrayImage(rectangle.Width, rectangle.Height, gray);
        double scale = 2048.0 / Math.Max(image.Width, image.Height);
        return scale >= 1 ? image : image.Resize(Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale)));
    }

    /// <summary>Reads an image without changing its source, and rejects a concurrent replacement.</summary>
    public static GrayImage ReadFile(string path, ImageCrop? crop = null)
    {
        var before = new FileInfo(path);
        long length = before.Length, stamp = before.LastWriteTimeUtc.Ticks;
        if (length > 50 * 1024 * 1024) throw new InvalidDataException("InputTooLarge.");
        byte[] bytes;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (file.Length != length) throw new IOException("SourceChanging.");
            bytes = new byte[checked((int)length)]; file.ReadExactly(bytes);
        }
        var image = Decode(bytes, crop);
        before.Refresh();
        if (!before.Exists || before.Length != length || before.LastWriteTimeUtc.Ticks != stamp) throw new IOException("SourceChanging.");
        return image;
    }

    /// <summary>Resizes with area integration when shrinking and bilinear interpolation when enlarging.</summary>
    public GrayImage Resize(int width, int height, ImageInterpolation interpolation = ImageInterpolation.Area)
    {
        if (width == Width && height == Height) return this;
        if (width < 1 || height < 1 || (long)width * height > 24_000_000) throw new ArgumentOutOfRangeException(nameof(width));
        byte[] pixels = new byte[width * height];
        double dx = (double)Width / width, dy = (double)Height / height;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (interpolation == ImageInterpolation.Area && dx >= 1 && dy >= 1)
            {
                double x0 = x * dx, x1 = (x + 1) * dx, y0 = y * dy, y1 = (y + 1) * dy, sum = 0;
                for (int sy = (int)y0; sy < Math.Ceiling(y1); sy++)
                for (int sx = (int)x0; sx < Math.Ceiling(x1); sx++)
                    sum += Pixels[Math.Min(sy, Height - 1) * Width + Math.Min(sx, Width - 1)]
                        * (Math.Min(x1, sx + 1) - Math.Max(x0, sx)) * (Math.Min(y1, sy + 1) - Math.Max(y0, sy));
                pixels[y * width + x] = (byte)Math.Clamp((int)Math.Round(sum / (dx * dy)), 0, 255);
            }
            else if (interpolation is ImageInterpolation.Area or ImageInterpolation.Linear)
            {
                double fx = Math.Clamp((x + .5) * dx - .5, 0, Width - 1), fy = Math.Clamp((y + .5) * dy - .5, 0, Height - 1);
                int ix = (int)fx, iy = (int)fy; double wx = fx - ix, wy = fy - iy;
                pixels[y * width + x] = (byte)Math.Round((1 - wy) * ((1 - wx) * At(ix, iy) + wx * At(ix + 1, iy))
                    + wy * ((1 - wx) * At(ix, iy + 1) + wx * At(ix + 1, iy + 1)));
            }
            else
            {
                double fx = (x + .5) * dx - .5, fy = (y + .5) * dy - .5, value = 0, weights = 0;
                int radius = interpolation == ImageInterpolation.Cubic ? 2 : 4;
                for (int iy = (int)Math.Floor(fy) - radius + 1; iy <= (int)Math.Floor(fy) + radius; iy++)
                for (int ix = (int)Math.Floor(fx) - radius + 1; ix <= (int)Math.Floor(fx) + radius; ix++)
                {
                    double weight = Kernel(fx - ix, interpolation) * Kernel(fy - iy, interpolation);
                    value += weight * At(ix, iy); weights += weight;
                }
                pixels[y * width + x] = (byte)Math.Clamp((int)Math.Round(value / weights), 0, 255);
            }
        }
        return new(width, height, pixels);
    }

    private static double Kernel(double x, ImageInterpolation interpolation)
    {
        x = Math.Abs(x);
        if (interpolation == ImageInterpolation.Cubic)
        {
            const double a = -.75;
            return x <= 1 ? (a + 2) * x * x * x - (a + 3) * x * x + 1
                : x < 2 ? a * x * x * x - 5 * a * x * x + 8 * a * x - 4 * a : 0;
        }
        if (x < 1e-10) return 1;
        return x >= 4 ? 0 : Math.Sin(Math.PI * x) * Math.Sin(Math.PI * x / 4) / (Math.PI * Math.PI * x * x / 4);
    }

    internal byte At(int x, int y) => Pixels[Math.Clamp(y, 0, Height - 1) * Width + Math.Clamp(x, 0, Width - 1)];

    /// <summary>Encodes deterministic grayscale PNG pixels without metadata.</summary>
    public byte[] EncodePng()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Gray8, SKAlphaType.Opaque));
        for (int y = 0; y < Height; y++) Marshal.Copy(Pixels, y * Width, bitmap.GetPixels() + y * bitmap.RowBytes, Width);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>Computes the upstream horizontal 64-bit difference hash.</summary>
    public byte[] DifferenceHash()
    {
        var small = Resize(9, 8, ImageInterpolation.Linear);
        var hash = new byte[8];
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) hash[y] = (byte)((hash[y] << 1) | (small.Pixels[y * 9 + x] < small.Pixels[y * 9 + x + 1] ? 1 : 0));
        return hash;
    }
}

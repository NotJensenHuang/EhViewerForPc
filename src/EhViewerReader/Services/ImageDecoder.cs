using Avalonia.Media.Imaging;
using Avalonia.Platform;
using EhViewerReader.Core.Imaging;
using SkiaSharp;

namespace EhViewerReader.Services;

public sealed class DecodedImage : IDisposable
{
    public DecodedImage(Bitmap bitmap, int decodedWidth, int decodedHeight, string path)
    {
        Bitmap = bitmap;
        DecodedWidth = decodedWidth;
        DecodedHeight = decodedHeight;
        Path = path;
    }

    public Bitmap Bitmap { get; }
    public int DecodedWidth { get; }
    public int DecodedHeight { get; }
    public string Path { get; }
    public long ApproximateBytes => (long)DecodedWidth * DecodedHeight * 4;

    /// <summary>True when the file carries more than one animation frame.</summary>
    public bool IsAnimated { get; init; }
    public int FrameCount { get; init; }
    public int FrameIndex { get; init; }

    /// <summary>Frame duration in milliseconds as declared by the container.</summary>
    public int FrameDurationMs { get; init; }

    internal ImageDecoder? Owner { get; init; }
    internal int TargetWidth { get; init; }
    internal int TargetHeight { get; init; }
    internal int MaxMegapixels { get; init; }
    internal int MaxDimension { get; init; }

    /// <summary>
    /// Decodes the next animation frame with the same scaling parameters as the
    /// original decode. Returns null for static images or on decode failure;
    /// callers keep showing the current frame in that case.
    /// </summary>
    public Task<DecodedImage?> DecodeNextFrameAsync(CancellationToken cancellationToken)
    {
        if (!IsAnimated || Owner is null || FrameCount < 2) return Task.FromResult<DecodedImage?>(null);
        int next = (FrameIndex + 1) % FrameCount;
        return Owner.DecodeFrameAsync(
            Path, next, TargetWidth, TargetHeight, MaxMegapixels, MaxDimension, cancellationToken);
    }

    public void Dispose() => Bitmap.Dispose();
}

public sealed record DecodeFailure(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>
/// Decodes supported image formats with SkiaSharp, applies EXIF orientation and
/// produces an Avalonia bitmap without ever exceeding the shared pixel budget.
/// </summary>
public sealed class ImageDecoder : IDisposable
{
    private const long LargeImageThresholdBytes = 32L * 1024 * 1024;
    private readonly LogService _log;
    private readonly SemaphoreSlim _normalSemaphore = new(4, 4);
    private readonly SemaphoreSlim _largeSemaphore = new(1, 1);

    public ImageDecoder(LogService log)
    {
        _log = log;
    }

    /// <param name="path">Image file path.</param>
    /// <param name="targetWidth">Display-oriented target width in physical pixels, or null.</param>
    /// <param name="targetHeight">Display-oriented target height in physical pixels, or null.</param>
    public Task<DecodedImage?> DecodeAsync(
        string path,
        int? targetWidth,
        int? targetHeight,
        int maxMegapixels,
        int maxDimension,
        CancellationToken cancellationToken) =>
        DecodeFrameAsync(path, 0, targetWidth, targetHeight, maxMegapixels, maxDimension, cancellationToken);

    /// <summary>
    /// Decodes one animation frame of the file. Frame 0 uses the standard
    /// decode path; animated files report <see cref="DecodedImage.IsAnimated"/>
    /// so controls can drive playback.
    /// </summary>
    public async Task<DecodedImage?> DecodeFrameAsync(
        string path,
        int frameIndex,
        int? targetWidth,
        int? targetHeight,
        int maxMegapixels,
        int maxDimension,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(
                () => DecodeCore(path, targetWidth, targetHeight, maxMegapixels, maxDimension, cancellationToken, frameIndex),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or PathTooLongException)
        {
            _log.Warn("Decode failed: " + path, ex);
            return null;
        }
    }

    public async Task<ImageDimensionInfo?> ReadDimensionAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run<ImageDimensionInfo?>(
                () =>
                {
                    using FileStream stream = OpenRead(path);
                    using SKCodec? codec = SKCodec.Create(stream, out SKCodecResult result);
                    if (codec is null || result != SKCodecResult.Success) return null;
                    int width = codec.Info.Width;
                    int height = codec.Info.Height;
                    bool swapped = SwapsDimensions(codec.EncodedOrigin);
                    var file = new FileInfo(path);
                    return new ImageDimensionInfo(
                        swapped ? height : width,
                        swapped ? width : height,
                        width,
                        height,
                        (int)codec.EncodedOrigin,
                        file.Length,
                        file.LastWriteTimeUtc.Ticks);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private DecodedImage DecodeCore(
        string path,
        int? targetWidth,
        int? targetHeight,
        int maxMegapixels,
        int maxDimension,
        CancellationToken cancellationToken,
        int frameIndex = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream stream = OpenRead(path);

        using SKCodec? codec = SKCodec.Create(stream, out SKCodecResult createResult);
        if (codec is null || createResult != SKCodecResult.Success)
        {
            throw new InvalidOperationException("Unsupported or corrupt image (" + createResult + ").");
        }

        int storedWidth = codec.Info.Width;
        int storedHeight = codec.Info.Height;
        if (storedWidth <= 0 || storedHeight <= 0)
        {
            throw new InvalidOperationException("Image has empty dimensions.");
        }

        bool animated = codec.FrameCount > 1 && frameIndex >= 0 && frameIndex < codec.FrameCount;
        var options = frameIndex > 0 ? new SKCodecOptions(frameIndex) : SKCodecOptions.Default;

        SKEncodedOrigin origin = codec.EncodedOrigin;
        bool swapped = SwapsDimensions(origin);
        int orientedWidth = swapped ? storedHeight : storedWidth;
        int orientedHeight = swapped ? storedWidth : storedHeight;

        (int finalWidth, int finalHeight) = ImageBudget.Compute(
            orientedWidth,
            orientedHeight,
            targetWidth,
            targetHeight,
            maxMegapixels,
            maxDimension);

        // Ask the codec for its nearest supported scaled decode size. JPEG/WebP
        // support 1/2, 1/4, 1/8; PNG usually returns the native size.
        float desiredScale = Math.Min(
            finalWidth / (float)orientedWidth,
            finalHeight / (float)orientedHeight);
        SKSizeI scaled = codec.GetScaledDimensions(Math.Clamp(desiredScale, 0.01f, 1f));
        int decodeWidth = Math.Clamp(scaled.Width, 1, storedWidth);
        int decodeHeight = Math.Clamp(scaled.Height, 1, storedHeight);

        bool direct = !animated && frameIndex == 0 &&
                      origin == SKEncodedOrigin.TopLeft &&
                      decodeWidth == finalWidth && decodeHeight == finalHeight;
        long estimatedBytes = (long)decodeWidth * decodeHeight * 4 +
                              (direct ? 0 : (long)finalWidth * finalHeight * 4);
        SemaphoreSlim gate = estimatedBytes >= LargeImageThresholdBytes ? _largeSemaphore : _normalSemaphore;
        gate.Wait(cancellationToken);
        try
        {
            var writeable = new WriteableBitmap(
                new Avalonia.PixelSize(finalWidth, finalHeight),
                new Avalonia.Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            try
            {
                if (direct && DecodeIntoFramebuffer(codec, writeable, finalWidth, finalHeight, cancellationToken))
                {
                    return CreateResult(writeable, path, codec, animated, frameIndex,
                        finalWidth, finalHeight, targetWidth, targetHeight, maxMegapixels, maxDimension);
                }

                DecodeScaledAndDraw(
                    codec,
                    writeable,
                    origin,
                    storedWidth,
                    storedHeight,
                    ref decodeWidth,
                    ref decodeHeight,
                    finalWidth,
                    finalHeight,
                    cancellationToken,
                    options);
                return CreateResult(writeable, path, codec, animated, frameIndex,
                    finalWidth, finalHeight, targetWidth, targetHeight, maxMegapixels, maxDimension);
            }
            catch
            {
                writeable.Dispose();
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private DecodedImage CreateResult(
        WriteableBitmap bitmap,
        string path,
        SKCodec codec,
        bool animated,
        int frameIndex,
        int finalWidth,
        int finalHeight,
        int? targetWidth,
        int? targetHeight,
        int maxMegapixels,
        int maxDimension)
    {
        int duration = animated
            ? codec.FrameInfo[Math.Clamp(frameIndex, 0, codec.FrameInfo.Length - 1)].Duration
            : 0;
        return new DecodedImage(bitmap, finalWidth, finalHeight, path)
        {
            IsAnimated = animated,
            FrameCount = animated ? codec.FrameCount : 1,
            FrameIndex = frameIndex,
            FrameDurationMs = duration,
            Owner = this,
            TargetWidth = targetWidth ?? 0,
            TargetHeight = targetHeight ?? 0,
            MaxMegapixels = maxMegapixels,
            MaxDimension = maxDimension,
        };
    }

    private static bool DecodeIntoFramebuffer(
        SKCodec codec,
        WriteableBitmap target,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var framebuffer = target.Lock();
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        SKCodecResult result = codec.GetPixels(info, framebuffer.Address, SKCodecOptions.Default);
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }

        return false;
    }

    private static void DecodeScaledAndDraw(
        SKCodec codec,
        WriteableBitmap target,
        SKEncodedOrigin origin,
        int storedWidth,
        int storedHeight,
        ref int decodeWidth,
        ref int decodeHeight,
        int finalWidth,
        int finalHeight,
        CancellationToken cancellationToken,
        SKCodecOptions options = default)
    {
        using SKBitmap? decoded = DecodeBitmap(
            codec, storedWidth, storedHeight, ref decodeWidth, ref decodeHeight, cancellationToken, options);
        if (decoded is null)
        {
            throw new InvalidOperationException("Pixel decode failed.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var framebuffer = target.Lock();
        var info = new SKImageInfo(finalWidth, finalHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = new SKBitmap();
        if (!surface.InstallPixels(info, framebuffer.Address, framebuffer.RowBytes))
        {
            throw new InvalidOperationException("Unable to wrap the writeable bitmap.");
        }

        using var canvas = new SKCanvas(surface);
        canvas.Clear(SKColors.Transparent);

        bool swapped = SwapsDimensions(origin);
        int orientedDecodeWidth = swapped ? decodeHeight : decodeWidth;
        int orientedDecodeHeight = swapped ? decodeWidth : decodeHeight;
        float scale = Math.Min(
            finalWidth / (float)orientedDecodeWidth,
            finalHeight / (float)orientedDecodeHeight);

        SKMatrix matrix = SKMatrix.CreateScale(scale, scale);
        if (origin != SKEncodedOrigin.TopLeft)
        {
            SKMatrix orientation = CreateOrientationMatrix(origin, decodeWidth, decodeHeight);
            matrix = SKMatrix.Concat(matrix, orientation);
        }

        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(decoded, 0, 0);
        canvas.Flush();
    }

    private static SKBitmap? DecodeBitmap(
        SKCodec codec,
        int storedWidth,
        int storedHeight,
        ref int decodeWidth,
        ref int decodeHeight,
        CancellationToken cancellationToken,
        SKCodecOptions options = default)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new SKImageInfo(decodeWidth, decodeHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            var bitmap = new SKBitmap(info);
            SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels(), options);
            if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
            {
                return bitmap;
            }

            bitmap.Dispose();
            bool scaled = decodeWidth != storedWidth || decodeHeight != storedHeight;
            if (!scaled)
            {
                return null;
            }

            // Codec cannot scale to the requested size (typically PNG); retry native.
            decodeWidth = storedWidth;
            decodeHeight = storedHeight;
        }

        return null;
    }

    private static SKMatrix CreateOrientationMatrix(SKEncodedOrigin origin, int width, int height)
    {
        var matrix = SKMatrix.CreateIdentity();
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                matrix.ScaleX = -1;
                matrix.TransX = width;
                break;
            case SKEncodedOrigin.BottomRight:
                matrix.ScaleX = -1;
                matrix.ScaleY = -1;
                matrix.TransX = width;
                matrix.TransY = height;
                break;
            case SKEncodedOrigin.BottomLeft:
                matrix.ScaleY = -1;
                matrix.TransY = height;
                break;
            case SKEncodedOrigin.LeftTop:
                matrix.ScaleX = 0;
                matrix.SkewX = 1;
                matrix.SkewY = 1;
                matrix.ScaleY = 0;
                break;
            case SKEncodedOrigin.RightTop:
                matrix.ScaleX = 0;
                matrix.SkewX = -1;
                matrix.TransX = height;
                matrix.SkewY = 1;
                matrix.ScaleY = 0;
                break;
            case SKEncodedOrigin.RightBottom:
                matrix.ScaleX = 0;
                matrix.SkewX = -1;
                matrix.TransX = height;
                matrix.SkewY = -1;
                matrix.ScaleY = 0;
                matrix.TransY = width;
                break;
            case SKEncodedOrigin.LeftBottom:
                matrix.ScaleX = 0;
                matrix.SkewX = 1;
                matrix.SkewY = -1;
                matrix.ScaleY = 0;
                matrix.TransY = width;
                break;
        }

        return matrix;
    }

    internal static FileStream OpenRead(string path)
    {
        const FileShare share = FileShare.ReadWrite | FileShare.Delete;
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, share, 64 * 1024, FileOptions.SequentialScan);
        }
        catch (PathTooLongException) when (OperatingSystem.IsWindows())
        {
            string full = Path.GetFullPath(path);
            string extended = full.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + full[2..]
                : @"\\?\" + full;
            return new FileStream(extended, FileMode.Open, FileAccess.Read, share, 64 * 1024, FileOptions.SequentialScan);
        }
    }

    internal static bool SwapsDimensions(SKEncodedOrigin origin) => origin is
        SKEncodedOrigin.LeftTop or
        SKEncodedOrigin.RightTop or
        SKEncodedOrigin.RightBottom or
        SKEncodedOrigin.LeftBottom;

    public void Dispose()
    {
        _normalSemaphore.Dispose();
        _largeSemaphore.Dispose();
    }
}

public readonly record struct ImageDimensionInfo(
    int DisplayWidth,
    int DisplayHeight,
    int StoredWidth,
    int StoredHeight,
    int EncodedOrigin,
    long FileLength,
    long FileMtimeTicks);

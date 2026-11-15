using Avalonia.Media.Imaging;
using Avalonia.Platform;
using EhViewerForPc.Core.Imaging;
using SkiaSharp;

namespace EhViewerForPc.Services;

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
    private bool _disposed;

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

    /// <summary>True when the bitmap is borrowed from <see cref="AnimationFramePool"/>.</summary>
    internal bool IsPooled { get; init; }

    /// <summary>Releases the frame. Pooled frames go back to the pool; everything
    /// else is disposed. Safe to call twice.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (IsPooled) AnimationFramePool.Release(Bitmap);
        else ImageMemoryBudget.Release(Bitmap);
    }
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

    /// <summary>
    /// Opens a sequential frame reader for an animated file. Returns null for
    /// static images and for files Skia cannot open, in which case the caller
    /// keeps using the one-shot <see cref="DecodeFrameAsync"/> path.
    /// </summary>
    public AnimationCursor? TryOpenAnimationCursor(
        string path,
        int? targetWidth,
        int? targetHeight,
        int maxMegapixels,
        int maxDimension)
    {
        FileStream? stream = null;
        SKCodec? codec = null;
        try
        {
            stream = OpenRead(path);
            codec = SKCodec.Create(stream, out SKCodecResult result);
            if (codec is null || result != SKCodecResult.Success || codec.FrameCount < 2)
            {
                codec?.Dispose();
                stream.Dispose();
                return null;
            }

            var cursor = new AnimationCursor(
                this, path, stream, codec, targetWidth, targetHeight, maxMegapixels, maxDimension);
            stream = null;
            codec = null;
            return cursor;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or PathTooLongException)
        {
            _log.Warn("Animation cursor open failed: " + path, ex);
            codec?.Dispose();
            stream?.Dispose();
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

        SKBitmap? scratch = null;
        try
        {
            return DecodeFrameWithCodec(
                codec, path, targetWidth, targetHeight, maxMegapixels, maxDimension, cancellationToken, frameIndex,
                pooledBuffer: false, ref scratch);
        }
        finally
        {
            scratch?.Dispose();
        }
    }

    /// <summary>
    /// Decodes one frame through an already-open codec. Split out so playback can
    /// keep a single codec alive across frames: opening the stream and letting
    /// Skia re-parse the container on every frame is pure overhead, and GIF/WebP
    /// readers are stateful, so a kept codec advances sequentially instead of
    /// seeking on each request. Callers must not use one codec concurrently;
    /// <see cref="AnimationCursor"/> serializes access.
    /// </summary>
    internal DecodedImage DecodeFrameWithCodec(
        SKCodec codec,
        string path,
        int? targetWidth,
        int? targetHeight,
        int maxMegapixels,
        int maxDimension,
        CancellationToken cancellationToken,
        int frameIndex,
        bool pooledBuffer,
        ref SKBitmap? scratch)
    {
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

        // Animation frames come from a single sequential cursor: one session has
        // at most one decode in flight, so the parallel-decode guard would only
        // make playback wait behind page prefetch (the "freezes mid-way" report).
        SemaphoreSlim gate = estimatedBytes >= LargeImageThresholdBytes ? _largeSemaphore : _normalSemaphore;
        bool gated = !pooledBuffer;
        using var scratchBudget = ImageMemoryBudget.Reserve(direct ? 0 : (long)decodeWidth * decodeHeight * 4, scratch: true);
        if (gated) gate.Wait(cancellationToken);
        try
        {
            WriteableBitmap writeable = pooledBuffer
                ? AnimationFramePool.Rent(finalWidth, finalHeight)
                : ImageMemoryBudget.CreateBitmap(finalWidth, finalHeight);

            try
            {
                if (direct && DecodeIntoFramebuffer(codec, writeable, finalWidth, finalHeight, cancellationToken))
                {
                    return CreateResult(writeable, path, codec, animated, frameIndex,
                        finalWidth, finalHeight, targetWidth, targetHeight, maxMegapixels, maxDimension, pooledBuffer);
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
                    options,
                    ref scratch);
                return CreateResult(writeable, path, codec, animated, frameIndex,
                    finalWidth, finalHeight, targetWidth, targetHeight, maxMegapixels, maxDimension, pooledBuffer);
            }
            catch
            {
                if (pooledBuffer) AnimationFramePool.Release(writeable);
                else ImageMemoryBudget.Release(writeable);
                throw;
            }
        }
        finally
        {
            if (gated) gate.Release();
        }
    }

    /// <summary>
    /// Decodes one animation frame into a persistent, already-composed canvas.
    ///
    /// GIF/WebP frames are deltas: without telling Skia which frame is already in
    /// the destination buffer (<see cref="SKCodecOptions.PriorFrame"/>) it has to
    /// rebuild the frame chain from the start of the file for every request. That
    /// is what made long animations collapse - on a real 75-frame GIF the cost
    /// grew from 8 ms at frame 1 to 200 ms at frame 70, so playback stalled
    /// mid-way. Keeping the canvas and passing the prior frame makes each step a
    /// delta decode instead of a full replay.
    /// </summary>
    internal DecodedImage DecodeAnimationFrame(
        SKCodec codec,
        string path,
        int frameIndex,
        int? targetWidth,
        int? targetHeight,
        int maxMegapixels,
        int maxDimension,
        bool pooledBuffer,
        ref SKBitmap? canvas,
        ref int canvasFrame,
        ref IDisposable? canvasBudget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int storedWidth = codec.Info.Width;
        int storedHeight = codec.Info.Height;
        if (storedWidth <= 0 || storedHeight <= 0)
        {
            throw new InvalidOperationException("Image has empty dimensions.");
        }

        SKEncodedOrigin origin = codec.EncodedOrigin;
        bool swapped = SwapsDimensions(origin);
        int orientedWidth = swapped ? storedHeight : storedWidth;
        int orientedHeight = swapped ? storedWidth : storedHeight;

        (int finalWidth, int finalHeight) = ImageBudget.Compute(
            orientedWidth, orientedHeight, targetWidth, targetHeight, maxMegapixels, maxDimension);

        float desiredScale = Math.Min(
            finalWidth / (float)orientedWidth,
            finalHeight / (float)orientedHeight);
        SKSizeI scaled = codec.GetScaledDimensions(Math.Clamp(desiredScale, 0.01f, 1f));
        int decodeWidth = Math.Clamp(scaled.Width, 1, storedWidth);
        int decodeHeight = Math.Clamp(scaled.Height, 1, storedHeight);

        var info = new SKImageInfo(decodeWidth, decodeHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (canvas is null || canvas.Width != decodeWidth || canvas.Height != decodeHeight)
        {
            canvas?.Dispose();
            canvasBudget?.Dispose();
            canvasBudget = ImageMemoryBudget.Reserve((long)decodeWidth * decodeHeight * 4, scratch: true);
            canvas = new SKBitmap(info);
            canvasFrame = -1;
        }

        // Seeking backwards (loop wrap) invalidates the composed base; the frame
        // has to be rebuilt from a clean canvas.
        if (canvasFrame < 0 || frameIndex <= canvasFrame)
        {
            canvas.Erase(SKColors.Transparent);
            canvasFrame = -1;
        }

        SKCodecOptions options = canvasFrame >= 0
            ? new SKCodecOptions(frameIndex, canvasFrame)
            : new SKCodecOptions(frameIndex);

        SKCodecResult result = codec.GetPixels(info, canvas.GetPixels(), options);
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            // The delta could not be applied to what we hold (non-monotonic frame
            // dependency). Rebuild from a clean canvas once.
            canvas.Erase(SKColors.Transparent);
            canvasFrame = -1;
            result = codec.GetPixels(info, canvas.GetPixels(), new SKCodecOptions(frameIndex));
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                throw new InvalidOperationException("Animation frame decode failed (" + result + ").");
            }
        }

        canvasFrame = frameIndex;

        WriteableBitmap writeable = pooledBuffer
            ? AnimationFramePool.Rent(finalWidth, finalHeight)
            : ImageMemoryBudget.CreateBitmap(finalWidth, finalHeight);

        try
        {
            using var framebuffer = writeable.Lock();
            var targetInfo = new SKImageInfo(finalWidth, finalHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = new SKBitmap();
            if (!surface.InstallPixels(targetInfo, framebuffer.Address, framebuffer.RowBytes))
            {
                throw new InvalidOperationException("Unable to wrap the writeable bitmap.");
            }

            using var skCanvas = new SKCanvas(surface);
            skCanvas.Clear(SKColors.Transparent);

            int orientedDecodeWidth = swapped ? decodeHeight : decodeWidth;
            int orientedDecodeHeight = swapped ? decodeWidth : decodeHeight;
            float scale = Math.Min(
                finalWidth / (float)orientedDecodeWidth,
                finalHeight / (float)orientedDecodeHeight);

            SKMatrix matrix = SKMatrix.CreateScale(scale, scale);
            if (origin != SKEncodedOrigin.TopLeft)
            {
                matrix = SKMatrix.Concat(matrix, CreateOrientationMatrix(origin, decodeWidth, decodeHeight));
            }

            skCanvas.SetMatrix(matrix);
            skCanvas.DrawBitmap(canvas, 0, 0);
            skCanvas.Flush();

            return CreateResult(writeable, path, codec, animated: true, frameIndex,
                finalWidth, finalHeight, targetWidth, targetHeight, maxMegapixels, maxDimension, pooledBuffer);
        }
        catch
        {
            if (pooledBuffer) AnimationFramePool.Release(writeable);
            else ImageMemoryBudget.Release(writeable);
            throw;
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
        int maxDimension,
        bool pooledBuffer = false)
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
            IsPooled = pooledBuffer,
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
        SKCodecOptions options,
        ref SKBitmap? scratch)
    {
        SKBitmap? decoded = DecodeBitmap(
            codec, storedWidth, storedHeight, ref decodeWidth, ref decodeHeight, cancellationToken, options, ref scratch);
        if (decoded is null)
        {
            throw new InvalidOperationException("Pixel decode failed.");
        }

        try
        {
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
        finally
        {
            // The scratch buffer is owned by the caller and reused across frames.
            if (!ReferenceEquals(decoded, scratch)) decoded.Dispose();
        }
    }

    private static SKBitmap? DecodeBitmap(
        SKCodec codec,
        int storedWidth,
        int storedHeight,
        ref int decodeWidth,
        ref int decodeHeight,
        CancellationToken cancellationToken,
        SKCodecOptions options,
        ref SKBitmap? scratch)
    {
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new SKImageInfo(decodeWidth, decodeHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            bool reusable = scratch is { } existing &&
                            existing.Width == decodeWidth &&
                            existing.Height == decodeHeight;
            SKBitmap bitmap = reusable ? scratch! : new SKBitmap(info);
            if (reusable) bitmap.Erase(SKColors.Transparent);

            SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels(), options);
            if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
            {
                scratch = bitmap;
                return bitmap;
            }

            if (!reusable) bitmap.Dispose();
            else scratch = null;

            bool scaled = decodeWidth != storedWidth || decodeHeight != storedHeight;
            if (!scaled)
            {
                return null;
            }

            // Codec cannot scale to the requested size (typically PNG); retry native.
            throw new InvalidOperationException("此图片格式无法在内存预算内缩小解码，请选择压缩图");
        }
    }

    internal static SKMatrix CreateOrientationMatrix(SKEncodedOrigin origin, int width, int height)
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
        // The frame pool is intentionally left alone: a pooled bitmap may still be
        // referenced by the render thread while the window is closing, and the
        // buffers are worthless after process exit anyway.
    }
}

/// <summary>
/// Sequential frame reader for one animated file, owned by a single playback
/// loop. The stream and codec stay open for the whole session, so the container
/// is parsed once and Skia can advance frame by frame instead of the per-frame
/// open/parse/seek the one-shot path has to do.
///
/// Decoding happens on the thread pool; <see cref="Dispose"/> waits for an
/// in-flight frame so Skia never touches a released codec.
/// </summary>
public sealed class AnimationCursor : IDisposable
{
    private readonly ImageDecoder _decoder;
    private readonly FileStream _stream;
    private readonly SKCodec _codec;
    private readonly string _path;
    private readonly int? _targetWidth;
    private readonly int? _targetHeight;
    private readonly int _maxMegapixels;
    private readonly int _maxDimension;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Persistent composition buffer. Frames are deltas on top of the previous
    /// one, so this must survive between calls (see DecodeAnimationFrame).
    /// </summary>
    private SKBitmap? _canvas;
    private IDisposable? _canvasBudget;

    /// <summary>Frame currently composed into <see cref="_canvas"/>; -1 = empty.</summary>
    private int _canvasFrame = -1;

    internal AnimationCursor(
        ImageDecoder decoder,
        string path,
        FileStream stream,
        SKCodec codec,
        int? targetWidth,
        int? targetHeight,
        int maxMegapixels,
        int maxDimension)
    {
        _decoder = decoder;
        _path = path;
        _stream = stream;
        _codec = codec;
        _targetWidth = targetWidth;
        _targetHeight = targetHeight;
        _maxMegapixels = maxMegapixels;
        _maxDimension = maxDimension;
        FrameCount = codec.FrameCount;
    }

    public int FrameCount { get; }

    /// <summary>Declared duration of one frame, in milliseconds.</summary>
    public int FrameDurationMs(int frameIndex)
    {
        SKCodecFrameInfo[] info = _codec.FrameInfo;
        if (info.Length == 0) return 0;
        return info[Math.Clamp(frameIndex, 0, info.Length - 1)].Duration;
    }

    public async Task<DecodedImage?> DecodeFrameAsync(int frameIndex, CancellationToken cancellationToken)
    {
        if (_disposed) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return null;
            return await Task.Run(
                () => _decoder.DecodeAnimationFrame(
                    _codec, _path, frameIndex, _targetWidth, _targetHeight, _maxMegapixels, _maxDimension,
                    pooledBuffer: true, ref _canvas, ref _canvasFrame, ref _canvasBudget, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or PathTooLongException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _codec.Dispose();
            _stream.Dispose();
            _canvas?.Dispose();
            _canvasBudget?.Dispose();
            _canvas = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
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

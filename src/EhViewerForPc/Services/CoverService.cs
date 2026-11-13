using System.Buffers.Binary;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using EhViewerForPc.Core.Format;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Services;

/// <summary>
/// Cover pipeline for the library grid. Modelled on WinUIMusicPlayer's
/// CoverLoadQueue: bounded load queue with BelowNormal worker threads, request
/// de-duplication per cache key, and a persistent cache of *decoded* thumbnails
/// (raw BGRA in a BMP container). A cache hit is a file read plus a memcpy - no
/// codec, no SkiaSharp, no EXIF handling - so scrolling back through already
/// visited rows costs almost nothing.
///
/// Cache entries are keyed by stable id + decode size bucket and invalidated by
/// the source file's mtime, mirroring the reference implementation.
/// </summary>
public sealed class CoverService : IDisposable
{
    private readonly ImageLoader _images;
    private readonly LogService _log;
    private readonly string _cacheDirectory;
    private readonly CoverLoadQueue _queue;
    private readonly ConcurrentDictionary<string, string> _resolvedFallback = new(StringComparer.Ordinal);

    public CoverService(ImageLoader images, LogService log, string cacheDirectory)
    {
        _images = images;
        _log = log;
        _cacheDirectory = cacheDirectory;
        _queue = new CoverLoadQueue(log);
    }

    public async Task<DecodedImage?> LoadCoverAsync(
        GalleryInfo gallery,
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken)
    {
        // ItemsStretch="Fill" makes the exact card width drift with window width;
        // 64px buckets keep the cache key (and therefore the decode) stable while
        // never losing more than ~32px of sharpness.
        int width = Bucket(Math.Min(targetWidth, _images.CoverDecodeWidth));
        int height = Bucket(targetHeight);
        string cacheKey = AppPaths.Sanitize(gallery.StableId) + "_" + width + "x" + height;
        using CoverPixels? pixels = await _queue
            .EnqueueAsync(cacheKey, token => ResolveCoverAsync(gallery, cacheKey, width, height, token), cancellationToken)
            .ConfigureAwait(false);
        if (pixels is null) return null;
        return CreateBitmap(pixels);
    }

    private async Task<CoverPixels?> ResolveCoverAsync(
        GalleryInfo gallery,
        string cacheKey,
        int width,
        int height,
        CancellationToken token)
    {
        string cachePath = Path.Combine(_cacheDirectory, cacheKey + ".bmp");
        string fallbackKey = gallery.StableId + "|" + (gallery.CoverPath ?? string.Empty);

        // ① Fast path: declared cover or remembered fallback page has a current
        //    decoded cache entry - no codec work at all.
        string? probe = ProbeSourcePath(gallery, fallbackKey);
        if (probe is not null && TryReadCache(cachePath, probe, out CoverPixels? cached))
        {
            return cached;
        }

        // ② Declared cover (.thumb): decode and publish to the cache.
        if (!string.IsNullOrWhiteSpace(gallery.CoverPath) && File.Exists(gallery.CoverPath))
        {
            DecodedImage? cover = await _images
                .LoadFileAsync(gallery.CoverPath, width, height, token)
                .ConfigureAwait(false);
            if (cover is not null)
            {
                using (cover)
                {
                    CoverPixels pixels = ExtractPixels(cover, gallery.CoverPath);
                    try { WriteCache(cachePath, gallery.CoverPath, pixels, token); return pixels; }
                    catch { pixels.Dispose(); throw; }
                }
            }

            _log.Warn("Cover decode failed, falling back to first readable page: " + gallery.CoverPath);
        }

        // ③ Remembered fallback from a previous run of this process.
        if (_resolvedFallback.TryGetValue(fallbackKey, out string? remembered))
        {
            if (remembered.Length == 0) return null;
            if (File.Exists(remembered))
            {
                if (TryReadCache(cachePath, remembered, out cached))
                {
                    return cached;
                }

                CoverPixels? pixels = await DecodeToPixelsAsync(remembered, cachePath, width, height, token)
                    .ConfigureAwait(false);
                return pixels;
            }
        }

        // ④ First run for a corrupt/missing .thumb: scan the folder and try the
        //    first readable page, then remember it so later scrolls skip the scan.
        PageScanResult pages;
        try
        {
            pages = PageFileParser.Scan(gallery.FolderPath, null, out string? scanError);
            if (scanError is not null)
            {
                _log.Warn("Cover fallback page scan failed: " + scanError);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            _log.Warn("Cover fallback page scan failed: " + gallery.FolderPath, ex);
            _resolvedFallback[fallbackKey] = string.Empty;
            return null;
        }

        foreach (GalleryPage page in pages.Pages)
        {
            token.ThrowIfCancellationRequested();
            PageCandidate? candidate = page.Candidates.FirstOrDefault(c => c.Length > 0);
            if (candidate is null) continue;

            if (TryReadCache(cachePath, candidate.FilePath, out cached))
            {
                _resolvedFallback[fallbackKey] = candidate.FilePath;
                return cached;
            }

            CoverPixels? pixels = await DecodeToPixelsAsync(page.Candidates, cachePath, width, height, token)
                .ConfigureAwait(false);
            if (pixels is not null)
            {
                _resolvedFallback[fallbackKey] = pixels.SourcePath;
                return pixels;
            }
        }

        _resolvedFallback[fallbackKey] = string.Empty;
        return null;
    }

    private async Task<CoverPixels?> DecodeToPixelsAsync(
        object source,
        string cachePath,
        int width,
        int height,
        CancellationToken token)
    {
        DecodedImage? image = source switch
        {
            string path => await _images.LoadFileAsync(path, width, height, token).ConfigureAwait(false),
            IReadOnlyList<PageCandidate> candidates => await _images
                .LoadCandidatesAsync(candidates, width, height, token)
                .ConfigureAwait(false),
            _ => null,
        };

        if (image is null) return null;
        using (image)
        {
            CoverPixels pixels = ExtractPixels(image, image.Path);
            try { WriteCache(cachePath, pixels.SourcePath, pixels, token); return pixels; }
            catch { pixels.Dispose(); throw; }
        }
    }

    private string? ProbeSourcePath(GalleryInfo gallery, string fallbackKey)
    {
        if (!string.IsNullOrWhiteSpace(gallery.CoverPath) && File.Exists(gallery.CoverPath))
        {
            return gallery.CoverPath;
        }

        if (_resolvedFallback.TryGetValue(fallbackKey, out string? remembered) &&
            remembered.Length > 0 && File.Exists(remembered))
        {
            return remembered;
        }

        return null;
    }

    /// <summary>Buckets a requested pixel size onto the 64px cache grid.</summary>
    private static int Bucket(int value)
    {
        if (value <= 0) return 256;
        int rounded = (int)Math.Round(value / 64.0) * 64;
        return Math.Clamp(rounded, 128, 1024);
    }

    private static CoverPixels ExtractPixels(DecodedImage image, string sourcePath)
    {
        int width = image.Bitmap.PixelSize.Width;
        int height = image.Bitmap.PixelSize.Height;
        if (image.Bitmap is not WriteableBitmap writeable)
        {
            throw new InvalidOperationException("Cover decode did not produce a WriteableBitmap.");
        }

        int rowBytes = checked(width * 4);
        var pixels = ArrayPool<byte>.Shared.Rent(checked(rowBytes * height));
        try
        {
            using var frame = writeable.Lock();
            for (int row = 0; row < height; row++)
                Marshal.Copy(IntPtr.Add(frame.Address, checked(row * frame.RowBytes)), pixels, row * rowBytes, rowBytes);
            return new CoverPixels(width, height, pixels, sourcePath);
        }
        catch { ArrayPool<byte>.Shared.Return(pixels); throw; }
    }

    private static DecodedImage CreateBitmap(CoverPixels pixels)
    {
        var bitmap = ImageMemoryBudget.CreateBitmap(pixels.Width, pixels.Height);

        try
        {
            using var frame = bitmap.Lock();
            int rowBytes = checked(pixels.Width * 4);
            for (int row = 0; row < pixels.Height; row++)
                Marshal.Copy(pixels.Pixels, row * rowBytes, IntPtr.Add(frame.Address, checked(row * frame.RowBytes)), rowBytes);
            return new DecodedImage(bitmap, pixels.Width, pixels.Height, pixels.SourcePath);
        }
        catch { ImageMemoryBudget.Release(bitmap); throw; }
    }

    /// <summary>
    /// Reads a cached decoded thumbnail. Raw BMP container (54-byte header +
    /// BGRA pixels, top-down via negative height) written by <see cref="WriteCache"/>.
    /// Invalid entries - stale mtime, truncated, wrong magic - are deleted.
    /// </summary>
    private bool TryReadCache(string cachePath, string sourcePath, out CoverPixels? pixels)
    {
        pixels = null;
        try
        {
            if (!File.Exists(cachePath)) return false;
            if (File.GetLastWriteTimeUtc(cachePath) < File.GetLastWriteTimeUtc(sourcePath))
            {
                try { File.Delete(cachePath); }
                catch (IOException) { }
                return false;
            }

            using var stream = new FileStream(
                cachePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            if (stream.Length < 58) return false;

            Span<byte> header = stackalloc byte[54];
            header.Clear();
            stream.ReadExactly(header);
            if (header[0] != (byte)'B' || header[1] != (byte)'M') return false;

            int width = BinaryPrimitives.ReadInt32LittleEndian(header[18..]);
            int height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(header[22..]));
            if (width <= 0 || height <= 0 || width > 2048 || height > 2048) return false;

            int pixelBytes = width * height * 4;
            if (stream.Length < 54 + pixelBytes) return false;

            var data = ArrayPool<byte>.Shared.Rent(pixelBytes);
            try { stream.ReadExactly(data.AsSpan(0, pixelBytes)); }
            catch { ArrayPool<byte>.Shared.Return(data); throw; }
            pixels = new CoverPixels(width, height, data, sourcePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void WriteCache(string cachePath, string sourcePath, CoverPixels pixels, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".part";
            Span<byte> header = stackalloc byte[54];
            header.Clear();
            header[0] = (byte)'B';
            header[1] = (byte)'M';
            BinaryPrimitives.WriteUInt32LittleEndian(header[2..], (uint)(54 + pixels.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(header[6..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header[10..], 54);
            BinaryPrimitives.WriteUInt32LittleEndian(header[14..], 40);
            BinaryPrimitives.WriteInt32LittleEndian(header[18..], pixels.Width);
            BinaryPrimitives.WriteInt32LittleEndian(header[22..], -pixels.Height);
            BinaryPrimitives.WriteUInt16LittleEndian(header[26..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header[28..], 32);
            BinaryPrimitives.WriteUInt32LittleEndian(header[30..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header[34..], (uint)pixels.Length);

            using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                stream.Write(header);
                stream.Write(pixels.Pixels.AsSpan(0, pixels.Length));
                stream.Flush();
            }

            token.ThrowIfCancellationRequested();
            // Same-named cache entries cannot race (queue de-duplicates by key),
            // but a stale entry from a previous run may exist.
            File.Move(temporary, cachePath, true);
            temporary = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Cover cache write failed: " + ex.Message);
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public void Dispose() => _queue.Dispose();
}

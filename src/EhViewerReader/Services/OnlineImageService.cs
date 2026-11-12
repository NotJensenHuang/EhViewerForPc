using EhViewerReader.Core.Imaging;
using EhViewerReader.Core.Format;
using System.Security.Cryptography;
using System.Text;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Online;
using EhViewerReader.Core.Storage;

namespace EhViewerReader.Services;

/// <summary>
/// Disk-backed image pipeline for everything that comes from the network
/// (listing thumbnails and online reader pages). Files are cached under the app
/// data directory, so re-opening a gallery or scrolling back is cheap, and a
/// partially downloaded page never reaches the decoder.
///
/// The cache is bounded by <see cref="AppSettings.OnlineCacheMegabytes"/> and
/// <see cref="AppSettings.OnlineCacheDays"/>; all hits update the access time so
/// pruning is a real LRU, and pruning is coalesced instead of scanning the whole
/// directory on every write.
/// </summary>
public sealed class OnlineImageService
{
    private const int FreshFileSeconds = 120;

    private readonly AppPaths _paths;
    private readonly EhClient _client;
    private readonly LogService _log;
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly GalleryImageStore _pages;
    private int _pruning;
    private int _pruneQueued;

    public OnlineImageService(
        AppPaths paths,
        EhClient client,
        ImageLoader loader,
        LogService log,
        AppSettings settings,
        GalleryImageStore? pages = null)
    {
        _paths = paths;
        _client = client;
        Loader = loader;
        _log = log;
        _settings = settings;
        _pages = pages ?? new GalleryImageStore(paths, client);
        _pages.FileCached += CacheWritten;
    }

    public ImageLoader Loader { get; }

    public long CacheBudgetBytes => Math.Max(128, _settings.OnlineCacheMegabytes) * 1024L * 1024;

    /// <summary>Null when the user disabled the retention limit (0 days).</summary>
    public TimeSpan? CacheMaxAge =>
        _settings.OnlineCacheDays > 0 ? TimeSpan.FromDays(Math.Clamp(_settings.OnlineCacheDays, 1, 90)) : null;

    /// <summary>Thumbnail request. <paramref name="identity"/> keeps the cached
    /// file stable across entries (gallery id), so history can reuse the image
    /// the listing fetched even when the URL is missing or has expired.</summary>
    public IImageRequest CreateThumbnailRequest(string url, string? identity = null)
    {
        string key = identity is { Length: > 0 } ? identity : url;
        if (string.IsNullOrWhiteSpace(key)) return NullImageRequest.Instance;

        string path = Path.Combine(_paths.ThumbnailCacheDirectory, Hash(key) + ".img");
        if (string.IsNullOrWhiteSpace(url))
        {
            return ImageSignature.IsValidFile(path)
                ? new CachedFileImageRequest(this, "thumb:" + key, path)
                : NullImageRequest.Instance;
        }

        return new RemoteImageRequest(
            this,
            "thumb:" + key,
            path,
            _ => Task.FromResult(url),
            referer: EhUrls.Base(_client.Site) + "/",
            resolved: null);
    }

    public IImageRequest CreatePageRequest(
        EhPageRef page,
        bool original,
        Action<int, int>? resolved, Action<bool>? qualityResolved = null, int? resampledWidth = null)
    {
        return new GalleryPageImageRequest(_pages, Loader, page, original, resolved, CacheWritten, qualityResolved,
            resampledWidth ?? _client.ResampledWidth);
    }

    /// <summary>
    /// Page request whose viewer reference is resolved on demand. The reader can
    /// therefore open with logical page slots before any preview page has been
    /// fetched, and only the pages the user actually sees are resolved.
    /// </summary>
    public IImageRequest CreateLazyPageRequest(
        long gid,
        string key,
        int pageNumber,
        Func<CancellationToken, Task<EhPageRef?>> resolve,
        bool original,
        Action<int, int>? resolved,
        Action<bool>? qualityResolved = null,
        int? resampledWidth = null)
    {
        return new OnlinePageImageRequest(this, gid, key, pageNumber, resolve, original, resolved, qualityResolved,
            resampledWidth ?? _client.ResampledWidth);
    }

    /// <summary>Falls back to cached bytes when the viewer reference cannot be
    /// resolved (offline, quota, deleted page).</summary>
    internal bool TryGetCachedPage(long gid, int pageNumber, bool original, int width, out string path, out bool isOriginal) =>
        _pages.TryGetCachedPath(gid, pageNumber, original, width, out path, out isOriginal);

    private void CacheWritten() => SchedulePrune(immediate: false);

    /// <summary>Starts the background maintenance pass (app start).</summary>
    public void StartBackgroundMaintenance() => SchedulePrune(immediate: true);

    /// <summary>Applies the current budget/retention right away (settings save).</summary>
    public void ApplyPolicy() => SchedulePrune(immediate: true);

    private void SchedulePrune(bool immediate)
    {
        if (Interlocked.Exchange(ref _pruneQueued, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!immediate) await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                if (Interlocked.CompareExchange(ref _pruning, 1, 0) != 0) return;
                try
                {
                    PruneCache(CacheMaxAge, CacheBudgetBytes);
                }
                finally
                {
                    Volatile.Write(ref _pruning, 0);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn("Online cache prune failed: " + ex.Message);
            }
            finally
            {
                Volatile.Write(ref _pruneQueued, 0);
            }
        });
    }

    /// <summary>
    /// Keeps the on-disk page/thumbnail cache bounded: when
    /// <paramref name="maxAge"/> is set, removes entries unused for longer than
    /// it, then evicts the least recently used ones until the directory fits
    /// <paramref name="maxBytes"/>. A null age means "no time limit". Files
    /// written in the last two minutes are treated as in-flight and skipped,
    /// which bounds the overshoot to the transfers that are actually running.
    /// </summary>
    public CachePruneReport PruneCache(TimeSpan? maxAge, long maxBytes)
    {
        int removed = 0;
        long removedBytes = 0;
        CleanupStalePartFiles();

        try
        {
            DateTime? cutoff = maxAge is { } age ? DateTime.UtcNow - age : null;
            var files = new[] { _paths.OnlineCacheDirectory, _paths.ThumbnailCacheDirectory }
                .Where(Directory.Exists)
                .SelectMany(d => new DirectoryInfo(d).EnumerateFiles("*.img"))
                .OrderBy(f => f.LastAccessTimeUtc)
                .ToList();

            long total = files.Sum(f => f.Length);
            foreach (FileInfo file in files)
            {
                bool outdated = cutoff is { } limit && file.LastAccessTimeUtc < limit;
                if (total <= maxBytes && !outdated) continue;
                if (IsInUse(file.FullName)) continue;
                try
                {
                    long length = file.Length;
                    file.Delete();
                    total -= length;
                    removed++;
                    removedBytes += length;
                }
                catch (IOException)
                {
                    // In use by a decoder or a download; it is protected this round.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Online cache prune failed: " + ex.Message);
        }

        return new CachePruneReport(removed, removedBytes, GetCacheUsage().Bytes, maxBytes);
    }

    /// <summary>
    /// True when a decoder or another process holds the file open. Downloads go
    /// through a <c>.part</c> file, so an <c>.img</c> is only ever read; an
    /// exclusive open is therefore a cheap, race-safe protection test.
    /// </summary>
    private static bool IsInUse(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1,
                FileOptions.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Deletes leftover temp files of interrupted downloads.</summary>
    private void CleanupStalePartFiles()
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-1);
        foreach (string directory in new[] { _paths.OnlineCacheDirectory, _paths.ThumbnailCacheDirectory })
        {
            try
            {
                if (!Directory.Exists(directory)) continue;
                foreach (FileInfo part in new DirectoryInfo(directory).EnumerateFiles("*.part"))
                {
                    if (part.LastWriteTimeUtc > cutoff) continue;
                    try { part.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn("Online cache cleanup failed: " + directory + " | " + ex.Message);
            }
        }
    }

    public CacheUsage GetCacheUsage()
    {
        long bytes = 0;
        int files = 0;
        foreach (string directory in new[] { _paths.OnlineCacheDirectory, _paths.ThumbnailCacheDirectory })
        {
            try
            {
                if (!Directory.Exists(directory)) continue;
                foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles("*.img"))
                {
                    bytes += file.Length;
                    files++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn("Online cache usage failed: " + directory + " | " + ex.Message);
            }
        }

        return new CacheUsage(bytes, files);
    }

    /// <summary>Deletes every cached online image (settings → 清除在线缓存).</summary>
    public int ClearCache()
    {
        int removed = 0;
        foreach (string directory in new[] { _paths.OnlineCacheDirectory, _paths.ThumbnailCacheDirectory })
        {
            try
            {
                if (!Directory.Exists(directory)) continue;
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch (IOException)
                    {
                        // In use; skip.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn("Online cache clear failed: " + directory + " | " + ex.Message);
            }
        }

        return removed;
    }

    /// <summary>Downloads <paramref name="url"/> to <paramref name="path"/> once, even under concurrency.</summary>
    internal async Task EnsureFileAsync(
        string url,
        string path,
        string referer,
        CancellationToken token,
        IProgress<ImageLoadStatus>? progress)
    {
        SemaphoreSlim gate = _gates[(uint)StringComparer.Ordinal.GetHashCode(path) % (uint)_gates.Length];
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (ImageSignature.IsValidFile(path))
            {
                Touch(path);
                return;
            }

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // A shared download can be observed by several controls; the client
            // reports into this request's own progress sink.
            await _client.DownloadToFileAsync(url, referer, path, progress, token).ConfigureAwait(false);
            Touch(path);
            CacheWritten();
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException or IOException)
        {
            _log.Warn("Online image fetch failed: " + url + " | " + ex.Message);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    internal static void Touch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal ImageLoader ImageLoader => Loader;

    internal GalleryImageStore PageStore => _pages;

    private static string Hash(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..20].ToLowerInvariant();
    }
}

public readonly record struct CacheUsage(long Bytes, int Files)
{
    public string Text => Files == 0
        ? "0 MB"
        : Core.Download.DownloadTask.FormatSize(Bytes) + " · " + Files + " 个文件";
}

public readonly record struct CachePruneReport(int Removed, long RemovedBytes, long TotalBytes, long BudgetBytes);

public sealed class GalleryPageImageRequest(GalleryImageStore pages, ImageLoader loader, EhPageRef page, bool original,
    Action<int, int>? resolved, Action cacheWritten, Action<bool>? qualityResolved, int resampledWidth) : IImageRequest
{
    public string Key { get; } = $"page:{page.Gid}-{page.Page}:{original}:{resampledWidth}";

    public async Task<DecodedImage?> LoadAsync(
        int targetWidth,
        int targetHeight,
        CancellationToken token,
        IProgress<ImageLoadStatus>? progress = null)
    {
        CachedGalleryImage cached = await pages
            .GetAsync(page, original, token, resampledWidth, progress)
            .ConfigureAwait(false);
        qualityResolved?.Invoke(cached.IsOriginal);
        progress?.Report(ImageLoadStatus.Decoding());
        DecodedImage? image = await loader.LoadFileAsync(cached.Path, targetWidth, targetHeight, token).ConfigureAwait(false);
        if (image is not null)
        {
            resolved?.Invoke(image.DecodedWidth, image.DecodedHeight);
            progress?.Report(ImageLoadStatus.Ready());
        }
        else
        {
            progress?.Report(ImageLoadStatus.Failed("无法解码页面图片"));
        }

        // A cache hit is not a write: only real downloads may trigger pruning.
        if (cached.Downloaded) cacheWritten();
        return image;
    }
}

/// <summary>
/// Online page whose viewer reference is fetched on first use. The reference is
/// shared for the lifetime of the reader session, so re-entering a page or
/// switching modes does not re-resolve it.
/// </summary>
public sealed class OnlinePageImageRequest : IImageRequest
{
    private readonly OnlineImageService _service;
    private readonly long _gid;
    private readonly int _pageNumber;
    private readonly Func<CancellationToken, Task<EhPageRef?>> _resolve;
    private readonly bool _original;
    private readonly Action<int, int>? _resolved;
    private readonly Action<bool>? _qualityResolved;
    private readonly int _resampledWidth;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IImageRequest? _inner;

    public OnlinePageImageRequest(
        OnlineImageService service,
        long gid,
        string key,
        int pageNumber,
        Func<CancellationToken, Task<EhPageRef?>> resolve,
        bool original,
        Action<int, int>? resolved,
        Action<bool>? qualityResolved,
        int resampledWidth)
    {
        _service = service;
        _gid = gid;
        Key = key;
        _pageNumber = pageNumber;
        _resolve = resolve;
        _original = original;
        _resolved = resolved;
        _qualityResolved = qualityResolved;
        _resampledWidth = resampledWidth;
    }

    public string Key { get; }

    public async Task<DecodedImage?> LoadAsync(
        int targetWidth,
        int targetHeight,
        CancellationToken token,
        IProgress<ImageLoadStatus>? progress = null)
    {
        if (_inner is null)
        {
            progress?.Report(ImageLoadStatus.Resolving());
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_inner is null)
                {
                    EhPageRef? page = null;
                    Exception? resolveError = null;
                    try
                    {
                        page = await _resolve(token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is EhException or HttpRequestException or TaskCanceledException)
                    {
                        resolveError = ex;
                    }

                    if (page is not null)
                    {
                        _inner = _service.CreatePageRequest(page, _original, _resolved, _qualityResolved, _resampledWidth);
                    }
                    else if (_service.TryGetCachedPage(_gid, _pageNumber, _original, _resampledWidth,
                                 out string cachedPath, out bool cachedOriginal))
                    {
                        // No viewer reference (offline / quota), but the page was read
                        // before: serve the cached bytes instead of a dead page.
                        _qualityResolved?.Invoke(cachedOriginal);
                        _inner = new CachedFileImageRequest(_service, "cached:" + Key, cachedPath);
                    }
                    else
                    {
                        if (resolveError is not null) throw resolveError;
                        throw new EhException($"无法解析第 {_pageNumber} 页：页面可能已被删除，或当前账号无权访问");
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        return await _inner.LoadAsync(targetWidth, targetHeight, token, progress).ConfigureAwait(false);
    }
}

public sealed class RemoteImageRequest : IImageRequest
{
    private readonly OnlineImageService _service;
    private readonly string _cachePath;
    private readonly Func<CancellationToken, Task<string>> _resolveUrl;
    private readonly string _referer;
    private readonly Action<int, int>? _resolved;

    public RemoteImageRequest(
        OnlineImageService service,
        string key,
        string cachePath,
        Func<CancellationToken, Task<string>> resolveUrl,
        string referer,
        Action<int, int>? resolved)
    {
        _service = service;
        Key = key;
        _cachePath = cachePath;
        _resolveUrl = resolveUrl;
        _referer = referer;
        _resolved = resolved;
    }

    public string Key { get; }

    public async Task<DecodedImage?> LoadAsync(
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken,
        IProgress<ImageLoadStatus>? progress = null)
    {
        if (!ImageSignature.IsValidFile(_cachePath))
        {
            progress?.Report(ImageLoadStatus.Resolving());
            string url = await _resolveUrl(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(url)) return null;
            string referer = _referer.Length > 0 ? _referer : url;
            await _service.EnsureFileAsync(url, _cachePath, referer, cancellationToken, progress).ConfigureAwait(false);
        }
        else
        {
            OnlineImageService.Touch(_cachePath);
        }

        progress?.Report(ImageLoadStatus.Decoding());
        DecodedImage? image = await _service.ImageLoader
            .LoadFileAsync(_cachePath, targetWidth, targetHeight, cancellationToken)
            .ConfigureAwait(false);

        if (image is null)
        {
            progress?.Report(ImageLoadStatus.Failed("无法解码图片"));
            return null;
        }

        progress?.Report(ImageLoadStatus.Ready());
        if (_resolved is not null)
        {
            try
            {
                _resolved(image.DecodedWidth, image.DecodedHeight);
            }
            catch
            {
                // A listener must never break decoding.
            }
        }

        return image;
    }
}

/// <summary>Reads an already cached file without touching the network.</summary>
public sealed class CachedFileImageRequest : IImageRequest
{
    private readonly OnlineImageService _service;
    private readonly string _path;

    public CachedFileImageRequest(OnlineImageService service, string key, string path)
    {
        _service = service;
        Key = key;
        _path = path;
    }

    public string Key { get; }

    public async Task<DecodedImage?> LoadAsync(
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken,
        IProgress<ImageLoadStatus>? progress = null)
    {
        OnlineImageService.Touch(_path);
        progress?.Report(ImageLoadStatus.Decoding());
        DecodedImage? image = await _service.ImageLoader
            .LoadFileAsync(_path, targetWidth, targetHeight, cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(image is null ? ImageLoadStatus.Failed("缓存图片无法解码") : ImageLoadStatus.Ready());
        return image;
    }
}

public sealed class NullImageRequest : IImageRequest
{
    public static readonly NullImageRequest Instance = new();

    public string Key => "null";

    public Task<DecodedImage?> LoadAsync(
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken,
        IProgress<ImageLoadStatus>? progress = null) =>
        Task.FromResult<DecodedImage?>(null);
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using EhViewerReader.Core.Online;
using EhViewerReader.Core.Storage;

namespace EhViewerReader.Services;

/// <summary>
/// Disk-backed image pipeline for everything that comes from the network
/// (listing thumbnails and online reader pages). Files are cached under the app
/// data directory, so re-opening a gallery or scrolling back is cheap, and a
/// partially downloaded page never reaches the decoder.
/// </summary>
public sealed class OnlineImageService
{
    private readonly AppPaths _paths;
    private readonly EhClient _client;
    private readonly LogService _log;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public OnlineImageService(AppPaths paths, EhClient client, ImageLoader loader, LogService log)
    {
        _paths = paths;
        _client = client;
        Loader = loader;
        _log = log;
    }

    public ImageLoader Loader { get; }

    public IImageRequest CreateThumbnailRequest(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return NullImageRequest.Instance;
        string path = Path.Combine(_paths.ThumbnailCacheDirectory, Hash(url) + ".img");
        return new RemoteImageRequest(
            this,
            "thumb:" + url,
            path,
            _ => Task.FromResult(url),
            referer: EhUrls.Base(_client.Site) + "/",
            resolved: null);
    }

    public IImageRequest CreatePageRequest(
        EhPageRef page,
        bool original,
        Action<int, int>? resolved)
    {
        string key = $"{page.Gid}-{page.Page:D5}";
        string path = Path.Combine(_paths.OnlineCacheDirectory, key + ".img");
        return new RemoteImageRequest(
            this,
            "page:" + key,
            path,
            token => _client.GetImageUrlAsync(page, original, token),
            referer: page.PageUrl,
            resolved: resolved);
    }

    /// <summary>
    /// Keeps the on-disk page/thumbnail cache bounded: removes entries older
    /// than <paramref name="maxAge"/> and, if the cache is still larger than
    /// <paramref name="maxBytes"/>, the least recently used ones.
    /// </summary>
    public void PruneCache(TimeSpan maxAge, long maxBytes = 2L * 1024 * 1024 * 1024)
    {
        foreach (string directory in new[] { _paths.OnlineCacheDirectory, _paths.ThumbnailCacheDirectory })
        {
            try
            {
                if (!Directory.Exists(directory)) continue;
                List<FileInfo> files = new DirectoryInfo(directory)
                    .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f.LastAccessTimeUtc)
                    .ToList();

                long total = files.Sum(f => f.Length);
                DateTime cutoff = DateTime.UtcNow - maxAge;
                foreach (FileInfo file in files)
                {
                    bool expired = file.LastAccessTimeUtc < cutoff;
                    if (!expired && total <= maxBytes) continue;

                    total -= file.Length;
                    try
                    {
                        file.Delete();
                    }
                    catch (IOException)
                    {
                        // A page currently being read keeps the file open; it
                        // will be pruned on the next run.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn("Online cache prune failed: " + directory + " | " + ex.Message);
            }
        }
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
    internal async Task EnsureFileAsync(string url, string path, string referer, CancellationToken token)
    {
        SemaphoreSlim gate = _gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.Length > 0) return;
            }

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            await _client.DownloadToFileAsync(url, referer, path, null, token).ConfigureAwait(false);
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

    private static string Hash(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..20].ToLowerInvariant();
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

    public async Task<DecodedImage?> LoadAsync(int targetWidth, int targetHeight, CancellationToken cancellationToken)
    {
        if (!File.Exists(_cachePath) || new FileInfo(_cachePath).Length == 0)
        {
            string url = await _resolveUrl(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(url)) return null;
            string referer = _referer.Length > 0 ? _referer : url;
            await _service.EnsureFileAsync(url, _cachePath, referer, cancellationToken).ConfigureAwait(false);
        }

        DecodedImage? image = await _service.Loader
            .LoadFileAsync(_cachePath, targetWidth, targetHeight, cancellationToken)
            .ConfigureAwait(false);

        if (image is not null && _resolved is not null)
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

public sealed class NullImageRequest : IImageRequest
{
    public static readonly NullImageRequest Instance = new();

    public string Key => "null";

    public Task<DecodedImage?> LoadAsync(int targetWidth, int targetHeight, CancellationToken cancellationToken) =>
        Task.FromResult<DecodedImage?>(null);
}

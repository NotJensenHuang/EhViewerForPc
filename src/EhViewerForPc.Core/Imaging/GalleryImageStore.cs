using System.Security.Cryptography;
using System.Text;
using EhViewerForPc.Core.Format;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Core.Imaging;

public sealed record CachedGalleryImage(string Path, bool IsOriginal, bool Downloaded = false);

/// <summary>Shared page files for online reading and downloads. A fixed stripe
/// set bounds synchronization memory regardless of how many pages are visited.</summary>
public sealed class GalleryImageStore
{
    private readonly AppPaths _paths;
    private readonly EhClient _client;
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public event Action? FileCached;
    public Func<long, int, ImageQuality, int, string?>? FindLocalPage { get; set; }

    public GalleryImageStore(AppPaths paths, EhClient client) { _paths = paths; _client = client; }

    /// <summary>
    /// Returns the on-disk page file, downloading it when absent. Progress is
    /// reported as resolving → downloading (with the server's Content-Length when
    /// present) → decoding. A hit on an existing file reports decoding only.
    /// </summary>
    public async Task<CachedGalleryImage> GetAsync(
        EhPageRef page,
        bool original,
        CancellationToken token,
        int? resampledWidth = null,
        IProgress<ImageLoadStatus>? progress = null,
        bool refresh = false)
    {
        int width = resampledWidth ?? _client.ResampledWidth;
        string key = CacheKey(page.Gid, page.Page);
        SemaphoreSlim gate = _gates[(uint)StringComparer.Ordinal.GetHashCode(key) % (uint)_gates.Length];
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string requested = Path.Combine(_paths.OnlineCacheDirectory, key + (original ? "-orig" : "-" + width) + ".img");
            // 重试必须绕过在线缓存：占位图或错误响应本身可能是完整的 GIF/PNG，
            // 容器校验无法识别，复用缓存只会让失败稳定复现。
            if (!refresh && ImageSignature.IsValidFile(requested))
            {
                Touch(requested);
                progress?.Report(ImageLoadStatus.Decoding(new FileInfo(requested).Length));
                return new(requested, original);
            }

            string? local = FindLocalPage?.Invoke(page.Gid, page.Page, original ? ImageQuality.Original : ImageQuality.Resampled, width);
            if (local is not null && ImageSignature.IsValidFile(local))
            {
                progress?.Report(ImageLoadStatus.Decoding());
                return new(local, original);
            }

            progress?.Report(ImageLoadStatus.Resolving());
            EhResolvedImage resolved = await _client.ResolveImageAsync(page, original, token, width, refresh).ConfigureAwait(false);
            string target = Path.Combine(_paths.OnlineCacheDirectory, key + (resolved.IsOriginal ? "-orig" : "-" + width) + ".img");
            bool downloaded = false;
            if (!ImageSignature.IsValidFile(target))
            {
                Directory.CreateDirectory(_paths.OnlineCacheDirectory);
                await _client.DownloadToFileAsync(resolved.Url, page.PageUrl, target, progress, token).ConfigureAwait(false);
                downloaded = true;
            }

            Touch(target);
            progress?.Report(ImageLoadStatus.Decoding(new FileInfo(target).Length));
            if (downloaded) FileCached?.Invoke();
            return new(target, resolved.IsOriginal, downloaded);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// True when a page file for this gallery already exists on disk. Used by the
    /// reader as an offline fallback: a page whose viewer reference can no longer
    /// be resolved (no network) is still readable from cached bytes.
    /// </summary>
    public bool TryGetCachedPath(long gid, int pageNumber, bool original, int? resampledWidth, out string path, out bool isOriginal)
    {
        int width = resampledWidth ?? _client.ResampledWidth;
        string key = CacheKey(gid, pageNumber);
        string requested = Path.Combine(_paths.OnlineCacheDirectory, key + (original ? "-orig" : "-" + width) + ".img");
        if (ImageSignature.IsValidFile(requested))
        {
            path = requested;
            isOriginal = original;
            return true;
        }

        string alternative = Path.Combine(_paths.OnlineCacheDirectory, key + (original ? "-" + width : "-orig") + ".img");
        if (ImageSignature.IsValidFile(alternative))
        {
            path = alternative;
            isOriginal = !original;
            return true;
        }

        path = string.Empty;
        isOriginal = false;
        return false;
    }

    /// <summary>
    /// Cache file name for one page. The scope (site + account) is part of the
    /// key so a different account or site can never reuse another context's
    /// resampled/original bytes.
    /// </summary>
    private string CacheKey(long gid, int pageNumber)
    {
        string scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_client.CacheScope)))[..8]
            .ToLowerInvariant();
        return $"{scope}-{gid}-{pageNumber:D5}";
    }

    private static void Touch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

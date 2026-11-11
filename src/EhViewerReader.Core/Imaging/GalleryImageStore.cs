using EhViewerReader.Core.Format;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Online;
using EhViewerReader.Core.Storage;

namespace EhViewerReader.Core.Imaging;

public sealed record CachedGalleryImage(string Path, bool IsOriginal);

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

    public async Task<CachedGalleryImage> GetAsync(EhPageRef page, bool original, CancellationToken token, int? resampledWidth = null)
    {
        int width = resampledWidth ?? _client.ResampledWidth;
        string key = $"{page.Gid}-{page.Page:D5}";
        SemaphoreSlim gate = _gates[(uint)StringComparer.Ordinal.GetHashCode(key) % (uint)_gates.Length];
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string requested = Path.Combine(_paths.OnlineCacheDirectory, key + (original ? "-orig" : "-" + width) + ".img");
            if (ImageSignature.IsValidFile(requested)) { Touch(requested); return new(requested, original); }
            string? local = FindLocalPage?.Invoke(page.Gid, page.Page, original ? ImageQuality.Original : ImageQuality.Resampled, width);
            if (local is not null && ImageSignature.IsValidFile(local)) return new(local, original);
            EhResolvedImage resolved = await _client.ResolveImageAsync(page, original, token, width).ConfigureAwait(false);
            string target = Path.Combine(_paths.OnlineCacheDirectory, key + (resolved.IsOriginal ? "-orig" : "-" + width) + ".img");
            if (!ImageSignature.IsValidFile(target))
            {
                Directory.CreateDirectory(_paths.OnlineCacheDirectory);
                await _client.DownloadToFileAsync(resolved.Url, page.PageUrl, target, null, token).ConfigureAwait(false);
            }
            Touch(target);
            FileCached?.Invoke();
            return new(target, resolved.IsOriginal);
        }
        finally { gate.Release(); }
    }

    private static void Touch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

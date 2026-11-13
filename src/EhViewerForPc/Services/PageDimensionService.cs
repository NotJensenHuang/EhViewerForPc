using System.Collections.Concurrent;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Services;

public sealed class PageDimensionCacheFile
{
    public int SchemaVersion { get; set; } = 1;
    public string StableId { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public DateTime SavedUtc { get; set; }
    public ConcurrentDictionary<int, CachedPageDimension> Pages { get; set; } = new();
}

public sealed class CachedPageDimension
{
    public int Width { get; set; }
    public int Height { get; set; }
    public long FileLength { get; set; }
    public long FileMtimeTicks { get; set; }
    public int EncodedOrigin { get; set; }
    public string FileName { get; set; } = string.Empty;
}

public sealed class PageDimension
{
    public required GalleryPage Page { get; init; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Known { get; set; }
    public string? Error { get; set; }
}

public sealed class PageDimensionProgress
{
    public int Processed { get; init; }
    public int Total { get; init; }
    public string? CurrentItem { get; init; }
}

public sealed class PageDimensionService
{
    private const int PlaceholderWidth = 1200;
    private const int PlaceholderHeight = 1600;
    private readonly AppPaths _paths;
    private readonly ImageDecoder _decoder;
    private readonly LogService _log;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public PageDimensionService(AppPaths paths, ImageDecoder decoder, LogService log)
    {
        _paths = paths;
        _decoder = decoder;
        _log = log;
    }

    public int Concurrency { get; set; } = 6;

    public async Task<IReadOnlyList<PageDimension>> LoadAsync(
        GalleryInfo gallery,
        IReadOnlyList<GalleryPage> pages,
        IProgress<PageDimensionProgress>? progress,
        CancellationToken cancellationToken)
    {
        PageDimensionCacheFile cache = LoadCache(gallery);
        var results = new PageDimension[pages.Count];
        var changed = false;
        int processed = 0;
        int total = pages.Count;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, pages.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Concurrency),
                CancellationToken = cancellationToken,
            },
            async (index, token) =>
            {
                GalleryPage page = pages[index];
                PageDimension dimension = await LoadOneAsync(cache, page, token).ConfigureAwait(false);
                results[index] = dimension;
                if (dimension.Known)
                {
                    changed = true;
                }

                int count = Interlocked.Increment(ref processed);
                if (count % 8 == 0 || count == total)
                {
                    progress?.Report(new PageDimensionProgress
                    {
                        Processed = count,
                        Total = total,
                        CurrentItem = page.Number.ToString(),
                    });
                }
            }).ConfigureAwait(false);

        if (changed)
        {
            await SaveCacheAsync(gallery, cache, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    private async Task<PageDimension> LoadOneAsync(
        PageDimensionCacheFile cache,
        GalleryPage page,
        CancellationToken cancellationToken)
    {
        PageCandidate? candidate = page.Candidates.FirstOrDefault(c => c.Length > 0);
        if (candidate is null)
        {
            return Placeholder(page, "empty");
        }

        if (cache.Pages.TryGetValue(page.Number, out CachedPageDimension? cached) &&
            cached.FileLength == candidate.Length &&
            string.Equals(cached.FileName, candidate.FileName, StringComparison.OrdinalIgnoreCase))
        {
            long mtime = SafeMtime(candidate.FilePath);
            if (mtime == cached.FileMtimeTicks)
            {
                return new PageDimension
                {
                    Page = page,
                    Width = cached.Width,
                    Height = cached.Height,
                    Known = true,
                };
            }
        }

        ImageDimensionInfo? info = await _decoder.ReadDimensionAsync(candidate.FilePath, cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            return Placeholder(page, "dimension-unreadable");
        }

        cache.Pages[page.Number] = new CachedPageDimension
        {
            Width = info.Value.DisplayWidth,
            Height = info.Value.DisplayHeight,
            FileLength = info.Value.FileLength,
            FileMtimeTicks = info.Value.FileMtimeTicks,
            EncodedOrigin = info.Value.EncodedOrigin,
            FileName = candidate.FileName,
        };

        return new PageDimension
        {
            Page = page,
            Width = info.Value.DisplayWidth,
            Height = info.Value.DisplayHeight,
            Known = true,
        };
    }

    private PageDimensionCacheFile LoadCache(GalleryInfo gallery)
    {
        string path = _paths.GetDimensionCacheFile(gallery.StableId);
        PageDimensionCacheFile cache = JsonFileStore.LoadOrDefault(
            path, ReaderJsonContext.Default.PageDimensionCacheFile, () => new PageDimensionCacheFile(), out _);
        if (cache.SchemaVersion != 1 ||
            !string.Equals(cache.StableId, gallery.StableId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(cache.FolderPath, gallery.FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            cache = new PageDimensionCacheFile
            {
                StableId = gallery.StableId,
                FolderPath = gallery.FolderPath,
            };
        }

        cache.Pages = new ConcurrentDictionary<int, CachedPageDimension>(cache.Pages);
        return cache;
    }

    private async Task SaveCacheAsync(GalleryInfo gallery, PageDimensionCacheFile cache, CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cache.StableId = gallery.StableId;
            cache.FolderPath = gallery.FolderPath;
            cache.SavedUtc = DateTime.UtcNow;
            string path = _paths.GetDimensionCacheFile(gallery.StableId);
            await Task.Run(() =>
            {
                if (!JsonFileStore.Save(path, cache, ReaderJsonContext.Default.PageDimensionCacheFile, out string? error) && error is not null)
                {
                    _log.Warn("Dimension cache save failed: " + error);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static PageDimension Placeholder(GalleryPage page, string reason) => new()
    {
        Page = page,
        Width = PlaceholderWidth,
        Height = PlaceholderHeight,
        Known = false,
        Error = reason,
    };

    private static long SafeMtime(string path)
    {
        try
        {
            return new FileInfo(path).LastWriteTimeUtc.Ticks;
        }
        catch
        {
            return 0;
        }
    }
}

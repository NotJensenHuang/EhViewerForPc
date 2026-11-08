using System.Collections.Concurrent;
using EhViewerReader.Core.Format;
using EhViewerReader.Core.Models;

namespace EhViewerReader.Services;

public sealed class CoverService
{
    private readonly ImageLoader _images;
    private readonly LogService _log;
    private readonly ConcurrentDictionary<string, string> _resolvedFallback = new(StringComparer.Ordinal);

    public CoverService(ImageLoader images, LogService log)
    {
        _images = images;
        _log = log;
    }

    public async Task<DecodedImage?> LoadCoverAsync(
        GalleryInfo gallery,
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken)
    {
        string? coverPath = gallery.CoverPath;
        string cacheKey = gallery.StableId + "|" + (coverPath ?? string.Empty);
        if (_resolvedFallback.TryGetValue(cacheKey, out string? remembered))
        {
            if (remembered.Length == 0) return null;
            DecodedImage? cached = await _images
                .LoadFileAsync(remembered, targetWidth, targetHeight, cancellationToken)
                .ConfigureAwait(false);
            if (cached is not null) return cached;
        }

        if (!string.IsNullOrWhiteSpace(coverPath) && File.Exists(coverPath))
        {
            DecodedImage? cover = await _images
                .LoadFileAsync(coverPath, targetWidth, targetHeight, cancellationToken)
                .ConfigureAwait(false);
            if (cover is not null)
            {
                return cover;
            }

            _log.Warn("Cover decode failed, falling back to first readable page: " + coverPath);
        }

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
            return null;
        }

        foreach (GalleryPage page in pages.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PageCandidate? candidate = page.Candidates.FirstOrDefault(c => c.Length > 0);
            if (candidate is null) continue;

            DecodedImage? image = await _images
                .LoadCandidatesAsync(page.Candidates, targetWidth, targetHeight, cancellationToken)
                .ConfigureAwait(false);
            if (image is not null)
            {
                // Remember the working fallback so a corrupt .thumb is not
                // re-scanned for every scroll into view.
                _resolvedFallback[cacheKey] = candidate.FilePath;
                return image;
            }
        }

        _resolvedFallback[cacheKey] = string.Empty;
        return null;
    }
}

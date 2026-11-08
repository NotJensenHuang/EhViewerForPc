using EhViewerReader.Core.Format;
using EhViewerReader.Core.Models;

namespace EhViewerReader.Services;

public interface IImageRequest
{
    string Key { get; }
    Task<DecodedImage?> LoadAsync(int targetWidth, int targetHeight, CancellationToken cancellationToken);
}

public sealed class PageImageRequest : IImageRequest
{
    private readonly IReadOnlyList<PageCandidate> _candidates;
    private readonly ImageLoader _loader;

    public PageImageRequest(IReadOnlyList<PageCandidate> candidates, ImageLoader loader)
    {
        _candidates = candidates;
        _loader = loader;
        Key = candidates.Count == 0 ? "empty" : candidates[0].FilePath;
    }

    public string Key { get; }

    public Task<DecodedImage?> LoadAsync(int targetWidth, int targetHeight, CancellationToken cancellationToken) =>
        _loader.LoadCandidatesAsync(_candidates, targetWidth, targetHeight, cancellationToken);
}

public sealed class CoverImageRequest : IImageRequest
{
    private readonly GalleryInfo _gallery;
    private readonly CoverService _covers;

    public CoverImageRequest(GalleryInfo gallery, CoverService covers)
    {
        _gallery = gallery;
        _covers = covers;
    }

    public string Key => _gallery.StableId;

    public Task<DecodedImage?> LoadAsync(int targetWidth, int targetHeight, CancellationToken cancellationToken) =>
        _covers.LoadCoverAsync(_gallery, targetWidth, targetHeight, cancellationToken);
}

public sealed class ImageLoader
{
    private readonly ImageDecoder _decoder;
    private readonly LogService _log;

    public ImageLoader(ImageDecoder decoder, LogService log)
    {
        _decoder = decoder;
        _log = log;
    }

    /// <summary>Direct decoder access (diagnostics and smoke tests).</summary>
    public ImageDecoder Decoder => _decoder;

    public int MaxMegapixels { get; set; } = 24;
    public int MaxDimension { get; set; } = 16000;

    public async Task<DecodedImage?> LoadCandidatesAsync(
        IReadOnlyList<PageCandidate> candidates,
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken)
    {
        foreach (PageCandidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Length <= 0) continue;

            DecodedImage? image = await _decoder.DecodeAsync(
                candidate.FilePath,
                targetWidth > 0 ? targetWidth : null,
                targetHeight > 0 ? targetHeight : null,
                MaxMegapixels,
                MaxDimension,
                cancellationToken).ConfigureAwait(false);

            if (image is not null)
            {
                return image;
            }
        }

        return null;
    }

    public async Task<DecodedImage?> LoadFileAsync(
        string path,
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return await _decoder.DecodeAsync(
            path,
            targetWidth > 0 ? targetWidth : null,
            targetHeight > 0 ? targetHeight : null,
            MaxMegapixels,
            MaxDimension,
            cancellationToken).ConfigureAwait(false);
    }

    public void ReportCandidatesFailed(IReadOnlyList<PageCandidate> candidates, string reason)
    {
        string names = string.Join(", ", candidates.Select(c => c.FileName));
        _log.Warn($"Page decode failed ({reason}): {names}");
    }

    internal static string ExtensionOf(string path)
    {
        string extension = Path.GetExtension(path);
        return string.IsNullOrEmpty(extension) ? "unknown" : extension.TrimStart('.').ToLowerInvariant();
    }

    internal static bool LooksLikeImage(string path) => ImageSignature.DetectFile(path) != ImageKind.Unknown;
}

using EhViewerReader.Core.Format;
using EhViewerReader.Core.Imaging;
using EhViewerReader.Core.Models;

namespace EhViewerReader.Services;

public interface IImageRequest
{
    string Key { get; }

    /// <summary>
    /// Loads and decodes the image. <paramref name="progress"/> may receive
    /// resolving/downloading (with byte counts) and decoding states; it is
    /// optional and must never be required for correctness.
    /// </summary>
    Task<DecodedImage?> LoadAsync(
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken,
        IProgress<ImageLoadStatus>? progress = null);
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

    public async Task<DecodedImage?> LoadAsync(
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken,
        IProgress<ImageLoadStatus>? progress = null)
    {
        PageCandidate? candidate = _candidates.FirstOrDefault(c => c.Length > 0);
        progress?.Report(ImageLoadStatus.Decoding(candidate?.Length ?? 0));
        DecodedImage? image = await _loader
            .LoadCandidatesAsync(_candidates, targetWidth, targetHeight, cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(image is null
            ? ImageLoadStatus.Failed(_candidates.Count == 0 ? "没有可读页面文件" : "无法解码页面文件")
            : ImageLoadStatus.Ready(candidate?.Length ?? 0));
        return image;
    }
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

    public async Task<DecodedImage?> LoadAsync(
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken,
        IProgress<ImageLoadStatus>? progress = null)
    {
        progress?.Report(ImageLoadStatus.Decoding());
        DecodedImage? image = await _covers
            .LoadCoverAsync(_gallery, targetWidth, targetHeight, cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(image is null ? ImageLoadStatus.Failed("无法读取封面") : ImageLoadStatus.Ready());
        return image;
    }
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
    public int CoverDecodeWidth { get; set; } = 360;

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

using System.Globalization;
using EhViewerReader.Core.Format;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Online;

namespace EhViewerReader.Services;

/// <summary>
/// Everything the reader needs from a gallery, regardless of where the pages
/// live. Two implementations exist: a local folder (EhViewer download) and an
/// online gallery served by <see cref="EhClient"/>.
/// </summary>
public interface IReaderSource
{
    /// <summary>Per-copy identity (used for caches that must not collide).</summary>
    string StableId { get; }

    /// <summary>Cross-copy identity (gid); reading progress is keyed by this.</summary>
    string ProgressKey { get; }

    string Title { get; }
    string SourceLabel { get; }
    string DebugInfo { get; }
    string MissingSummary { get; }

    IReadOnlyList<GalleryPage> Pages { get; }

    IImageRequest CreateRequest(int index);

    Task<IReadOnlyList<PageDimension>> LoadDimensionsAsync(
        IProgress<PageDimensionProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class LocalReaderSource : IReaderSource
{
    private readonly AppServices _services;
    private readonly GalleryInfo _gallery;

    public LocalReaderSource(AppServices services, GalleryInfo gallery)
    {
        _services = services;
        _gallery = gallery;

        IReadOnlyList<GalleryPage> pages = PageFileParser.Scan(gallery.FolderPath, null, out string? scanError).Pages;
        Pages = pages;
        if (scanError is not null)
        {
            services.Log.Warn("Reader page scan failed: " + gallery.FolderPath + " | " + scanError);
        }
    }

    public GalleryInfo Gallery => _gallery;
    public string StableId => _gallery.StableId;
    public string ProgressKey => _gallery.WorkId;
    public string Title => _gallery.DisplayTitle;
    public string SourceLabel => _gallery.SourceName;
    public string DebugInfo => $"{_gallery.StableId} · {Pages.Count} 页 · {_gallery.ExtensionSummary}";
    public IReadOnlyList<GalleryPage> Pages { get; }

    public string MissingSummary => _gallery.MissingRanges.Count == 0
        ? string.Empty
        : "缺页 " + string.Join(", ", _gallery.MissingRanges.Select(r => r.ToString()));

    public IImageRequest CreateRequest(int index) =>
        new PageImageRequest(Pages[index].Candidates, _services.Images);

    public Task<IReadOnlyList<PageDimension>> LoadDimensionsAsync(
        IProgress<PageDimensionProgress>? progress,
        CancellationToken cancellationToken) =>
        _services.Dimensions.LoadAsync(_gallery, Pages, progress, cancellationToken);
}

/// <summary>
/// Online gallery source. Page references come from the preview pages; an image
/// is fetched (and cached on disk) the first time it is displayed. Continuous
/// mode starts with placeholder slots and refines them as pages decode.
/// </summary>
public sealed class OnlineReaderSource : IReaderSource
{
    private const int PlaceholderWidth = 1200;
    private const int PlaceholderHeight = 1700;

    private readonly AppServices _services;
    private readonly EhGalleryDetails _details;
    private readonly IReadOnlyList<EhPageRef> _refs;
    private readonly bool _originalImages;

    public OnlineReaderSource(
        AppServices services,
        EhGalleryDetails details,
        IReadOnlyList<EhPageRef> refs,
        ImageQuality quality = ImageQuality.Original)
    {
        _services = services;
        _details = details;
        _refs = refs;
        // Originals need an account; anonymous browsing always gets the
        // site-resampled file, so skip the extra nl request in that case.
        _originalImages = quality == ImageQuality.Original && services.Online.IsSignedIn;

        var pages = new List<GalleryPage>(refs.Count);
        for (int i = 0; i < refs.Count; i++)
        {
            pages.Add(new GalleryPage
            {
                Number = i + 1,
                DisplayIndex = i,
                Candidates = [],
                State = PageState.Ok,
            });
        }

        Pages = pages;
    }

    /// <summary>Raised on a background thread when a page's real size became known.</summary>
    public Action<int, int, int>? DimensionResolved { get; set; }

    public string StableId => "online-" + _details.Gid.ToString(CultureInfo.InvariantCulture);
    public string ProgressKey => _details.Gid.ToString(CultureInfo.InvariantCulture);
    public string Title => _details.DisplayTitle;
    public string SourceLabel => "在线 · " + EhUrls.SiteName(_services.Online.Site);
    public string DebugInfo => $"online:{_details.Gid} · {Pages.Count} 页";
    public string MissingSummary => string.Empty;
    public IReadOnlyList<GalleryPage> Pages { get; }

    public IImageRequest CreateRequest(int index)
    {
        if (index < 0 || index >= _refs.Count) return NullImageRequest.Instance;
        int captured = index;
        return _services.OnlineImages.CreatePageRequest(
            _refs[index],
            original: _originalImages,
            (width, height) => DimensionResolved?.Invoke(captured, width, height));
    }

    public Task<IReadOnlyList<PageDimension>> LoadDimensionsAsync(
        IProgress<PageDimensionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var dimensions = new List<PageDimension>(Pages.Count);
        for (int i = 0; i < Pages.Count; i++)
        {
            EhPageRef reference = _refs[i];
            bool known = reference.Width > 0 && reference.Height > 0;
            dimensions.Add(new PageDimension
            {
                Page = Pages[i],
                Width = known ? reference.Width : PlaceholderWidth,
                Height = known ? reference.Height : PlaceholderHeight,
                Known = known,
                Error = known ? null : "online-estimate",
            });
        }

        progress?.Report(new PageDimensionProgress { Processed = Pages.Count, Total = Pages.Count });
        return Task.FromResult<IReadOnlyList<PageDimension>>(dimensions);
    }
}

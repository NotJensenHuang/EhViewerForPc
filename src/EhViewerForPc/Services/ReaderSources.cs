using System.Globalization;
using EhViewerForPc.Core.Format;
using EhViewerForPc.Core.Imaging;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;

namespace EhViewerForPc.Services;

/// <summary>
/// Everything the reader needs from a gallery, regardless of where the pages
/// live. Three implementations exist: a local folder (EhViewer download), an
/// online gallery served by <see cref="EhClient"/>, and a download task folder.
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
    int ExpectedPageCount => Pages.Count;
    bool IsComplete => true;

    /// <summary>Whether a single unreadable page can be fetched again from the
    /// original gallery. Local downloads can; online pages are always fetchable;
    /// a bare folder without a gid cannot.</summary>
    bool CanRedownload { get; }

    /// <summary><paramref name="refresh"/> drops any cached request for the page
    /// so the next load re-fetches it (manual retry).</summary>
    IImageRequest CreateRequest(int index, bool refresh = false);

    Task<IReadOnlyList<PageDimension>> LoadDimensionsAsync(
        int focusPageNumber,
        IProgress<PageDimensionProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class LocalReaderSource : IReaderSource
{
    private readonly AppServices _services;
    private readonly GalleryInfo _gallery;
    private readonly GalleryPage[] _pages;
    private IReadOnlyList<PageRange> _missingRanges;

    public LocalReaderSource(AppServices services, GalleryInfo gallery)
    {
        _services = services;
        _gallery = gallery;
        _missingRanges = gallery.MissingRanges;
        services.Works.Upsert(new WorkRecord { WorkId = gallery.WorkId, Gid = gallery.Gid, Token = gallery.Token,
            Title = gallery.DisplayTitle, PageCount = gallery.ExpectedPages ?? gallery.PageCount });

        PageScanResult scan = PageFileParser.Scan(gallery.FolderPath, null, out string? scanError);
        // 打开阅读器只做便宜的目录扫描：容器校验要逐页两次 seek，197 页冷缓存
        // 实测约 1 秒，放在 UI 线程会让「开始阅读」明显卡顿。校验改在页面显示
        // 之后由 ValidateStatesAsync 在后台补做。
        // 文件扫描只返回实际文件；阅读序列按原始页号补齐，避免缺页改变进度与总页数。
        int count = Math.Max(gallery.ExpectedPages ?? 0, Math.Max(gallery.MaxPageNumber, scan.MaxPageNumber));
        var pages = new GalleryPage[count];
        int present = 0;
        for (int i = 0; i < count; i++)
        {
            GalleryPage? page = present < scan.Pages.Count && scan.Pages[present].Number == i + 1
                ? scan.Pages[present++] : null;
            pages[i] = new GalleryPage
            {
                Number = i + 1,
                DisplayIndex = i,
                Candidates = page?.Candidates ?? [],
                State = page?.State ?? PageState.Empty,
            };
        }
        _pages = pages;
        IsComplete = !gallery.IsIncomplete && !scan.IsIncomplete && present == count;
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
    public IReadOnlyList<GalleryPage> Pages => _pages;
    public int ExpectedPageCount => Math.Max(_gallery.ExpectedPages ?? 0, Pages.Count);
    public bool IsComplete { get; private set; }

    /// <summary>需要原画廊的 gid / token 与原始页号；自然命名或 gid 冲突的副本
    /// 无法定位在线页，界面上不显示重下按钮。</summary>
    public bool CanRedownload => _gallery.Gid is > 0 && !_gallery.HasGidConflict && !_gallery.HasNaturalPageNames;

    public string MissingSummary => _missingRanges.Count == 0
        ? string.Empty
        : "缺页 " + string.Join(", ", _missingRanges.Select(r => r.ToString()));

    public IImageRequest CreateRequest(int index, bool refresh = false) =>
        // Corrupt pages must not reach the decoder: a failed decode paints the
        // network-error overlay on top of the "this page is damaged" notice.
        Pages[index].State != PageState.Ok ? NullImageRequest.Instance
            : new PageImageRequest(Pages[index].Candidates, _services.Images);

    internal Task RedownloadPageAsync(int index, CancellationToken token, IProgress<ImageLoadStatus>? progress = null) =>
        _services.RetryLocalPageAsync(_gallery, Pages[index], token, progress);

    /// <summary>
    /// 打开阅读器之后的容器校验：逐页读取头尾判断截断/错误内容，结果写回
    /// 页状态。纯读操作，不修改任何文件，放在后台线程执行。
    /// </summary>
    internal async Task<PageScanResult> ValidateStatesAsync(CancellationToken token)
    {
        string folder = _gallery.FolderPath;
        int? expected = _gallery.ExpectedPages;
        PageScanResult scan = await Task.Run(() =>
        {
            var header = SpiderInfoParser.TryParseFile(Path.Combine(folder, ".ehviewer"), out _) ?? new SpiderHeader();
            if (expected is > 0)
            {
                header.HasPages = true;
                header.Pages = expected.Value;
            }
            return PageFileParser.Scan(folder, header, out _, validateContainers: true);
        }, token).ConfigureAwait(false);

        foreach (GalleryPage scanned in scan.Pages)
        {
            token.ThrowIfCancellationRequested();
            int slot = scanned.Number - 1;
            if (slot < 0 || slot >= _pages.Length) continue;
            if (_pages[slot].Number != scanned.Number) continue;
            _pages[slot].State = scanned.State;
        }

        _missingRanges = scan.MissingRanges;
        IsComplete = Pages.Count > 0 && Pages.All(p => p.State == PageState.Ok);
        return scan;
    }

    internal async Task ReloadPageAsync(int index, CancellationToken token)
    {
        int number = Pages[index].Number;
        string folder = _gallery.FolderPath;
        int? expected = _gallery.ExpectedPages;
        var scan = await Task.Run(() =>
        {
            var header = SpiderInfoParser.TryParseFile(Path.Combine(folder, ".ehviewer"), out _) ?? new SpiderHeader();
            // 保留真实头信息，同时沿用已知总数，避免旧下载头使尾部缺页消失。
            if (expected is > 0)
            {
                header.HasPages = true;
                header.Pages = expected.Value;
            }
            return PageFileParser.Scan(folder, header, out _, validateContainers: true);
        }, token);
        var page = scan.Pages.FirstOrDefault(p => p.Number == number);
        _pages[index] = new GalleryPage
        {
            Number = number, DisplayIndex = index,
            Candidates = page?.Candidates ?? [], State = page?.State ?? PageState.Empty,
        };
        IsComplete = Pages.Count > 0 && Pages.All(p => p.State == PageState.Ok);
        _missingRanges = scan.MissingRanges;
    }

    public Task<IReadOnlyList<PageDimension>> LoadDimensionsAsync(
        int focusPageNumber,
        IProgress<PageDimensionProgress>? progress,
        CancellationToken cancellationToken) =>
        _services.Dimensions.LoadAsync(_gallery, Pages, progress, cancellationToken);
}

/// <summary>
/// Online gallery source. Page slots exist as soon as the gallery is known (the
/// detail page carries the page count), while each viewer page reference is
/// resolved on demand, so opening a 500-page gallery costs one navigation and
/// the first visible page — not a full preview-pagination scan.
/// </summary>
public sealed class OnlineReaderSource : IReaderSource, IDisposable
{
    private const int PlaceholderWidth = 1200;
    private const int PlaceholderHeight = 1700;

    private readonly AppServices _services;
    private readonly EhGalleryDetails _details;
    private readonly OnlinePageIndex _index;
    private readonly IImageRequest?[] _requests;
    private readonly bool _originalImages;
    private readonly int _resampledWidth;
    private string _actualQuality = string.Empty;

    public OnlineReaderSource(
        AppServices services,
        EhGalleryDetails details,
        OnlinePageIndex index,
        ImageQuality quality = ImageQuality.Resampled)
    {
        _services = services;
        _details = details;
        _index = index;

        services.Works.Upsert(new WorkRecord { WorkId = details.Gid.ToString(CultureInfo.InvariantCulture), Gid = details.Gid, Token = details.Token,
            Title = details.DisplayTitle, ThumbnailUrl = details.ThumbUrl, PageCount = details.Pages, Quality = quality });
        // Originals need an account; anonymous browsing always gets the
        // site-resampled file, so skip the original-file resolution in that case.
        _resampledWidth = services.Settings.ResampledWidth;
        _originalImages = quality == ImageQuality.Original && services.Online.IsSignedIn;

        int count = Math.Max(1, index.PageCount);
        var pages = new List<GalleryPage>(count);
        for (int i = 0; i < count; i++)
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
        _requests = new IImageRequest?[count];
    }

    /// <summary>Raised on a background thread when a page's real size became known.</summary>
    public Action<int, int, int>? DimensionResolved { get; set; }

    public Action? QualityChanged { get; set; }

    public string StableId => "online-" + _details.Gid.ToString(CultureInfo.InvariantCulture);
    public string ProgressKey => _details.Gid.ToString(CultureInfo.InvariantCulture);
    public string Title => _details.DisplayTitle;
    public string SourceLabel => "在线 · " + EhUrls.SiteName(_services.Online.Site) + " · " + (_actualQuality.Length > 0 ? _actualQuality : _originalImages ? "原图" : $"压缩图 {_resampledWidth}px");
    public string DebugInfo => $"online:{_details.Gid} · {Pages.Count} 页 · 按需解析";
    public string MissingSummary => string.Empty;
    public IReadOnlyList<GalleryPage> Pages { get; }
    public int ExpectedPageCount => Math.Max(Pages.Count, _index.PageCount);

    /// <summary>The detail page's page count is authoritative; a page whose image
    /// fails to load is reported per page and does not move the reading position.</summary>
    public bool IsComplete => true;

    /// <summary>Online pages are always re-fetchable (refresh drops the cached
    /// resolution), so a failed page can be retried in place.</summary>
    public bool CanRedownload => true;

    public IImageRequest CreateRequest(int index, bool refresh = false)
    {
        if (index < 0 || index >= Pages.Count) return NullImageRequest.Instance;
        if (refresh) _requests[index] = null;
        return _requests[index] ??= _services.OnlineImages.CreateLazyPageRequest(
            _details.Gid,
            $"page:{_details.Gid}-{index + 1}:{_originalImages}:{_resampledWidth}",
            index + 1,
            token => _index.GetPageAsync(index + 1, token),
            _originalImages,
            (width, height) => DimensionResolved?.Invoke(index, width, height),
            original =>
            {
                _actualQuality = original ? "原图" : _originalImages ? "压缩图（原图不可用）" : $"压缩图 {_resampledWidth}px";
                QualityChanged?.Invoke();
            },
            _resampledWidth);
    }

    public async Task<IReadOnlyList<PageDimension>> LoadDimensionsAsync(
        int focusPageNumber,
        IProgress<PageDimensionProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Only the preview page that holds the reading position is primed; the
        // rest stay estimates until each image decodes and refines its slot.
        try
        {
            await _index.GetPageAsync(Math.Max(1, focusPageNumber), cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException) { }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }

        var dimensions = new List<PageDimension>(Pages.Count);
        for (int i = 0; i < Pages.Count; i++)
        {
            bool known = _index.TryGetKnown(i + 1, out EhPageRef? reference) &&
                         reference is { Width: > 0, Height: > 0 };
            dimensions.Add(new PageDimension
            {
                Page = Pages[i],
                Width = known ? reference!.Width : PlaceholderWidth,
                Height = known ? reference!.Height : PlaceholderHeight,
                Known = known,
                Error = known ? null : "online-estimate",
            });
        }

        progress?.Report(new PageDimensionProgress { Processed = Pages.Count, Total = Pages.Count });
        return dimensions;
    }

    /// <summary>Drops the page-reference index when the reader session ends.</summary>
    public void Dispose() => _index.Dispose();
}

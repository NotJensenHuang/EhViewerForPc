using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Services;

namespace EhViewerForPc.ViewModels;

/// <summary>
/// One page in the continuous strip. DisplayHeight is derived from the real
/// image ratio so the virtualizing panel can compute stable extents.
/// </summary>
public sealed class ContinuousPageItem : ObservableObject
{
    /// <summary>Gap between consecutive pages. Zero: continuous mode renders
    /// pages flush so a gallery reads like one seamless strip.</summary>
    public const double Gap = 0;
    private const double PlaceholderWidth = 1200;
    private const double PlaceholderHeight = 1600;

    private double _displayWidth;
    private double _displayHeight;
    private IImageRequest _request;
    private int _originalWidth;
    private int _originalHeight;

    public ContinuousPageItem(GalleryPage page, IImageRequest request, int originalWidth, int originalHeight, bool known)
    {
        Page = page;
        _request = request;
        _originalWidth = originalWidth > 0 ? originalWidth : (int)PlaceholderWidth;
        _originalHeight = originalHeight > 0 ? originalHeight : (int)PlaceholderHeight;
        Known = known;
    }

    public GalleryPage Page { get; private set; }

    public IImageRequest Request
    {
        get => _request;
        private set => SetProperty(ref _request, value);
    }

    /// <summary>Swaps the request so a failed page can retry without rebuilding
    /// the whole strip (which would move the scroll position).</summary>
    public void UpdateRequest(IImageRequest request) => Request = request;
    public void UpdatePage(GalleryPage page, IImageRequest request)
    {
        Page = page;
        OnPropertyChanged(nameof(Page));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(IsCorrupt));
        OnPropertyChanged(nameof(MissingPageText));
        Request = request;
    }
    public int OriginalWidth => _originalWidth;
    public int OriginalHeight => _originalHeight;
    public bool Known { get; private set; }
    public int Number => Page.Number;

    /// <summary>缺页/损坏页提供的直接重下入口；由阅读器注入，绑定到按钮。</summary>
    public IAsyncRelayCommand<int>? RetryCommand { get; init; }

    /// <summary>RetryCommand 只有在副本能定位到在线原始页时才注入。</summary>
    public bool CanRetry => RetryCommand is not null;

    /// <summary>A page the reader must not attempt to decode. Empty and corrupt
    /// look the same to the layout but differ in what the user can do about it.</summary>
    public bool IsMissing => Page.State is PageState.Empty or PageState.Corrupt or PageState.Unreadable;

    /// <summary>Just the present-but-unreadable case: retrying the download is
    /// the actionable fix, whereas a missing page has nothing to replace.</summary>
    public bool IsCorrupt => Page.State is PageState.Corrupt or PageState.Unreadable;

    public string MissingPageText => Page.State switch
    {
        PageState.Corrupt => $"第 {Number} 页已损坏",
        PageState.Unreadable => $"第 {Number} 页无法读取",
        _ => $"第 {Number} 页缺失",
    };

    private bool _isRetrying;
    private double _retryProgress = -1;
    private string _retryStatusText = string.Empty;

    public bool IsRetrying => _isRetrying;
    public double RetryProgress => _retryProgress;
    public bool IsRetryProgressIndeterminate => _retryProgress < 0;
    public string RetryStatusText => _retryStatusText;
    public bool HasRetryStatus => _retryStatusText.Length > 0;

    public void SetRetryState(bool running, double progress, string text)
    {
        _isRetrying = running;
        _retryProgress = progress;
        _retryStatusText = text;
        OnPropertyChanged(nameof(IsRetrying));
        OnPropertyChanged(nameof(RetryProgress));
        OnPropertyChanged(nameof(IsRetryProgressIndeterminate));
        OnPropertyChanged(nameof(RetryStatusText));
        OnPropertyChanged(nameof(HasRetryStatus));
    }

    /// <summary>
    /// Replaces the estimated aspect ratio with the real one once the page has
    /// been decoded. Online galleries start from a placeholder; the slot is
    /// resized along its current layout axis so the strip stays aligned.
    /// </summary>
    public void SetNaturalSize(int width, int height, bool keepWidth)
    {
        if (width <= 0 || height <= 0) return;
        if (width == _originalWidth && height == _originalHeight && Known) return;

        _originalWidth = width;
        _originalHeight = height;
        Known = true;
        OnPropertyChanged(nameof(OriginalWidth));
        OnPropertyChanged(nameof(OriginalHeight));
        OnPropertyChanged(nameof(Ratio));

        if (keepWidth)
        {
            if (DisplayWidth > 0) Resize(DisplayWidth);
        }
        else if (DisplayHeight > 0)
        {
            ResizeToHeight(DisplayHeight);
        }
    }

    public double DisplayWidth
    {
        get => _displayWidth;
        private set => SetProperty(ref _displayWidth, value);
    }

    public double DisplayHeight
    {
        get => _displayHeight;
        private set
        {
            if (SetProperty(ref _displayHeight, value))
            {
                OnPropertyChanged(nameof(SlotHeight));
            }
        }
    }

    public double SlotHeight => _displayHeight + Gap;
    public double SlotWidth => _displayWidth + Gap;
    public double Top { get; set; }

    /// <summary>
    /// Horizontal slot position, accumulated in <em>display order</em> (the
    /// order the items are laid out, reversed for right-to-left reading).
    /// </summary>
    public double Left { get; set; }

    /// <summary>Index in reading order; display order differs when RTL.</summary>
    public int ReadIndex { get; set; }

    public double Ratio => OriginalHeight / (double)Math.Max(1, OriginalWidth);

    public void Resize(double width)
    {
        DisplayWidth = width;
        DisplayHeight = Math.Max(80, width * Ratio);
    }

    /// <summary>Sizes the slot for horizontal reading: height fixed, width by ratio.</summary>
    public void ResizeToHeight(double height)
    {
        DisplayHeight = Math.Max(80, height);
        DisplayWidth = Ratio > 0.01 ? Math.Max(80, height / Ratio) : PlaceholderWidth;
    }

    public string Label => Page.Number.ToString();
}

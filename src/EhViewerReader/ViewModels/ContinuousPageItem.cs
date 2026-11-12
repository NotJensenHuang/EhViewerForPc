using CommunityToolkit.Mvvm.ComponentModel;
using EhViewerReader.Core.Models;
using EhViewerReader.Services;

namespace EhViewerReader.ViewModels;

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

    public GalleryPage Page { get; }

    public IImageRequest Request
    {
        get => _request;
        private set => SetProperty(ref _request, value);
    }

    /// <summary>Swaps the request so a failed page can retry without rebuilding
    /// the whole strip (which would move the scroll position).</summary>
    public void UpdateRequest(IImageRequest request) => Request = request;
    public int OriginalWidth => _originalWidth;
    public int OriginalHeight => _originalHeight;
    public bool Known { get; private set; }
    public int Number => Page.Number;

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

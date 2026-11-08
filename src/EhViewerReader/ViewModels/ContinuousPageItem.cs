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

    public ContinuousPageItem(GalleryPage page, IImageRequest request, int originalWidth, int originalHeight, bool known)
    {
        Page = page;
        Request = request;
        OriginalWidth = originalWidth > 0 ? originalWidth : (int)PlaceholderWidth;
        OriginalHeight = originalHeight > 0 ? originalHeight : (int)PlaceholderHeight;
        Known = known;
    }

    public GalleryPage Page { get; }
    public IImageRequest Request { get; }
    public int OriginalWidth { get; }
    public int OriginalHeight { get; }
    public bool Known { get; }
    public int Number => Page.Number;

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

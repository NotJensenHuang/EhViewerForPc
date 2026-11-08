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
    public const double Gap = 12;
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
    public double Top { get; set; }

    public double Ratio => OriginalHeight / (double)Math.Max(1, OriginalWidth);

    public void Resize(double width)
    {
        DisplayWidth = width;
        DisplayHeight = Math.Max(80, width * Ratio);
    }

    public string Label => Page.Number.ToString();
}

namespace EhViewerReader.Core.Imaging;

public enum ImageLoadPhase
{
    /// <summary>Looking up the image page / resolving the direct URL.</summary>
    Resolving = 0,

    /// <summary>Receiving bytes. <see cref="ImageLoadStatus.TotalBytes"/> may be null.</summary>
    Downloading = 1,

    /// <summary>Bytes are on disk; the decoder is working.</summary>
    Decoding = 2,

    /// <summary>Bitmap ready; the consumer can drop its overlay.</summary>
    Ready = 3,

    /// <summary>The page failed. <see cref="ImageLoadStatus.Message"/> says why.</summary>
    Failed = 4,
}

/// <summary>
/// Progress of one image load, shared by the HTTP layer, the disk cache and the
/// image controls. <see cref="TotalBytes"/> is only set when the server sent a
/// trustworthy Content-Length, so callers must never fake a percentage.
/// </summary>
public readonly record struct ImageLoadStatus(
    ImageLoadPhase Phase,
    long ReceivedBytes = 0,
    long? TotalBytes = null,
    string? Message = null)
{
    public bool HasPercentage => TotalBytes is > 0 && ReceivedBytes >= 0;

    public double Fraction => TotalBytes is > 0
        ? Math.Clamp(ReceivedBytes / (double)TotalBytes.Value, 0, 1)
        : 0;

    public static ImageLoadStatus Resolving() => new(ImageLoadPhase.Resolving);

    public static ImageLoadStatus Decoding(long bytes = 0) => new(ImageLoadPhase.Decoding, bytes);

    public static ImageLoadStatus Ready(long bytes = 0) => new(ImageLoadPhase.Ready, bytes);

    public static ImageLoadStatus Failed(string message, long received = 0, long? total = null) =>
        new(ImageLoadPhase.Failed, received, total, message);
}

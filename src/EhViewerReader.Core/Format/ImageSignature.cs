namespace EhViewerReader.Core.Format;

public enum ImageKind
{
    Unknown = 0,
    Jpeg,
    Png,
    Gif,
    Webp,
}

public static class ImageSignature
{
    public static ImageKind Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return ImageKind.Jpeg;
        }

        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return ImageKind.Png;
        }

        if (bytes.Length >= 6 &&
            bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'8')
        {
            return ImageKind.Gif;
        }

        if (bytes.Length >= 12 &&
            bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return ImageKind.Webp;
        }

        return ImageKind.Unknown;
    }

    public static ImageKind DetectFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                16, FileOptions.SequentialScan);
            Span<byte> buffer = stackalloc byte[16];
            int read = stream.Read(buffer);
            return Detect(buffer[..read]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            return ImageKind.Unknown;
        }
    }

    public static string ToKindString(ImageKind kind) => kind switch
    {
        ImageKind.Jpeg => "jpeg",
        ImageKind.Png => "png",
        ImageKind.Gif => "gif",
        ImageKind.Webp => "webp",
        _ => "unknown",
    };
}

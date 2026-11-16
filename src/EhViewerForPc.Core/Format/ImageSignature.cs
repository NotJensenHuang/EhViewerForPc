namespace EhViewerForPc.Core.Format;

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

    /// <summary>Cheap container validation before an image is committed or reused.
    /// Rejects HTML, empty and truncated transfers without allocating image pixels.</summary>
    public static bool IsValidFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64);
            if (stream.Length < 16) return false;
            Span<byte> header = stackalloc byte[32];
            int read = stream.Read(header);
            ReadOnlySpan<byte> head = header[..read];
            ImageKind kind = Detect(head);
            Span<byte> tail = stackalloc byte[12];
            stream.Seek(-12, SeekOrigin.End);
            stream.ReadExactly(tail);
            return kind switch
            {
                ImageKind.Jpeg => tail[^2] == 0xff && tail[^1] == 0xd9,
                ImageKind.Png => tail[4..8].SequenceEqual("IEND"u8),
                ImageKind.Gif => tail[^1] == 0x3b,
                ImageKind.Webp => IsCompleteWebp(head, stream.Length),
                _ => false,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>WebP 载荷边界：RIFF 声明长度 + 8 是有效字节数。截断必须拒绝，
    /// 但少数编码器会在末尾追加填充或把长度写成 0，因此只要求文件不短于
    /// 声明长度，并核对首个 chunk 标识。</summary>
    private static bool IsCompleteWebp(ReadOnlySpan<byte> header, long length)
    {
        if (header.Length < 16) return false;
        bool vp8 = header[12] == (byte)'V' && header[13] == (byte)'P' && header[14] == (byte)'8' &&
                   header[15] is (byte)' ' or (byte)'L' or (byte)'X';
        if (!vp8) return false;
        uint declared = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        return declared == 0 ? length >= 20 : length >= declared + 8L;
    }

    /// <summary>人类可读的容器识别结果，用于下载失败时定位真实原因
    /// （格式不支持与传输截断必须能区分开）。</summary>
    public static string DescribeFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16, FileOptions.SequentialScan);
            long length = stream.Length;
            Span<byte> header = stackalloc byte[32];
            int read = stream.Read(header);
            ImageKind kind = Detect(header[..read]);
            return kind == ImageKind.Unknown
                ? $"{length} 字节，未识别为受支持的图片"
                : $"{ToKindString(kind)}，{length} 字节";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "临时文件不可读";
        }
    }
}

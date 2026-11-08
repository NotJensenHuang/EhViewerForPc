using System.Buffers;
using System.Globalization;
using System.Text;
using EhViewerReader.Core.Models;

namespace EhViewerReader.Core.Format;

/// <summary>
/// Reads the EhViewer download metadata file <c>.ehviewer</c>.
///
/// VERSION2 text layout:
///   VERSION2 / startPage(hex) / gid / token / ignored / previewPages /
///   previewPerPage / pages / "{index} {pToken}"...
///
/// VERSION1 text layout (first line is not VERSION...):
///   startPage(hex) / gid / token / ignored / previewPages / ignored /
///   pages / "{index} {pToken}"...
///
/// Note the ignored line between previewPages and pages exists in BOTH versions.
/// Newer recovery code can also encounter a CBOR map with gid/token/pages.
/// </summary>
public static class SpiderInfoParser
{
    public const int MaxPages = 100_000;
    private const int MaxTextBytes = 1 * 1024 * 1024;
    private const int MaxCborBytes = 32 * 1024 * 1024;
    private const int MaxLineBytes = 8192;

    public static SpiderHeader? TryParseFile(string path, out string? error)
    {
        error = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                8192, FileOptions.SequentialScan);
            return TryParse(stream, out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    public static SpiderHeader? TryParse(Stream stream, out string? error)
    {
        error = null;
        try
        {
            int first = stream.ReadByte();
            if (first < 0)
            {
                error = "empty .ehviewer file";
                return null;
            }

            bool cbor = first is >= 0xA0 and <= 0xBF;
            byte[] bytes = ReadBytes(stream, first, cbor ? MaxCborBytes : MaxTextBytes);
            return cbor
                ? CborHeaderReader.TryRead(bytes, out error)
                : ParseText(bytes, out error);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    private static byte[] ReadBytes(Stream stream, int firstByte, int maxBytes)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)firstByte);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int remaining = maxBytes - 1;
            while (remaining > 0)
            {
                int read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (read <= 0) break;
                ms.Write(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return ms.ToArray();
    }

    public static SpiderHeader? ParseText(ReadOnlySpan<byte> bytes, out string? error)
    {
        error = null;
        var lines = ReadLines(bytes, 16);
        if (lines.Count == 0)
        {
            error = "empty text header";
            return null;
        }

        var header = new SpiderHeader { Version = 1 };
        int index = 0;
        string firstLine = lines[0].TrimStart('\uFEFF');

        int version = 1;
        if (firstLine.StartsWith("VERSION", StringComparison.Ordinal))
        {
            version = ParseVersion(firstLine);
            header.Version = version;
            index++;
            if (version != 2)
            {
                error = $"unsupported .ehviewer version: {firstLine}";
                header.Error = error;
                return header;
            }
        }

        // startPage is present in both versions as the next line.
        if (index >= lines.Count)
        {
            error = "missing startPage";
            header.Error = error;
            return header;
        }

        header.StartPage = ParseStartPage(lines[index]);
        index++;

        if (index >= lines.Count || !long.TryParse(lines[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out long gid))
        {
            error = "invalid gid";
            header.Error = error;
            return header;
        }

        header.Gid = gid;
        header.HasGid = true;
        index++;

        if (index >= lines.Count)
        {
            error = "missing token";
            header.Error = error;
            return header;
        }

        header.Token = lines[index].TrimEnd('\r');
        index++;

        // ignored line (always consumed)
        if (index < lines.Count) index++;

        if (index >= lines.Count || !int.TryParse(lines[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int previewPages))
        {
            error = "invalid previewPages";
            header.Error = error;
            return header;
        }

        header.PreviewPages = previewPages;
        index++;

        // previewPerPage (VERSION2) OR consumed-but-ignored line (VERSION1)
        if (index < lines.Count)
        {
            if (version >= 2)
            {
                _ = int.TryParse(lines[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int previewPerPage);
                header.PreviewPerPage = previewPerPage;
            }

            index++;
        }

        if (index >= lines.Count || !int.TryParse(lines[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pages))
        {
            error = "invalid pages";
            header.Error = error;
            return header;
        }

        header.Pages = pages;
        header.HasPages = pages > 0 && pages <= MaxPages;
        header.IsValid = header.HasGid && header.HasPages;
        if (!header.HasPages)
        {
            error = pages <= 0 ? "pages <= 0" : "pages exceeds limit";
            header.Error = error;
        }

        return header;
    }

    private static int ParseVersion(string firstLine)
    {
        if (!int.TryParse(firstLine.AsSpan("VERSION".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
        {
            return -1;
        }

        return version;
    }

    private static int ParseStartPage(string text)
    {
        int value = 0;
        foreach (char ch in text)
        {
            value *= 16;
            if (ch is >= '0' and <= '9') value += ch - '0';
            else if (ch is >= 'a' and <= 'f') value += ch - 'a' + 10;
        }

        return Math.Max(value, 0);
    }

    private static List<string> ReadLines(ReadOnlySpan<byte> bytes, int maxLines)
    {
        var lines = new List<string>(maxLines);
        int start = 0;
        for (int i = 0; i < bytes.Length && lines.Count < maxLines; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            AddLine(bytes[start..i], lines);
            start = i + 1;
        }

        if (start < bytes.Length && lines.Count < maxLines)
        {
            AddLine(bytes[start..], lines);
        }

        return lines;
    }

    private static void AddLine(ReadOnlySpan<byte> raw, List<string> lines)
    {
        if (raw.Length > 0 && raw[^1] == (byte)'\r') raw = raw[..^1];
        if (raw.Length > MaxLineBytes) raw = raw[..MaxLineBytes];
        lines.Add(Encoding.UTF8.GetString(raw));
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EhViewerReader.Core.Format;

public static partial class GalleryNameParser
{
    [GeneratedRegex(@"^(?<gid>\d+)-(?<title>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex GalleryDirectoryRegex();

    public static (long? Gid, string Title) Parse(string folderName)
    {
        Match match = GalleryDirectoryRegex().Match(folderName);
        if (!match.Success)
        {
            return (null, folderName.Trim());
        }

        string gidText = match.Groups["gid"].Value;
        string title = match.Groups["title"].Value.Trim();
        if (!long.TryParse(gidText, out long gid))
        {
            return (null, folderName.Trim());
        }

        return (gid, title.Length == 0 ? folderName.Trim() : title);
    }
}

public static class StableIdFactory
{
    public static string PathHash(string path)
    {
        string normalized = NormalizePath(path);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    public static string NormalizePath(string path)
    {
        string full = Path.GetFullPath(path);
        full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.ToLowerInvariant();
    }

    public static string ForGallery(long? gid, int gidUseCount, string folderPath, bool forcePathSuffix)
    {
        string pathHash = PathHash(folderPath);
        if (gid.HasValue)
        {
            if (gidUseCount > 1 || forcePathSuffix)
            {
                return $"{gid.Value}@{pathHash}";
            }

            return gid.Value.ToString();
        }

        return $"dir@{pathHash}";
    }
}

namespace EhViewerReader.Core.Storage;

public sealed class AppPaths
{
    public AppPaths(string? baseDirectory = null)
    {
        BaseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EhViewerReader")
            : Path.GetFullPath(baseDirectory);
    }

    public string BaseDirectory { get; }
    public string SettingsFile => Path.Combine(BaseDirectory, "settings.json");
    public string ProgressFile => Path.Combine(BaseDirectory, "progress.json");

    /// <summary>Schema-1 single-library cache; kept for migration only.</summary>
    public string LegacyLibraryCacheFile => Path.Combine(BaseDirectory, "library-cache.json");

    public string GetLibraryCacheFile(string sourceId) =>
        Path.Combine(BaseDirectory, "library-cache-" + Sanitize(sourceId) + ".json");

    public string LogDirectory => Path.Combine(BaseDirectory, "logs");
    public string CoverCacheDirectory => Path.Combine(BaseDirectory, "covers");
    public string DimensionCacheDirectory => Path.Combine(BaseDirectory, "dimensions");
    public string ExportDirectory => Path.Combine(BaseDirectory, "exports");

    /// <summary>On-demand page cache used by the online reader.</summary>
    public string OnlineCacheDirectory => Path.Combine(BaseDirectory, "online-cache");

    /// <summary>Thumbnail cache for online listings.</summary>
    public string ThumbnailCacheDirectory => Path.Combine(BaseDirectory, "thumbnails");

    public string DownloadTasksFile => Path.Combine(BaseDirectory, "downloads.json");

    /// <summary>Cached EhTagTranslation database (compact JSON).</summary>
    public string TagTranslationFile => Path.Combine(BaseDirectory, "tag-translations.json");

    /// <summary>
    /// Default download root when the user has not configured one. Kept outside
    /// the app data directory so downloads survive a reinstall and can be added
    /// as an ordinary library source.
    /// </summary>
    public string DefaultDownloadDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EhViewerDownloads");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(BaseDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(CoverCacheDirectory);
        Directory.CreateDirectory(DimensionCacheDirectory);
        Directory.CreateDirectory(ExportDirectory);
        Directory.CreateDirectory(OnlineCacheDirectory);
        Directory.CreateDirectory(ThumbnailCacheDirectory);
    }

    public string GetDimensionCacheFile(string stableId) =>
        Path.Combine(DimensionCacheDirectory, Sanitize(stableId) + ".json");

    public string GetCoverCacheFile(string stableId, long sourceLength, long sourceMtimeTicks, string extension = ".jpg") =>
        Path.Combine(CoverCacheDirectory, $"{Sanitize(stableId)}-{sourceLength}-{sourceMtimeTicks}{extension}");

    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return "_";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) || ch is '/' or '\\' or ':' ? '_' : ch).ToArray();
        string result = new string(chars);
        return result.Length <= 120 ? result : result[..120];
    }
}

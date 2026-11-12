using System.Diagnostics;
using EhViewerReader.Core.Storage;
using System.Text.RegularExpressions;
using EhViewerReader.Core.Format;
using EhViewerReader.Core.Models;

namespace EhViewerReader.Core.Scanning;

public sealed partial class LibraryScanner
{
    [GeneratedRegex("^[0-9a-fA-F]{32,}\\.tmp$", RegexOptions.CultureInvariant)]
    private static partial Regex TempDirectoryRegex();

    public ScanResult Scan(
        string rootPath,
        LibraryCache? cache,
        bool forceRefresh = false,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? sourceId = null,
        string? sourceName = null,
        bool qualifyStableId = false,
        string? onlyFolder = null)
    {
        string root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"资料库目录不存在: {root}");
        }

        var stopwatch = Stopwatch.StartNew();
        var cacheEntries = cache?.Entries ?? new Dictionary<string, GalleryCacheEntry>(PathIdentity.Comparer);
        var directories = onlyFolder is null ? EnumerateGalleryDirectories(new DirectoryInfo(root), cancellationToken).ToArray() : new[] { new DirectoryInfo(onlyFolder) };

        var galleries = new List<GalleryInfo>(directories.Length);
        var newEntries = new Dictionary<string, GalleryCacheEntry>(PathIdentity.Comparer);
        int cacheHits = 0;
        int scanned = 0;
        int ignored = 0;
        int processed = 0;

        progress?.Report(new ScanProgress { Phase = "枚举目录", Total = directories.Length });

        foreach (DirectoryInfo directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;

            string folderName = directory.Name;
            (long? nameGid, string nameTitle) = GalleryNameParser.Parse(folderName);
            // Enumerated DirectoryInfo instances may carry a stale timestamp from the
            // directory index (NTFS flushes directory metadata lazily).
            DateTime folderWriteUtc = Directory.GetLastWriteTimeUtc(directory.FullName);

            string ehPath = Path.Combine(directory.FullName, ".ehviewer");
            string thumbPath = Path.Combine(directory.FullName, ".thumb");
            var ehFile = TryReadFileInfo(ehPath);
            var thumbFile = TryReadFileInfo(thumbPath);
            IReadOnlyList<PageCandidate> files = PageFileParser.Enumerate(directory.FullName, out string? enumError);
            var snapshot = GallerySnapshot.Create(folderWriteUtc, ehFile, thumbFile, files);

            string relativePath = Path.GetRelativePath(root, directory.FullName);
            GalleryCacheEntry? cached = cacheEntries.GetValueOrDefault(relativePath);
            GalleryInfo gallery;
            if (!forceRefresh && cached is not null && snapshot.Matches(cached))
            {
                gallery = FromCache(cached, directory.FullName);
                cacheHits++;
            }
            else
            {
                gallery = ScanDirectory(
                    directory,
                    nameGid,
                    nameTitle,
                    folderWriteUtc,
                    ehPath,
                    thumbPath,
                    ehFile,
                    thumbFile,
                    files,
                    enumError);
                scanned++;
                if (gallery.PageCount == 0 && !gallery.HasEhviewer)
                {
                    ignored++;
                }
            }

            galleries.Add(gallery);
            newEntries[relativePath] = ToCacheEntry(gallery, snapshot);

            if (processed % 25 == 0 || processed == directories.Length)
            {
                progress?.Report(new ScanProgress
                {
                    Phase = "扫描画廊",
                    Processed = processed,
                    Total = directories.Length,
                    CurrentItem = folderName,
                    FromCache = cacheHits == processed,
                });
            }
        }

        AssignStableIds(galleries, sourceId, sourceName, qualifyStableId);
        stopwatch.Stop();
        var libraryCache = new LibraryCache
        {
            SchemaVersion = LibraryCache.CurrentSchemaVersion,
            RootPath = root,
            SavedUtc = DateTime.UtcNow,
            Entries = newEntries,
        };

        progress?.Report(new ScanProgress
        {
            Phase = "完成",
            Processed = directories.Length,
            Total = directories.Length,
            FromCache = cacheHits == directories.Length,
        });

        return new ScanResult
        {
            RootPath = root,
            Galleries = galleries,
            IgnoredDirectoryCount = ignored,
            CacheHitCount = cacheHits,
            ScannedDirectoryCount = scanned,
            Elapsed = stopwatch.Elapsed,
            Cache = libraryCache,
        };
    }

    private static IEnumerable<DirectoryInfo> EnumerateGalleryDirectories(DirectoryInfo root, CancellationToken token, int depth = 0)
    {
        foreach (DirectoryInfo directory in root.EnumerateDirectories().Where(IsCandidateDirectory).OrderBy(d => d.Name, PathIdentity.Comparer))
        {
            token.ThrowIfCancellationRequested();
            bool gallery = File.Exists(Path.Combine(directory.FullName, ".ehviewer")) ||
                directory.EnumerateFiles().Any(f => PageFileParser.TryParsePageName(f.Name, out _, out _));
            if (gallery || !directory.EnumerateDirectories().Any()) yield return directory;
            else if (depth < 8)
                foreach (var child in EnumerateGalleryDirectories(directory, token, depth + 1)) yield return child;
        }
    }

    private static bool IsCandidateDirectory(DirectoryInfo directory)
    {
        string name = directory.Name;
        if (name.StartsWith(".", StringComparison.Ordinal)) return false;
        if (name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)) return false;
        if (TempDirectoryRegex().IsMatch(name)) return false;
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    private static GalleryInfo ScanDirectory(
        DirectoryInfo directory,
        long? nameGid,
        string nameTitle,
        DateTime folderWriteUtc,
        string ehPath,
        string thumbPath,
        FileInfo? ehFile,
        FileInfo? thumbFile,
        IReadOnlyList<PageCandidate> files,
        string? enumError)
    {
        bool hasEh = ehFile is not null;
        bool hasThumb = thumbFile is { Length: > 0 };

        SpiderHeader? header = null;
        string? headerError = enumError;
        if (hasEh)
        {
            header = SpiderInfoParser.TryParseFile(ehPath, out string? parseError);
            if (parseError is not null) headerError ??= parseError;
        }

        PageScanResult pages = PageFileParser.Scan(files, header, out string? pageError);
        if (pageError is not null) headerError ??= pageError;

        string? coverPath = null;
        string? coverKind = null;
        string? coverSource = null;
        if (hasThumb)
        {
            coverPath = thumbPath;
            coverSource = "thumb";
        }
        else
        {
            foreach (GalleryPage page in pages.Pages)
            {
                foreach (PageCandidate candidate in page.Candidates)
                {
                    if (candidate.Length <= 0) continue;
                    ImageKind kind = ImageSignature.DetectFile(candidate.FilePath);
                    if (kind == ImageKind.Unknown) continue;
                    coverPath = candidate.FilePath;
                    coverKind = ImageSignature.ToKindString(kind);
                    coverSource = "page";
                    goto CoverFound;
                }
            }

        CoverFound:
            ;
        }

        long? resolvedGid = nameGid ?? (header is { HasGid: true } ? header.Gid : null);
        bool gidConflict = nameGid.HasValue && header is { HasGid: true } && nameGid.Value != header.Gid;
        (string[] languages, string[] tags) = GalleryTags.Parse(nameTitle);
        bool isIncomplete = pages.IsIncomplete;

        return new GalleryInfo
        {
            StableId = string.Empty,
            Gid = resolvedGid,
            Token = header?.Token ?? string.Empty,
            DirectoryGid = nameGid,
            HeaderGid = header is { HasGid: true } ? header.Gid : null,
            Title = string.IsNullOrWhiteSpace(nameTitle) ? directory.Name : nameTitle,
            FolderName = directory.Name,
            FolderPath = directory.FullName,
            PageCount = pages.PageCount,
            MaxPageNumber = pages.MaxPageNumber,
            ExpectedPages = pages.HasExpectedPages ? pages.ExpectedPages : null,
            MissingRanges = pages.MissingRanges,
            HasHeaderPageCountOutdated = pages.HeaderPageCountOutdated,
            HasGidConflict = gidConflict,
            IsIncomplete = isIncomplete,
            HasEhviewer = hasEh,
            HasThumb = hasThumb,
            CoverPath = coverPath,
            CoverKind = coverKind,
            CoverSource = coverSource,
            Languages = languages,
            Tags = tags,
            LastWriteUtc = folderWriteUtc,
            ExtensionSummary = pages.ExtensionSummary,
            ZeroByteFiles = pages.ZeroByteFiles,
            HeaderError = header?.IsValid == true ? null : headerError ?? header?.Error,
        };
    }

    private static GalleryInfo FromCache(GalleryCacheEntry entry, string folderPath)
    {
        return new GalleryInfo
        {
            StableId = string.Empty,
            Gid = entry.DirectoryGid ?? entry.HeaderGid,
            Token = entry.Token,
            DirectoryGid = entry.DirectoryGid,
            HeaderGid = entry.HeaderGid,
            Title = entry.Title,
            FolderName = entry.FolderName,
            FolderPath = folderPath,
            PageCount = entry.PageCount,
            MaxPageNumber = entry.MaxPageNumber,
            ExpectedPages = entry.ExpectedPages,
            MissingRanges = entry.MissingRanges,
            HasHeaderPageCountOutdated = entry.HeaderPageCountOutdated,
            HasGidConflict = entry.HasGidConflict,
            IsIncomplete = entry.PageCount == 0 || entry.MissingRanges.Count > 0 ||
                           entry.HeaderPageCountOutdated || entry.ZeroByteFiles > 0,
            HasEhviewer = entry.HasEhviewer,
            HasThumb = entry.HasThumb,
            CoverPath = entry.CoverPath,
            CoverKind = entry.CoverKind,
            CoverSource = entry.CoverSource,
            Languages = entry.Languages,
            Tags = entry.Tags,
            LastWriteUtc = entry.FolderMtimeTicks > 0
                ? new DateTime(entry.FolderMtimeTicks, DateTimeKind.Utc)
                : Directory.GetLastWriteTimeUtc(folderPath),
            ExtensionSummary = entry.ExtensionSummary,
            ZeroByteFiles = entry.ZeroByteFiles,
            HeaderError = entry.HeaderError,
        };
    }

    private static GalleryCacheEntry ToCacheEntry(GalleryInfo gallery, GallerySnapshot snapshot)
    {
        return new GalleryCacheEntry
        {
            FolderName = gallery.FolderName,
            FolderMtimeTicks = gallery.LastWriteUtc.Ticks,
            EhviewerLength = snapshot.EhLength,
            EhviewerMtimeTicks = snapshot.EhMtimeTicks,
            ThumbLength = snapshot.ThumbLength,
            ThumbMtimeTicks = snapshot.ThumbMtimeTicks,
            PageFileCount = snapshot.PageFileCount,
            PageLengthSum = snapshot.PageLengthSum,
            PageMtimeMaxTicks = snapshot.PageMtimeMaxTicks,
            DirectoryGid = gallery.DirectoryGid,
            HeaderGid = gallery.HeaderGid,
            Token = gallery.Token,
            Title = gallery.Title,
            PageCount = gallery.PageCount,
            MaxPageNumber = gallery.MaxPageNumber,
            ExpectedPages = gallery.ExpectedPages,
            HeaderPageCountOutdated = gallery.HasHeaderPageCountOutdated,
            HasGidConflict = gallery.HasGidConflict,
            MissingRanges = gallery.MissingRanges.ToList(),
            HasEhviewer = gallery.HasEhviewer,
            HasThumb = gallery.HasThumb,
            CoverPath = gallery.CoverPath,
            CoverKind = gallery.CoverKind,
            CoverSource = gallery.CoverSource,
            Languages = gallery.Languages,
            Tags = gallery.Tags,
            ExtensionSummary = gallery.ExtensionSummary,
            ZeroByteFiles = gallery.ZeroByteFiles,
            HeaderError = gallery.HeaderError,
        };
    }

    /// <summary>
    /// Materializes a gallery list from the on-disk cache without touching the
    /// library. Used for the instant first paint before the background scan.
    /// </summary>
    public static List<GalleryInfo> FromCacheOnly(
        LibraryCache cache,
        string rootPath,
        string? sourceId = null,
        string? sourceName = null,
        bool qualifyStableId = false)
    {
        var galleries = new List<GalleryInfo>(cache.Entries.Count);
        foreach (var pair in cache.Entries)
        {
            galleries.Add(FromCache(pair.Value, Path.Combine(rootPath, pair.Key)));
        }

        AssignStableIds(galleries, sourceId, sourceName, qualifyStableId);
        return galleries;
    }

    private static void AssignStableIds(
        List<GalleryInfo> galleries,
        string? sourceId = null,
        string? sourceName = null,
        bool qualifyStableId = false)
    {
        var gidCounts = galleries
            .Where(g => g.Gid.HasValue)
            .GroupBy(g => g.Gid!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (GalleryInfo gallery in galleries)
        {
            int count = gallery.Gid.HasValue ? gidCounts[gallery.Gid.Value] : 0;
            gallery.IsDuplicateGid = count > 1;

            // Cross-source identity. The two shapes match the legacy StableId for
            // the common cases, so progress recorded by older builds still applies.
            gallery.WorkId = gallery.Gid.HasValue
                ? gallery.Gid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "dir@" + StableIdFactory.PathHash(gallery.FolderPath);

            string stableId = StableIdFactory.ForGallery(
                gallery.Gid,
                count,
                gallery.FolderPath,
                gallery.HasGidConflict);
            gallery.StableId = qualifyStableId && !string.IsNullOrEmpty(sourceId)
                ? stableId + "@" + sourceId
                : stableId;

            if (!string.IsNullOrEmpty(sourceId)) gallery.SourceId = sourceId;
            if (!string.IsNullOrEmpty(sourceName)) gallery.SourceName = sourceName;
        }
    }

    private static FileInfo? TryReadFileInfo(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Aggregated cheap-to-compute fingerprint. Comparing it with the cached copy
    /// catches in-place rewrites (0-byte page filled later, truncated file) that a
    /// directory timestamp alone cannot see.
    /// </summary>
    private readonly record struct GallerySnapshot(
        long FolderMtimeTicks,
        long EhLength,
        long EhMtimeTicks,
        long ThumbLength,
        long ThumbMtimeTicks,
        int PageFileCount,
        long PageLengthSum,
        long PageMtimeMaxTicks,
        int MaxPageNumber)
    {
        public static GallerySnapshot Create(
            DateTime folderWriteUtc,
            FileInfo? ehFile,
            FileInfo? thumbFile,
            IReadOnlyList<PageCandidate> files)
        {
            long sum = 0;
            long mtimeMax = 0;
            int maxPage = 0;
            foreach (PageCandidate file in files)
            {
                sum += file.Length;
                if (file.MtimeTicks > mtimeMax) mtimeMax = file.MtimeTicks;
                if (PageFileParser.TryParsePageName(file.FileName, out int number, out _) && number > maxPage)
                {
                    maxPage = number;
                }
            }

            return new GallerySnapshot(
                folderWriteUtc.Ticks,
                ehFile?.Length ?? 0,
                ehFile?.LastWriteTimeUtc.Ticks ?? 0,
                thumbFile?.Length ?? 0,
                thumbFile?.LastWriteTimeUtc.Ticks ?? 0,
                files.Count,
                sum,
                mtimeMax,
                maxPage);
        }

        public bool Matches(GalleryCacheEntry entry) =>
            entry.FolderMtimeTicks == FolderMtimeTicks &&
            entry.EhviewerLength == EhLength &&
            entry.EhviewerMtimeTicks == EhMtimeTicks &&
            entry.ThumbLength == ThumbLength &&
            entry.ThumbMtimeTicks == ThumbMtimeTicks &&
            entry.PageFileCount == PageFileCount &&
            entry.PageLengthSum == PageLengthSum &&
            entry.PageMtimeMaxTicks == PageMtimeMaxTicks &&
            entry.MaxPageNumber == MaxPageNumber;
    }
}

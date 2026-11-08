using SkiaSharp;
using System.Diagnostics;
using EhViewerReader.Core.Format;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Scanning;
using EhViewerReader.Core.Storage;

namespace EhViewerReader.Core.Tests;

public class LibraryScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EhReaderScan-" + Guid.NewGuid().ToString("N"));

    public LibraryScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void CreateGallery(string folder, long gid, int pages, int? expected = null, bool thumb = true)
    {
        string path = Path.Combine(_root, folder);
        Directory.CreateDirectory(path);
        string eh = $"VERSION2\n00000000\n{gid}\ntoken\n1\n{expected ?? pages}\n1\n{expected ?? pages}\n";
        File.WriteAllText(Path.Combine(path, ".ehviewer"), eh);
        if (thumb) File.WriteAllBytes(Path.Combine(path, ".thumb"), [0xFF, 0xD8, 0xFF, 0xE0]);
        for (int i = 1; i <= pages; i++)
        {
            File.WriteAllBytes(Path.Combine(path, $"{i:00000000}.jpg"), [0xFF, 0xD8, 0xFF, 0xE0, (byte)i]);
        }
    }

    [Fact]
    public void ScansAndUsesCache()
    {
        CreateGallery("100-Test Gallery", 100, 3);
        CreateGallery("101-Second", 101, 2, expected: 4);
        Directory.CreateDirectory(Path.Combine(_root, ".stfolder"));
        Directory.CreateDirectory(Path.Combine(_root, new string('a', 64) + ".tmp"));

        var scanner = new LibraryScanner();
        var first = scanner.Scan(_root, null);

        Assert.Equal(2, first.Galleries.Count);
        Assert.Equal(0, first.CacheHitCount);
        GalleryInfo gallery = Assert.Single(first.Galleries, g => g.Gid == 100);
        Assert.Equal(3, gallery.PageCount);
        Assert.Equal("Test Gallery", gallery.Title);
        Assert.Equal("100", gallery.StableId);
        Assert.NotNull(gallery.CoverPath);
        GalleryInfo tail = Assert.Single(first.Galleries, g => g.Gid == 101);
        Assert.True(tail.HasMissingPages);
        Assert.Equal(new PageRange(3, 4), tail.MissingRanges[0]);

        var cache = first.Cache;
        Console.WriteLine("cache entries: " + string.Join(" | ", cache.Entries.Select(e => $"{e.Key}={e.Value.FolderMtimeTicks}")));
        Console.WriteLine("dir mtimes: " + string.Join(" | ", new DirectoryInfo(_root).EnumerateDirectories()
            .Select(d => $"{d.Name}={d.LastWriteTimeUtc.Ticks}")));

        var second = scanner.Scan(_root, cache);
        Assert.Equal(2, second.CacheHitCount);
        Assert.Equal(0, second.ScannedDirectoryCount);
    }

    [Fact]
    public void HandlesDuplicateAndMissingGid()
    {
        CreateGallery("200-A", 200, 2);
        CreateGallery("200-B", 200, 2);
        CreateGallery("NoGid", 0, 2);
        // Replace the no-gid header gid with a real one; folder name is intentionally odd.
        File.WriteAllText(Path.Combine(_root, "NoGid", ".ehviewer"), "VERSION2\n00000000\n777\ntoken\n1\n2\n1\n2\n");

        var result = new LibraryScanner().Scan(_root, null);
        var duplicates = result.Galleries.Where(g => g.Gid == 200).ToArray();
        Assert.Equal(2, duplicates.Length);
        Assert.All(duplicates, g => Assert.True(g.IsDuplicateGid));
        Assert.Equal(2, duplicates.Select(g => g.StableId).Distinct().Count());
        Assert.All(duplicates, g => Assert.Contains('@', g.StableId));

        var noGid = Assert.Single(result.Galleries, g => g.FolderName == "NoGid");
        Assert.Equal(777, noGid.Gid);
        Assert.StartsWith("777", noGid.StableId);
    }


    [Fact]
    public void CacheRootComparisonIgnoresSeparatorStyle()
    {
        string root = Path.Combine(_root, "library");
        Directory.CreateDirectory(root);
        var paths = new AppPaths(Path.Combine(_root, "appdata"));
        paths.EnsureCreated();
        var store = new LibraryCacheStore(paths);
        var cache = new LibraryCache
        {
            RootPath = Path.GetFullPath(root),
            Entries = new Dictionary<string, GalleryCacheEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["100-A"] = new GalleryCacheEntry { FolderName = "100-A", PageCount = 2 },
            },
        };
        Assert.True(store.Save(cache, out string? error), error);

        LibraryCache loaded = store.Load(root.Replace('\\', '/'), out _);
        Assert.Single(loaded.Entries);
    }

    [Fact]
    public void IncludesEmptyGallery()
    {
        Directory.CreateDirectory(Path.Combine(_root, "300-Empty"));
        var result = new LibraryScanner().Scan(_root, null);
        GalleryInfo empty = Assert.Single(result.Galleries);
        Assert.Equal(0, empty.PageCount);
        Assert.True(empty.IsIncomplete);
        Assert.Equal(0, empty.PageCount);
    }

    [Fact]
    public void ZeroBytePageMarksGalleryIncomplete()
    {
        CreateGallery("400-ZeroByte", 400, 3);
        File.WriteAllBytes(Path.Combine(_root, "400-ZeroByte", "00000002.jpg"), []);

        var result = new LibraryScanner().Scan(_root, null);
        GalleryInfo gallery = Assert.Single(result.Galleries);

        Assert.Equal(1, gallery.ZeroByteFiles);
        Assert.True(gallery.IsIncomplete);
        Assert.True(gallery.PageCount == 3);
    }

    [Fact]
    public void InPlacePageRewriteInvalidatesCache()
    {
        CreateGallery("500-Rewrite", 500, 3);
        var scanner = new LibraryScanner();
        var first = scanner.Scan(_root, null);

        string page = Path.Combine(_root, "500-Rewrite", "00000002.jpg");
        File.WriteAllBytes(page, new byte[123]);

        var second = scanner.Scan(_root, first.Cache);
        Assert.Equal(1, second.ScannedDirectoryCount);
        Assert.Equal(0, second.CacheHitCount);
    }
}

public class RealLibraryIntegrationTests
{
    private static string? Root => Environment.GetEnvironmentVariable("EHVIEWER_TEST_ROOT");

    private static bool HasRoot =>
        !string.IsNullOrWhiteSpace(Root) && Directory.Exists(Root);

    [Fact]
    [Trait("Category", "Integration")]
    public void ScansRealLibraryAndKeepsCacheWarm()
    {
        if (!HasRoot) return;
        string root = Root!;

        var scanner = new LibraryScanner();
        var cold = scanner.Scan(root, null, forceRefresh: true);
        Assert.True(cold.Galleries.Count >= 6000, $"gallery count {cold.Galleries.Count}");
        int pages = cold.Galleries.Sum(g => g.PageCount);
        Assert.True(pages >= 240_000, $"page count {pages}");
        Assert.True(cold.Galleries.Count(g => g.HasMissingPages) >= 1);
        Assert.True(cold.Galleries.Count(g => g.ZeroByteFiles > 0) >= 1);
        Assert.True(cold.Galleries.Count(g => g.IsDuplicateGid) >= 1);
        Assert.True(cold.Galleries.Count(g => g.DirectoryGid is null && g.Gid.HasValue) >= 1);
        Assert.True(cold.Galleries.Count(g => !g.HasThumb) >= 1);
        Assert.True(cold.Galleries.Count(g => g.ExtensionSummary.Contains("webp")) >= 1);
        GalleryInfo largest = cold.Galleries.MaxBy(g => g.PageCount)!;
        Assert.True(largest.PageCount >= 1000, $"largest {largest.PageCount}");

        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "EhReaderRealCache-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        Assert.True(new LibraryCacheStore(paths).Save(cold.Cache, out string? saveError), saveError);

        LibraryCache loaded = new LibraryCacheStore(paths).Load(root, out _);
        ScanResult warm = scanner.Scan(root, loaded);
        Assert.True(warm.CacheHitCount >= cold.Galleries.Count - 10,
            $"cache hits {warm.CacheHitCount}/{cold.Galleries.Count}, scanned {warm.ScannedDirectoryCount}, " +
            $"warm {warm.Elapsed}, cold {cold.Elapsed}");
        Assert.True(warm.Elapsed < cold.Elapsed + TimeSpan.FromSeconds(5),
            $"warm {warm.Elapsed} (hits {warm.CacheHitCount}, scanned {warm.ScannedDirectoryCount}) " +
            $"vs cold {cold.Elapsed} (hits {cold.CacheHitCount})");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void DecodesRealPageHeadersWithoutAllocatingPixels()
    {
        if (!HasRoot) return;
        string root = Root!;
        var scan = new LibraryScanner().Scan(root, null, forceRefresh: false);
        GalleryInfo largest = scan.Galleries.MaxBy(g => g.PageCount)!;
        PageScanResult pages = PageFileParser.Scan(largest.FolderPath, null, out string? error);
        Assert.Null(error);
        Assert.True(pages.PageCount >= 1000);

        foreach (int index in new[] { 0, pages.PageCount / 2, pages.PageCount - 1 })
        {
            GalleryPage page = pages.Pages[index];
            PageCandidate? candidate = page.Candidates.FirstOrDefault(c => c.Length > 0);
            Assert.NotNull(candidate);
            using var stream = new FileStream(candidate!.FilePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.SequentialScan);
            using SKCodec? codec = SKCodec.Create(stream, out SKCodecResult result);
            Assert.Equal(SKCodecResult.Success, result);
            Assert.True(codec!.Info.Width > 0 && codec.Info.Height > 0);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void ZeroBytePageIsDetectedAndUndecodable()
    {
        if (!HasRoot) return;
        var scan = new LibraryScanner().Scan(Root!, null, forceRefresh: false);
        GalleryInfo? gallery = scan.Galleries.FirstOrDefault(g => g.ZeroByteFiles > 0);
        if (gallery is null) return;

        PageScanResult pages = PageFileParser.Scan(gallery.FolderPath, null, out _);
        GalleryPage? broken = pages.Pages.FirstOrDefault(p => p.Candidates.All(c => c.Length == 0));
        if (broken is null) return;

        Assert.True(gallery.IsIncomplete);
        Assert.All(broken.Candidates, c => Assert.Equal(0, c.Length));
    }
}

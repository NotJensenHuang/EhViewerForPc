using EhViewerReader.Core.Models;
using EhViewerReader.Core.Scanning;
using EhViewerReader.Core.Storage;

namespace EhViewerReader.Core.Tests;

public class LibraryMergerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EhReaderMerge-" + Guid.NewGuid().ToString("N"));

    public LibraryMergerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private GalleryInfo Gallery(
        long? gid,
        string folder,
        string sourceId,
        int pages,
        bool incomplete = false,
        bool thumb = true,
        DateTime? modified = null)
    {
        string path = Path.Combine(_root, sourceId, folder);
        Directory.CreateDirectory(path);
        string pathKey = "dir@" + EhViewerReader.Core.Format.StableIdFactory.PathHash(path);
        return new GalleryInfo
        {
            StableId = gid is null ? pathKey : gid + "@" + sourceId,
            WorkId = gid is null ? pathKey : gid.Value.ToString(),
            SourceId = sourceId,
            SourceName = sourceId,
            Gid = gid,
            Title = folder,
            FolderName = folder,
            FolderPath = path,
            PageCount = pages,
            IsIncomplete = incomplete,
            HasThumb = thumb,
            LastWriteUtc = modified ?? DateTime.UtcNow,
        };
    }

    [Fact]
    public void MergesCopiesAcrossSources()
    {
        var galleries = new[]
        {
            Gallery(1000, "1000-A", "backup", 4, incomplete: true),
            Gallery(1000, "1000-A", "primary", 12),
            Gallery(1001, "1001-B", "primary", 3),
        };

        List<GalleryGroup> groups = LibraryMerger.Group(galleries);

        Assert.Equal(2, groups.Count);
        GalleryGroup merged = groups[0];
        Assert.True(merged.IsMultiVersion);
        Assert.Equal(2, merged.Versions.Count);
        Assert.Equal(2, merged.SourceCount);
        Assert.Equal(12, merged.Primary.PageCount);
        Assert.Equal("primary", merged.Primary.SourceId);
        Assert.Equal("1000", merged.WorkId);
    }

    [Fact]
    public void KeepsDuplicatesInsideOneSourceSeparate()
    {
        var galleries = new[]
        {
            Gallery(2000, "2000-[Chinese]", "only", 5),
            Gallery(2000, "2000-[English]", "only", 6),
        };

        List<GalleryGroup> groups = LibraryMerger.Group(galleries);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.False(g.IsMultiVersion));
    }

    [Fact]
    public void ExplicitPreferenceOverridesPageCount()
    {
        var galleries = new[]
        {
            Gallery(3000, "3000-big", "fast", 40),
            Gallery(3000, "3000-small", "archive", 6),
        };

        List<GalleryGroup> groups = LibraryMerger.Group(galleries, workId => workId == "3000" ? "archive" : null);

        GalleryGroup group = Assert.Single(groups);
        Assert.Equal("archive", group.Primary.SourceId);
        Assert.Equal(6, group.Primary.PageCount);
    }

    [Fact]
    public void CompleteCopyWinsOverLargerIncompleteCopy()
    {
        var galleries = new[]
        {
            Gallery(4000, "4000-broken", "a", 100, incomplete: true),
            Gallery(4000, "4000-ok", "b", 20),
        };

        GalleryGroup group = Assert.Single(LibraryMerger.Group(galleries));
        Assert.Equal("b", group.Primary.SourceId);
    }

    [Fact]
    public void SourcesAreNotMergedByTitleAlone()
    {
        // No gid: WorkId falls back to the path hash, so two folders that happen
        // to share a title stay separate cards.
        var galleries = new[]
        {
            Gallery(null, "无 GID 样本", "a", 3),
            Gallery(null, "无 GID 样本", "b", 3),
        };

        List<GalleryGroup> groups = LibraryMerger.Group(galleries);
        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.False(g.IsMultiVersion));
    }

    [Fact]
    public void ScannerAssignsWorkIdAndQualifiedStableIds()
    {
        string sourceA = Path.Combine(_root, "a");
        string sourceB = Path.Combine(_root, "b");
        foreach (string source in new[] { sourceA, sourceB })
        {
            string folder = Path.Combine(source, "5000-Sample");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, ".ehviewer"), "VERSION2\n00000000\n5000\ntoken\n1\n2\n2\n2\n");
            File.WriteAllBytes(Path.Combine(folder, "00000001.jpg"), [0xFF, 0xD8, 0xFF]);
            File.WriteAllBytes(Path.Combine(folder, "00000002.jpg"), [0xFF, 0xD8, 0xFF]);
        }

        ScanResult a = new LibraryScanner().Scan(sourceA, null, sourceId: "sa", sourceName: "A", qualifyStableId: true);
        ScanResult b = new LibraryScanner().Scan(sourceB, null, sourceId: "sb", sourceName: "B", qualifyStableId: true);

        GalleryInfo ga = Assert.Single(a.Galleries);
        GalleryInfo gb = Assert.Single(b.Galleries);
        Assert.Equal("5000", ga.WorkId);
        Assert.Equal(ga.WorkId, gb.WorkId);
        Assert.NotEqual(ga.StableId, gb.StableId);
        Assert.Equal("sa", ga.SourceId);
        Assert.Equal("A", ga.SourceName);

        GalleryGroup group = Assert.Single(LibraryMerger.Group([ga, gb]));
        Assert.True(group.IsMultiVersion);
        Assert.Equal(2, group.SourceCount);
    }
}

public class MultiSourceSettingsTests
{
    [Fact]
    public void MigratesLegacyRootIntoSource()
    {
        string root = Path.Combine(Path.GetTempPath(), "EhReaderSettings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new AppSettings { SchemaVersion = 1, LibraryRoot = root };
            settings.Normalize();

            LibrarySource source = Assert.Single(settings.Sources);
            Assert.Equal(Path.GetFullPath(root), source.RootPath);
            Assert.True(source.Enabled);
            Assert.StartsWith("src-", source.Id);

            // Saving twice must not duplicate or regenerate the id.
            string id = source.Id;
            settings.Normalize();
            Assert.Equal(id, Assert.Single(settings.Sources).Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeduplicatesSourcesByPath()
    {
        var settings = new AppSettings();
        settings.Sources.Add(new LibrarySource { Id = "a", RootPath = @"C:\libraries\eh" });
        settings.Sources.Add(new LibrarySource { Id = "b", RootPath = @"C:\libraries\eh\" });
        settings.Sources.Add(new LibrarySource { Id = "a", RootPath = @"C:\libraries\other" });
        settings.Normalize();

        Assert.Equal(2, settings.Sources.Count);
        Assert.Equal(2, settings.Sources.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void RemovingSourcePrunesPreferences()
    {
        var settings = new AppSettings();
        LibrarySource source = settings.AddSource(@"C:\libraries\eh");
        settings.PreferredSourceByWork["1234"] = source.Id;
        settings.PreferredSourceByWork["5678"] = "other";

        settings.RemoveSource(source.Id);

        Assert.Empty(settings.Sources);
        Assert.False(settings.PreferredSourceByWork.ContainsKey("1234"));
        Assert.True(settings.PreferredSourceByWork.ContainsKey("5678"));
    }

    [Fact]
    public void CacheFilesAreIsolatedPerSource()
    {
        string root = Path.Combine(Path.GetTempPath(), "EhReaderCache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(Path.Combine(root, "appdata"));
            paths.EnsureCreated();
            var store = new LibraryCacheStore(paths);

            var first = new LibraryCache
            {
                RootPath = Path.Combine(root, "lib1"),
                Entries = new Dictionary<string, GalleryCacheEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    ["1-One"] = new GalleryCacheEntry { FolderName = "1-One", PageCount = 1 },
                },
            };
            var second = new LibraryCache
            {
                RootPath = Path.Combine(root, "lib2"),
                Entries = new Dictionary<string, GalleryCacheEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    ["2-Two"] = new GalleryCacheEntry { FolderName = "2-Two", PageCount = 2 },
                    ["3-Three"] = new GalleryCacheEntry { FolderName = "3-Three", PageCount = 3 },
                },
            };

            Assert.True(store.Save("src-a", first, out string? e1), e1);
            Assert.True(store.Save("src-b", second, out string? e2), e2);

            LibraryCache loadedA = store.Load("src-a", Path.Combine(root, "lib1"), out _);
            LibraryCache loadedB = store.Load("src-b", Path.Combine(root, "lib2"), out _);
            Assert.Single(loadedA.Entries);
            Assert.Equal(2, loadedB.Entries.Count);

            store.Delete("src-a");
            Assert.Empty(store.Load("src-a", Path.Combine(root, "lib1"), out _).Entries);
            Assert.Equal(2, store.Load("src-b", Path.Combine(root, "lib2"), out _).Entries.Count);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}

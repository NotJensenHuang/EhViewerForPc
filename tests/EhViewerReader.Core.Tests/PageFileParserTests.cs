using EhViewerReader.Core.Format;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Storage;

namespace EhViewerReader.Core.Tests;

public class PageFileParserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EhReaderTests-" + Guid.NewGuid().ToString("N"));

    public PageFileParserTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Touch(string name, int length = 10)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[length]);
    }

    [Fact]
    public void ParsesPagesAndPrefersNonEmptyCandidate()
    {
        Touch("00000001.jpg", 100);
        Touch("00000002.jpg", 0);
        Touch("00000002.webp", 200);
        Touch("00000003.PNG", 50);
        Touch("00000003.jpg", 50);
        Touch(".thumb", 20);
        Touch(".ehviewer", 20);
        Touch("notes.txt", 20);

        var result = PageFileParser.Scan(_root, null, out string? error);

        Assert.Null(error);
        Assert.Equal(3, result.PageCount);
        Assert.Equal(2, result.Pages[1].Number);
        Assert.Equal(".webp", result.Pages[1].Primary!.Extension);
        Assert.Equal(".jpg", result.Pages[2].Primary!.Extension);
        Assert.Empty(result.MissingRanges);
    }

    [Fact]
    public void DetectsTailMissingPagesFromHeader()
    {
        Touch("00000001.jpg");
        Touch("00000002.jpg");
        var header = new SpiderHeader { HasPages = true, Pages = 4, HasGid = true, Gid = 1, IsValid = true };

        var result = PageFileParser.Scan(_root, header, out _);

        Assert.Equal(2, result.PageCount);
        Assert.Single(result.MissingRanges);
        Assert.Equal(new PageRange(3, 4), result.MissingRanges[0]);
        Assert.True(result.HasMissingPages);
    }

    [Fact]
    public void HandlesHeaderOutdated()
    {
        Touch("00000001.jpg");
        Touch("00000002.jpg");
        Touch("00000003.jpg");
        var header = new SpiderHeader { HasPages = true, Pages = 2, HasGid = true, Gid = 1, IsValid = true };

        var result = PageFileParser.Scan(_root, header, out _);

        Assert.Equal(3, result.PageCount);
        Assert.True(result.HeaderPageCountOutdated);
        Assert.Empty(result.MissingRanges);
    }

    [Fact]
    public void ParsesPageNames()
    {
        Assert.True(PageFileParser.TryParsePageName("00000001.jpg", out int number, out string ext));
        Assert.Equal(1, number);
        Assert.Equal(".jpg", ext);
        Assert.False(PageFileParser.TryParsePageName("0000001.jpg", out _, out _));
        Assert.False(PageFileParser.TryParsePageName("00000001.txt", out _, out _));
        Assert.False(PageFileParser.TryParsePageName("00000000.jpg", out _, out _));
    }
}

public class SignatureTests
{
    [Fact]
    public void DetectsKnownFormats()
    {
        Assert.Equal(ImageKind.Jpeg, ImageSignature.Detect([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal(ImageKind.Png, ImageSignature.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.Equal(ImageKind.Gif, ImageSignature.Detect("GIF89a"u8));
        Assert.Equal(ImageKind.Webp, ImageSignature.Detect("RIFF1234WEBP"u8));
        Assert.Equal(ImageKind.Unknown, ImageSignature.Detect("RIFF1234WAVE"u8));
        Assert.Equal(ImageKind.Unknown, ImageSignature.Detect(ReadOnlySpan<byte>.Empty));
    }
}

public class GalleryNameTests
{
    [Theory]
    [InlineData("1002892-Title", 1002892L, "Title")]
    [InlineData("123-(C90) foo [Chinese]", 123L, "(C90) foo [Chinese]")]
    [InlineData("NoGid", null, "NoGid")]
    public void ParsesFolderName(string name, long? gid, string title)
    {
        var (actualGid, actualTitle) = GalleryNameParser.Parse(name);
        Assert.Equal(gid, actualGid);
        Assert.Equal(title, actualTitle);
    }

    [Fact]
    public void StableIdIsPathSensitiveForDuplicates()
    {
        string a = StableIdFactory.ForGallery(42, 2, @"C:\a", forcePathSuffix: false);
        string b = StableIdFactory.ForGallery(42, 2, @"C:\b", forcePathSuffix: false);
        Assert.NotEqual(a, b);
        Assert.StartsWith("42@", a);
        Assert.StartsWith("42@", b);
        Assert.Equal("42", StableIdFactory.ForGallery(42, 1, @"C:\a", forcePathSuffix: false));
    }
}

public class StorageTests
{
    [Fact]
    public void ProgressRoundTrips()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "EhReaderStore-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new ProgressStore(paths);
        store.RecordRead("42", 3, 2, 0.5, 10, completed: false);
        store.ToggleFavorite("42");
        Assert.True(store.Save(out string? error));
        Assert.Null(error);

        var reloaded = new ProgressStore(paths);
        var progress = reloaded.Get("42");
        Assert.NotNull(progress);
        Assert.Equal(3, progress.PageNumber);
        Assert.Equal(2, progress.PageIndex);
        Assert.True(progress.Favorite);
    }

    [Fact]
    public void CacheRoundTripsPageRanges()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "EhReaderCache-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new LibraryCacheStore(paths);
        var cache = new LibraryCache
        {
            RootPath = @"D:\lib",
            Entries = new Dictionary<string, GalleryCacheEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["1-Title"] = new GalleryCacheEntry
                {
                    FolderName = "1-Title",
                    PageCount = 10,
                    MissingRanges = [new PageRange(3, 4)],
                },
            },
        };
        Assert.True(store.Save(cache, out _));
        var loaded = store.Load(@"D:\lib", out string? error);
        Assert.Null(error);
        Assert.Equal(10, loaded.Entries["1-Title"].PageCount);
        Assert.Equal(new PageRange(3, 4), loaded.Entries["1-Title"].MissingRanges[0]);
    }
}

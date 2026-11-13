using EhViewerReader.Core.Download;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Online;
using EhViewerReader.Core.Scanning;
using EhViewerReader.Services;
using EhViewerReader.ViewModels;

namespace EhViewerReader.Core.Tests;

/// <summary>Covers the online-reader/library repairs: on-demand page index,
/// download-folder reading, favourite note handling and download projection.</summary>
public sealed class OnlineReaderRepairTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EhReaderFix-" + Guid.NewGuid().ToString("N"));

    public OnlineReaderRepairTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static EhPageList PageList(int groupIndex, int pageSize, bool hasMore)
    {
        var list = new EhPageList { HasMore = hasMore };
        for (int i = 0; i < pageSize; i++)
        {
            list.Pages.Add(new EhPageRef
            {
                Hash = "h",
                Gid = 7,
                Page = groupIndex * pageSize + i + 1,
                PageUrl = "https://example.invalid/s/h/7-" + (groupIndex * pageSize + i + 1),
                Width = 100,
                Height = 200,
            });
        }

        return list;
    }

    [Fact]
    public async Task PageIndexFetchesOnlyThePaginationThatHoldsThePage()
    {
        var requested = new List<int>();
        using var index = new OnlinePageIndex(60, (group, _) =>
        {
            lock (requested) requested.Add(group);
            return Task.FromResult(PageList(group, 20, hasMore: group < 2));
        });

        // The first resolution must learn the site's preview page size from the
        // first pagination; afterwards only the containing pagination is read.
        await index.GetPageAsync(1, CancellationToken.None);
        requested.Clear();

        EhPageRef? page = await index.GetPageAsync(45, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal(45, page!.Page);
        Assert.Equal([2], requested);
        Assert.Equal(20, index.PageSize);
        Assert.Equal(60, index.PageCount);
    }

    [Fact]
    public async Task ConcurrentPagesShareOnePaginationRequest()
    {
        int calls = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var index = new OnlinePageIndex(20, async (group, _) =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return PageList(group, 20, hasMore: false);
        });

        Task<EhPageRef?> first = index.GetPageAsync(1, CancellationToken.None);
        Task<EhPageRef?> second = index.GetPageAsync(17, CancellationToken.None);
        gate.SetResult(true);
        await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.NotNull(await first);
        Assert.NotNull(await second);
        Assert.True(index.IsComplete);
    }

    [Fact]
    public async Task CancellingOneWaitDoesNotCancelTheSharedTransfer()
    {
        var gate = new TaskCompletionSource<EhPageList>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var index = new OnlinePageIndex(20, (_, _) => gate.Task);
        using var cancellation = new CancellationTokenSource();

        Task<EhPageRef?> cancelled = index.GetPageAsync(1, cancellation.Token);
        Task<EhPageRef?> survivor = index.GetPageAsync(2, CancellationToken.None);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        gate.SetResult(PageList(0, 20, hasMore: false));
        Assert.NotNull(await survivor);
    }

    [Fact]
    public async Task PageBeyondTheLastOneReturnsNullWithoutScanningEveryPagination()
    {
        int calls = 0;
        using var index = new OnlinePageIndex(10, (group, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(PageList(group, 10, hasMore: false));
        });

        Assert.NotNull(await index.GetPageAsync(10, CancellationToken.None));
        Assert.Null(await index.GetPageAsync(11, CancellationToken.None));
        Assert.True(calls <= 2);
    }

    [Fact]
    public void DownloadReaderReadsTheTaskFolderEvenWhenTheLibraryHasNoCopy()
    {
        string folder = Path.Combine(_root, "task-folder");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "00000001.jpg"), [0xFF, 0xD8, 0xFF, 0xE0]);
        File.WriteAllBytes(Path.Combine(folder, "00000002.jpg"), [0xFF, 0xD8, 0xFF, 0xE0]);

        var task = new DownloadTask
        {
            Gid = 4242,
            Token = "tok",
            Title = "Partial task",
            FolderPath = folder,
            PageCount = 5,
        };

        GalleryInfo? info = DownloadReader.TryBuildGalleryInfo(task, out string? error);

        Assert.Null(error);
        Assert.NotNull(info);
        Assert.Equal("4242", info!.WorkId);
        Assert.StartsWith("dl-", info.StableId, StringComparison.Ordinal);
        Assert.Equal(2, info.PageCount);
        Assert.Equal(5, info.ExpectedPages);
        Assert.True(info.IsIncomplete);
        Assert.Equal("下载任务", info.SourceName);
    }

    [Fact]
    public void DownloadReaderExplainsAnEmptyOrMissingFolder()
    {
        string empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        var task = new DownloadTask { Gid = 1, FolderPath = empty, PageCount = 3 };

        Assert.Null(DownloadReader.TryBuildGalleryInfo(task, out string? error));
        Assert.Contains("没有可阅读", error);

        task.FolderPath = Path.Combine(_root, "missing");
        Assert.Null(DownloadReader.TryBuildGalleryInfo(task, out error));
        Assert.Contains("不存在", error);
    }

    [Fact]
    public void FavoriteNoteIsParsedWithoutLosingEntities()
    {
        const string html = "<html><body><textarea name=\"favnote\">keep &amp; this\nnote</textarea></body></html>";
        Assert.Equal("keep & this note", EhParser.ParseFavoriteNote(html));
        Assert.Equal(string.Empty, EhParser.ParseFavoriteNote("<html><body>no form</body></html>"));
    }

    [Fact]
    public void OnlineCacheSettingsDefaultAndClamp()
    {
        var settings = new AppSettings();
        Assert.Equal(512, settings.OnlineCacheMegabytes);
        Assert.Equal(0, settings.OnlineCacheDays);

        settings.OnlineCacheMegabytes = 1;
        settings.OnlineCacheDays = 900;
        settings.Normalize();
        Assert.Equal(128, settings.OnlineCacheMegabytes);
        Assert.Equal(90, settings.OnlineCacheDays);

        settings.OnlineCacheDays = -5;
        settings.Normalize();
        Assert.Equal(0, settings.OnlineCacheDays);
    }

    [Fact]
    public void CacheDefaultsMigrateButExplicitValuesSurvive()
    {
        var oldDefaults = new AppSettings { SchemaVersion = 5, OnlineCacheMegabytes = 2048, OnlineCacheDays = 14 };
        oldDefaults.Normalize();
        Assert.Equal(512, oldDefaults.OnlineCacheMegabytes);
        Assert.Equal(0, oldDefaults.OnlineCacheDays);
        Assert.Equal(7, oldDefaults.SchemaVersion);
        Assert.False(oldDefaults.ImageSearch.Enabled);

        var custom = new AppSettings { SchemaVersion = 5, OnlineCacheMegabytes = 4096, OnlineCacheDays = 30 };
        custom.Normalize();
        Assert.Equal(4096, custom.OnlineCacheMegabytes);
        Assert.Equal(30, custom.OnlineCacheDays);
    }

    [Fact]
    public void DuplicateFilterValueAppearsLastAndKeepsOldNumbers()
    {
        Assert.Equal(6, (int)LibraryFilter.Duplicates);
        Assert.Equal(0, (int)LibraryFilter.All);
        Assert.Equal(5, (int)LibraryFilter.Incomplete);
    }

    [Fact]
    public void MultiVersionGroupIsDetectedForTheDuplicateFilter()
    {
        GalleryInfo first = Info("a", "42", Path.Combine(_root, "a"));
        GalleryInfo second = Info("b", "42", Path.Combine(_root, "b"));
        GalleryGroup group = Assert.Single(LibraryMerger.Group([first, second], _ => null));
        var item = new GalleryItemViewModel(group, _ => NullImageRequest.Instance, null);

        Assert.True(item.HasMultipleVersions);
        Assert.Equal("×2", item.VersionBadge);
    }

    [Fact]
    public void DownloadProjectionShowsDeterminateProgressAndClearsOnCompletion()
    {
        GalleryInfo only = Info("solo", "7", Path.Combine(_root, "solo"));
        var item = new GalleryItemViewModel(
            new GalleryGroup { WorkId = "7", Versions = [only] },
            _ => NullImageRequest.Instance,
            null);

        item.ApplyDownload(new DownloadTask { State = DownloadState.Running, DownloadedPages = 3, PageCount = 10 }, true);
        Assert.True(item.HasActiveDownload);
        Assert.True(item.IsDownloadActive);
        Assert.Equal(0.3, item.DownloadFraction, 3);
        Assert.Contains("3/10", item.DownloadStatusText);

        item.ApplyDownload(new DownloadTask { State = DownloadState.Running, DownloadedPages = 3, PageCount = 0 }, true);
        Assert.True(item.IsDownloadIndeterminate);

        item.ApplyDownload(new DownloadTask { State = DownloadState.Completed, DownloadedPages = 10, PageCount = 10 }, true);
        Assert.False(item.HasActiveDownload);
    }

    [Fact]
    public void DownloadProjectionMarksTheNonPrimaryVersion()
    {
        GalleryInfo first = Info("a", "9", Path.Combine(_root, "a"));
        GalleryInfo second = Info("b", "9", Path.Combine(_root, "b"));
        var item = new GalleryItemViewModel(
            new GalleryGroup { WorkId = "9", Versions = [first, second] },
            _ => NullImageRequest.Instance,
            null);

        item.ApplyDownload(new DownloadTask { State = DownloadState.Paused, DownloadedPages = 2, PageCount = 8 }, false);

        Assert.Contains("其他版本", item.DownloadStatusText);
        Assert.Equal("已暂停", item.DownloadStatusText.Split(" · ")[1]);
    }

    private static GalleryInfo Info(string sourceId, string workId, string folder)
    {
        Directory.CreateDirectory(folder);
        return new GalleryInfo
        {
            StableId = sourceId + ":" + workId,
            WorkId = workId,
            SourceId = sourceId,
            SourceName = sourceId,
            Title = "Title " + workId,
            FolderName = Path.GetFileName(folder),
            FolderPath = folder,
            PageCount = 3,
            ExpectedPages = 3,
            Gid = long.Parse(workId),
        };
    }
}

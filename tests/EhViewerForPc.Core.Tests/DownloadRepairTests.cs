using System.Net;
using EhViewerForPc.Core.Download;
using EhViewerForPc.Core.Format;
using EhViewerForPc.Core.Imaging;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Core.Tests;

public sealed class DownloadRepairTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.qa/progress-repair-20261114/core-" + Guid.NewGuid().ToString("N")));
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");
    private string Source => Path.Combine(_root, "source");
    private string Folder => Path.Combine(Source, "42-original-name");
    private AppPaths Paths => new(Path.Combine(_root, "data"));

    public DownloadRepairTests()
    {
        Paths.EnsureCreated();
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(Path.Combine(Folder, "00000001.png"), Png);
        File.WriteAllText(Path.Combine(Folder, ".ehviewer"), "original metadata");
        Assert.True(ImageSignature.IsValidFile(Path.Combine(Folder, "00000001.png")));
    }

    [Fact]
    public async Task DownloadFromLinkedSiteResumesOnThatSiteAndUsesItsImageCache()
    {
        var settings = Settings();
        var handler = new FixtureHandler();
        using var client = new EhClient(settings, new HttpClient(handler), new HttpClient(handler));
        var images = new GalleryImageStore(Paths, client);
        var details = await client.GetDetailsAsync(42, "0123456789", CancellationToken.None, EhSite.ExHentai);
        using (var manager = new DownloadManager(Paths, settings, client, images: images))
        {
            DownloadTask queued = manager.Enqueue(details, startImmediately: false, quality: ImageQuality.Resampled);
            manager.Pause(queued);
        }

        using var restored = new DownloadManager(Paths, settings, client, images: images);
        DownloadTask task = Assert.Single(restored.Tasks);
        Assert.Equal(EhSite.ExHentai, task.Site);
        restored.Resume(task);
        await WaitForAsync(() => task.State is DownloadState.Completed or DownloadState.Failed);

        Assert.Equal(DownloadState.Completed, task.State);
        Assert.Equal(EhSite.EHentai, client.Site);
        Assert.Equal(4, handler.GalleryHosts.Count);
        Assert.All(handler.GalleryHosts, host => Assert.Equal("exhentai.org", host));
        Assert.True(images.TryGetCachedPath(42, 1, false, null, out _, out _, EhSite.ExHentai));
        Assert.False(images.TryGetCachedPath(42, 1, false, null, out _, out _, EhSite.EHentai));
    }

    [Fact]
    public async Task RepairOnlyAddsMissingPagesAtTheExactFolderAndPreservesMetadata()
    {
        var settings = Settings(); var handler = new FixtureHandler();
        using var client = new EhClient(settings, new HttpClient(handler), new HttpClient(handler));
        using var manager = new DownloadManager(Paths, settings, client);
        string original = Path.Combine(Folder, "00000001.png");
        DateTime stamp = File.GetLastWriteTimeUtc(original);
        var task = manager.EnqueueRepair(Details(), Folder, Source);
        await WaitForAsync(() => task.State is DownloadState.Completed or DownloadState.Failed);
        Assert.Equal(DownloadState.Completed, task.State);
        Assert.Equal(Folder, task.FolderPath);
        Assert.Equal(Png, File.ReadAllBytes(original));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(original));
        Assert.Equal("original metadata", File.ReadAllText(Path.Combine(Folder, ".ehviewer")));
        Assert.True(ImageSignature.IsValidFile(Path.Combine(Folder, "00000002.png")));
        Assert.DoesNotContain(handler.Requests, path => path.Contains("42-1", StringComparison.Ordinal));
        Assert.False(Directory.Exists(settings.Download.RootPath));
        Assert.Equal(2, task.DownloadedPages);
        Assert.Throws<InvalidOperationException>(() => manager.Remove(task, deleteFiles: true));
        manager.Remove(task, deleteFiles: false);
        Assert.True(File.Exists(original));
    }

    [Fact]
    public void WaitingRepairStartsAtExistingPagesAndPersistsAcrossRestart()
    {
        for (int page = 2; page <= 51; page++) File.WriteAllBytes(Path.Combine(Folder, $"{page:D8}.png"), Png);
        File.WriteAllBytes(Path.Combine(Folder, "00000052.png"), []);
        File.WriteAllBytes(Path.Combine(Folder, "00000054.png"), Png);
        var details = new EhGalleryDetails { Gid = 42, Token = "0123456789", Pages = 53 };
        var settings = Settings(); using var client = new EhClient(settings);
        using (var manager = new DownloadManager(Paths, settings, client))
        {
            var task = manager.EnqueueRepair(details, Folder, Source, false);
            Assert.Equal(DownloadState.Pending, task.State);
            Assert.Equal(51, task.DownloadedPages); Assert.Equal(53, task.PageCount);
            var result = manager.EnqueueRepairs([new(details, Folder, Source)], startImmediately: false);
            Assert.Equal(1, result.Existing); Assert.Equal(51, task.DownloadedPages);
        }
        using var restored = new DownloadManager(Paths, settings, client);
        Assert.Equal(51, restored.Tasks.Single().DownloadedPages);
    }

    [Fact]
    public async Task RestoredZeroProgressRepairsAreReconciledWithoutStartingDownloads()
    {
        var settings = Settings(); using var client = new EhClient(settings);
        using var manager = new DownloadManager(Paths, settings, client);
        var task = manager.EnqueueRepair(Details(), Folder, Source, false);
        task.DownloadedPages = 0;
        await manager.RefreshRepairProgressAsync();
        Assert.Equal(1, task.DownloadedPages); Assert.Equal(DownloadState.Pending, task.State);
        Assert.Equal(0, manager.ActiveCount);
    }

    [Fact]
    public async Task AlreadyCompletedCopyMakesNoImagePageRequestsAndPreservesEmptyAlternate()
    {
        File.WriteAllBytes(Path.Combine(Folder, "00000002.png"), Png);
        string alternate = Path.Combine(Folder, "00000002.jpg");
        File.WriteAllBytes(alternate, []);
        var before = Directory.GetFiles(Folder).ToDictionary(p => p, p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));
        var settings = Settings(); var handler = new FixtureHandler();
        using var client = new EhClient(settings, new HttpClient(handler), new HttpClient(handler));
        using var manager = new DownloadManager(Paths, settings, client);
        var task = manager.EnqueueRepair(Details(), Folder, Source);
        await WaitForAsync(() => task.State is DownloadState.Completed or DownloadState.Failed);
        Assert.Equal(DownloadState.Completed, task.State);
        Assert.DoesNotContain(handler.Requests, path => path.StartsWith("/s/", StringComparison.Ordinal) || path == "/page.png");
        Assert.All(before, item => Assert.Equal(item.Value, (new FileInfo(item.Key).Length, File.GetLastWriteTimeUtc(item.Key))));
        Assert.Equal(2, task.DownloadedPages);
        Assert.False(PageFileParser.Scan(Folder, new SpiderHeader { HasPages = true, Pages = 2 }, out _).IsIncomplete);
    }

    [Fact]
    public void CopiesHaveIndependentPersistedKeysAndCompletedTasksCanContinue()
    {
        var settings = Settings();
        using var client = new EhClient(settings);
        string other = Path.Combine(Source, "42-another-copy"); Directory.CreateDirectory(other);
        string firstKey;
        using (var manager = new DownloadManager(Paths, settings, client))
        {
            var first = manager.EnqueueRepair(Details(), Folder, Source, false);
            firstKey = first.Id;
            var second = manager.EnqueueRepair(Details(), other, Source, false);
            Assert.NotEqual(first.Id, second.Id);
            Assert.Same(first, manager.EnqueueRepair(Details(), Folder, Source, false));
            first.State = DownloadState.Completed;
            Assert.Same(first, manager.EnqueueRepair(Details(), Folder, Source, false));
            Assert.Equal(DownloadState.Pending, first.State);
        }
        using var restored = new DownloadManager(Paths, settings, client);
        Assert.Equal(2, restored.Tasks.Count);
        var copy = restored.Tasks.Single(t => t.FolderPath == Folder);
        Assert.Equal(firstKey, copy.Id);
        Assert.True(copy.FillMissingOnly);
        Assert.Equal(Source, copy.DestinationRoot);
    }

    [Fact]
    public async Task PauseAndImmediateResumeKeepTheOriginalDestinationAndDrainTemporaryFiles()
    {
        var settings = Settings(); var handler = new FixtureHandler { BlockImages = true };
        using var client = new EhClient(settings, new HttpClient(handler), new HttpClient(handler));
        using var manager = new DownloadManager(Paths, settings, client);
        var task = manager.EnqueueRepair(Details(), Folder, Source);
        await handler.ImageStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        manager.Pause(task);
        handler.ReleaseImages();
        Assert.Same(task, manager.EnqueueRepair(Details(), Folder, Source));
        await WaitForAsync(() => task.State is DownloadState.Completed or DownloadState.Failed);
        Assert.Equal(DownloadState.Completed, task.State);
        Assert.Equal(Folder, task.FolderPath);
        Assert.Empty(Directory.GetFiles(Folder, "*.part"));
    }

    [Fact]
    public async Task ReconcilingLocalPagesCompletesATaskWhosePagesWereFilledElsewhere()
    {
        var settings = Settings(); using var client = new EhClient(settings);
        using var manager = new DownloadManager(Paths, settings, client);
        var task = manager.EnqueueRepair(Details(), Folder, Source, false);
        task.State = DownloadState.Failed;
        task.Message = "1 页下载失败（可重试）";
        task.FailedPages = [2];
        task.DownloadedPages = 1;
        File.WriteAllBytes(Path.Combine(Folder, "00000002.png"), Png);

        Assert.True(await manager.RefreshLocalProgressAsync(task));

        Assert.Empty(task.FailedPages);
        Assert.Equal(2, task.DownloadedPages);
        Assert.Equal(DownloadState.Completed, task.State);
        Assert.Equal("已完成（按本地页文件核对）", task.Message);
    }

    [Fact]
    public async Task ReconcilingLocalPagesKeepsPagesThatAreStillMissing()
    {
        var settings = Settings(); using var client = new EhClient(settings);
        using var manager = new DownloadManager(Paths, settings, client);
        var details = new EhGalleryDetails { Gid = 42, Token = "0123456789", Title = "Fixture", Pages = 3 };
        var task = manager.EnqueueRepair(details, Folder, Source, false);
        task.State = DownloadState.Failed;
        task.FailedPages = [2, 3];
        task.DownloadedPages = 1;
        File.WriteAllBytes(Path.Combine(Folder, "00000002.png"), Png);

        Assert.True(await manager.RefreshLocalProgressAsync(task));

        Assert.Equal([3], task.FailedPages);
        Assert.Equal(2, task.DownloadedPages);
        Assert.Equal(DownloadState.Failed, task.State);
    }

    [Fact]
    public async Task ReaderPageRetryUpdatesTheQueuedTaskAndCompletesIt()
    {
        var settings = Settings(); var handler = new FixtureHandler();
        using var client = new EhClient(settings, new HttpClient(handler), new HttpClient(handler));
        using var manager = new DownloadManager(Paths, settings, client);
        var task = manager.EnqueueRepair(Details(), Folder, Source, false);
        task.State = DownloadState.Failed;
        task.Message = "1 页下载失败（可重试）";
        task.FailedPages = [2];
        task.DownloadedPages = 1;
        Assert.False(File.Exists(Path.Combine(Folder, "00000002.png")));

        string page = await manager.RedownloadPageAsync(Details(), 2, Folder, Source, null, CancellationToken.None);

        Assert.True(File.Exists(page));
        Assert.Empty(task.FailedPages);
        Assert.Equal(2, task.DownloadedPages);
        Assert.Equal(DownloadState.Completed, task.State);
    }

    [Fact]
    public async Task InvalidSameNameFileIsReplacedByRepairInsteadOfBlockingForever()
    {
        string conflict = Path.Combine(Folder, "00000002.png");
        byte[] broken = [1, 2, 3]; File.WriteAllBytes(conflict, broken);
        var settings = Settings(); var handler = new FixtureHandler();
        using var client = new EhClient(settings, new HttpClient(handler), new HttpClient(handler));
        using var manager = new DownloadManager(Paths, settings, client);
        var task = manager.EnqueueRepair(Details(), Folder, Source);
        await WaitForAsync(() => task.State is DownloadState.Completed or DownloadState.Failed);

        // 3 字节的文件本来就不是可读页：继续保留只会让「补齐」永远失败，
        // 因此补页允许就地替换；有效页仍然走「保留 + 报告冲突」的路径。
        Assert.Equal(DownloadState.Completed, task.State);
        Assert.Empty(task.FailedPages);
        Assert.Equal(Png, File.ReadAllBytes(conflict));
        Assert.True(ImageSignature.IsValidFile(conflict));
        Assert.Empty(Directory.GetFiles(Folder, "*.part"));
    }

    [Fact]
    public async Task RepairRemovesStaleTruncatedFileWhenTheContainerChanges()
    {
        // 真实案例：目录里 171 个页文件看起来「全齐」，但其中一页是 85% 就断掉的
        // webp（RIFF 声明 1008 字节，实际只有 32）。补回来的页是 png，写入后必须
        // 清掉这个旧文件，否则目录继续留着坏页，别的阅读器还可能优先读到它。
        string stale = Path.Combine(Folder, "00000002.webp");
        var truncated = new byte[32];
        "RIFF"u8.CopyTo(truncated);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(4), 1000);
        "WEBPVP8 "u8.CopyTo(truncated.AsSpan(8));
        File.WriteAllBytes(stale, truncated);
        Assert.False(ImageSignature.IsValidFile(stale));

        var settings = Settings(); var handler = new FixtureHandler();
        using var client = new EhClient(settings, new HttpClient(handler), new HttpClient(handler));
        using var manager = new DownloadManager(Paths, settings, client);
        var task = manager.EnqueueRepair(Details(), Folder, Source);
        await WaitForAsync(() => task.State is DownloadState.Completed or DownloadState.Failed);

        Assert.Equal(DownloadState.Completed, task.State);
        Assert.False(File.Exists(stale));
        Assert.True(ImageSignature.IsValidFile(Path.Combine(Folder, "00000002.png")));
        Assert.Equal(2, task.DownloadedPages);
    }

    [Fact]
    public async Task ReconcilingClassifiesCorruptPagesSeparatelyFromMissingOnes()
    {
        var settings = Settings(); using var client = new EhClient(settings);
        using var manager = new DownloadManager(Paths, settings, client);
        var details = new EhGalleryDetails { Gid = 42, Token = "0123456789", Title = "Fixture", Pages = 3 };
        var task = manager.EnqueueRepair(details, Folder, Source, false);
        task.State = DownloadState.Failed;
        task.FailedPages = [2, 3];
        task.CorruptPages = [];
        task.DownloadedPages = 1;

        File.WriteAllBytes(Path.Combine(Folder, "00000002.png"), [9, 9, 9]); // 有文件，但不是图片
        // 第 3 页没有任何文件 → 缺失

        Assert.True(await manager.RefreshLocalProgressAsync(task));

        Assert.Equal([2], task.CorruptPages);
        Assert.Equal([2, 3], task.FailedPages);
        Assert.Equal("1 页缺失、1 页文件损坏（可重试）", task.Message);
        Assert.Equal(DownloadState.Failed, task.State);
        Assert.Equal(1, task.DownloadedPages);
    }

    [Fact]
    public async Task ReconcileClearsCorruptStateAfterThePageWasReplaced()
    {
        var settings = Settings(); using var client = new EhClient(settings);
        using var manager = new DownloadManager(Paths, settings, client);
        var task = manager.EnqueueRepair(Details(), Folder, Source, false);
        task.State = DownloadState.Failed;
        task.FailedPages = [2];
        task.CorruptPages = [2];
        task.DownloadedPages = 1;

        File.WriteAllBytes(Path.Combine(Folder, "00000002.png"), Png);

        Assert.True(await manager.RefreshLocalProgressAsync(task));

        Assert.Empty(task.CorruptPages);
        Assert.Empty(task.FailedPages);
        Assert.Equal(DownloadState.Completed, task.State);
    }

    [Fact]
    public async Task ReaderPageRetryClearsCorruptEntryAndReportsProgress()
    {
        string corrupt = Path.Combine(Folder, "00000002.png");
        File.WriteAllBytes(corrupt, [1, 2, 3]);
        var settings = Settings(); var handler = new FixtureHandler();
        using var client = new EhClient(settings, new HttpClient(handler), new HttpClient(handler));
        using var manager = new DownloadManager(Paths, settings, client);
        var task = manager.EnqueueRepair(Details(), Folder, Source, false);
        task.State = DownloadState.Failed;
        task.FailedPages = [2];
        task.CorruptPages = [2];
        task.DownloadedPages = 1;

        var statuses = new CollectingProgress();
        string page = await manager.RedownloadPageAsync(Details(), 2, Folder, Source, corrupt, CancellationToken.None, statuses);

        Assert.True(ImageSignature.IsValidFile(page));
        Assert.Empty(task.CorruptPages);
        Assert.Empty(task.FailedPages);
        Assert.Equal(DownloadState.Completed, task.State);
        // 进度必须真的流出来：解析、解码与完成状态都要经过中继。
        Assert.Contains(statuses.Values, status => status.Phase == ImageLoadPhase.Resolving);
        Assert.Contains(statuses.Values, status => status.Phase is ImageLoadPhase.Decoding or ImageLoadPhase.Ready);
    }

    /// <summary>同步收集进度：Progress&lt;T&gt; 会投递到线程池，断言会在回调前跑。</summary>
    private sealed class CollectingProgress : IProgress<ImageLoadStatus>
    {
        public List<ImageLoadStatus> Values { get; } = [];

        public void Report(ImageLoadStatus value)
        {
            lock (Values) Values.Add(value);
        }
    }

    [Fact]
    public void FailedQueueSaveRollsBackTheExistingTask()
    {
        var settings = Settings(); using var client = new EhClient(settings);
        using var manager = new DownloadManager(Paths, settings, client);
        var task = manager.EnqueueRepair(Details(), Folder, Source, false);
        task.State = DownloadState.Completed; task.Message = "completed before retry";
        File.WriteAllBytes(Path.Combine(Folder, "00000002.png"), Png);
        int previousProgress = task.DownloadedPages;
        using (var locked = new FileStream(Paths.DownloadTasksFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => manager.EnqueueRepair(new EhGalleryDetails { Gid = 42, Token = "changed", Pages = 5 }, Folder, Source, false));
            Assert.Equal(DownloadState.Completed, task.State);
            Assert.Equal("completed before retry", task.Message);
            Assert.Equal("0123456789", task.Token);
            Assert.Equal(2, task.PageCount);
            Assert.Equal(previousProgress, task.DownloadedPages);
        }
        Assert.Single(manager.Tasks);
    }

    [Fact]
    public async Task SpeedUpdatesDuringAnUnfinishedPageAndClearsOnPauseAndCompletion()
    {
        var settings = Settings(); var handler = new FixtureHandler { StreamImages = true };
        using var client = new EhClient(settings, new HttpClient(handler), new HttpClient(handler));
        using var manager = new DownloadManager(Paths, settings, client);
        var task = manager.EnqueueRepair(Details(), Folder, Source);
        await handler.StreamBlocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForAsync(() => task.BytesPerSecond > 0);
        Assert.Equal(1, task.DownloadedPages);
        await WaitForAsync(() => task.BytesPerSecond == 0);
        manager.Pause(task);
        Assert.Equal(0, task.BytesPerSecond);
        handler.ReleaseImages(); handler.StreamImages = false;
        manager.Resume(task);
        await WaitForAsync(() => task.State is DownloadState.Completed or DownloadState.Failed);
        Assert.Equal(DownloadState.Completed, task.State);
        Assert.Equal(0, task.BytesPerSecond);
        Assert.DoesNotContain("bytesPerSecond", File.ReadAllText(Paths.DownloadTasksFile), StringComparison.OrdinalIgnoreCase);
        int requests = handler.Requests.Count(path => path == "/page.png");
        string other = Path.Combine(Source, "42-cached-copy"); Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(other, "00000001.png"), Png);
        bool reportedNetworkSpeed = false;
        manager.TaskChanged += (_, changed) => { if (changed.FolderPath == other && changed.BytesPerSecond > 0) reportedNetworkSpeed = true; };
        var cached = manager.EnqueueRepair(Details(), other, Source);
        await WaitForAsync(() => cached.State is DownloadState.Completed or DownloadState.Failed);
        Assert.Equal(DownloadState.Completed, cached.State);
        Assert.False(reportedNetworkSpeed);
        Assert.Equal(requests, handler.Requests.Count(path => path == "/page.png"));
    }

    [Fact]
    public void BatchRepairDeduplicatesFoldersReportsFailuresAndNotifiesOnce()
    {
        var settings = Settings(); using var client = new EhClient(settings);
        using var manager = new DownloadManager(Paths, settings, client);
        var original = manager.EnqueueRepair(Details(), Folder, Source, false); manager.Pause(original);
        string other = Path.Combine(Source, "42-other"); Directory.CreateDirectory(other);
        int notifications = 0; manager.Changed += (_, _) => notifications++;
        var requests = new[]
        {
            new DownloadRepairRequest(Details(), Folder, Source), new(Details(), Folder, Source),
            new(Details(), other, Source), new(Details(), Path.Combine(Source, "missing"), Source),
        };
        var result = manager.EnqueueRepairs(requests, startImmediately: false);
        Assert.Equal(1, result.Added); Assert.Equal(1, result.Continued); Assert.Single(result.Failures);
        Assert.Equal(1, notifications); Assert.Equal(2, manager.Tasks.Count);
        Assert.Same(original, manager.Tasks.Single(t => t.FolderPath == Folder));
        notifications = 0;
        using var locked = new FileStream(Paths.DownloadTasksFile, FileMode.Open, FileAccess.Read, FileShare.None);
        var repeated = manager.EnqueueRepairs(requests[..3], startImmediately: false);
        Assert.Equal(2, repeated.Existing); Assert.Equal(0, repeated.Added); Assert.Equal(0, notifications);
    }

    [Fact]
    public void BatchSaveFailureAndCancellationDoNotLeavePartiallyQueuedTasks()
    {
        var settings = Settings(); using var client = new EhClient(settings);
        using var manager = new DownloadManager(Paths, settings, client);
        var original = manager.EnqueueRepair(Details(), Folder, Source, false); manager.Pause(original);
        string other = Path.Combine(Source, "42-other"); Directory.CreateDirectory(other);
        var requests = new[] { new DownloadRepairRequest(Details(), Folder, Source), new(Details(), other, Source) };
        using (var locked = new FileStream(Paths.DownloadTasksFile, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => manager.EnqueueRepairs(requests, startImmediately: false));
        Assert.Single(manager.Tasks); Assert.Equal(DownloadState.Paused, original.State);
        using var canceled = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => manager.EnqueueRepairs(requests, new CancelProgress(canceled), canceled.Token, false));
        Assert.Single(manager.Tasks); Assert.Equal(DownloadState.Paused, original.State);
    }

    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<int>
    {
        public void Report(int value) => cancellation.Cancel();
    }

    [Fact]
    public void OutsideOrUnavailableTargetsDoNotCreateTasksOrDirectories()
    {
        var settings = Settings(); using var client = new EhClient(settings);
        using var manager = new DownloadManager(Paths, settings, client);
        string outside = Path.Combine(_root, "outside"); Directory.CreateDirectory(outside);
        Assert.Throws<IOException>(() => manager.EnqueueRepair(Details(), outside, Source, false));
        string missing = Path.Combine(Source, "not-here");
        Assert.Throws<IOException>(() => manager.EnqueueRepair(Details(), missing, Source, false));
        Assert.False(Directory.Exists(missing));
        Assert.Empty(manager.Tasks);
    }

    private AppSettings Settings()
    {
        var settings = new AppSettings();
        settings.Download.RootPath = Path.Combine(_root, "default-downloads");
        settings.Download.AutoStart = false;
        settings.Network.ProxyMode = ProxyMode.None;
        return settings;
    }
    private static EhGalleryDetails Details() => new() { Gid = 42, Token = "0123456789", Title = "Fixture", Pages = 2 };
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(condition());
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public bool BlockImages;
        public bool StreamImages;
        public readonly TaskCompletionSource StreamBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ImageStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly System.Collections.Concurrent.ConcurrentBag<string> Requests = [];
        public readonly System.Collections.Concurrent.ConcurrentBag<string> GalleryHosts = [];
        public void ReleaseImages() { BlockImages = false; _released.TrySetResult(); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath; Requests.Add(path);
            if (request.RequestUri.Host == "images.example")
            {
                ImageStarted.TrySetResult();
                if (BlockImages) await _released.Task.WaitAsync(cancellationToken);
                return new(HttpStatusCode.OK) { Content = StreamImages ? new StreamContent(new PacedStream(this)) : new ByteArrayContent(Png) };
            }
            GalleryHosts.Add(request.RequestUri.Host);
            string html = path.StartsWith("/s/", StringComparison.Ordinal)
                ? "<img id='img' src='https://images.example/page.png'>"
                : "<h1 id='gn'>Fixture</h1><div id='gdd'><table><tr><td>Length:</td><td>2 pages</td></tr></table></div><div id='gdt'><a href='/s/1111111111/42-1'><img></a><a href='/s/2222222222/42-2'><img></a></div>";
            return new(HttpStatusCode.OK) { Content = new StringContent(html) };
        }

        private sealed class PacedStream(FixtureHandler owner) : Stream
        {
            private int _chunks;
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_chunks >= 8)
                {
                    owner.StreamBlocked.TrySetResult();
                    await owner._released.Task.WaitAsync(cancellationToken);
                    return 0;
                }
                await Task.Delay(60, cancellationToken);
                int length = Math.Min(65536, buffer.Length);
                buffer.Span[..length].Clear();
                if (_chunks++ == 0) Png.CopyTo(buffer.Span);
                return length;
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}

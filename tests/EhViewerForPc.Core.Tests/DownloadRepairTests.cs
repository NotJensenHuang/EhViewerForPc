using System.Net;
using EhViewerForPc.Core.Download;
using EhViewerForPc.Core.Format;
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
    public async Task ConflictingOriginalFileIsPreservedAndReportedOnce()
    {
        string conflict = Path.Combine(Folder, "00000002.png");
        byte[] broken = [1, 2, 3]; File.WriteAllBytes(conflict, broken);
        var settings = Settings(); var handler = new FixtureHandler();
        using var client = new EhClient(settings, new HttpClient(handler), new HttpClient(handler));
        using var manager = new DownloadManager(Paths, settings, client);
        var task = manager.EnqueueRepair(Details(), Folder, Source);
        await WaitForAsync(() => task.State is DownloadState.Completed or DownloadState.Failed);
        Assert.Equal(DownloadState.Failed, task.State);
        Assert.Equal(new[] { 2 }, task.FailedPages);
        Assert.Contains("1 页下载失败", task.Message);
        Assert.Equal(broken, File.ReadAllBytes(conflict));
        Assert.Empty(Directory.GetFiles(Folder, "*.part"));
    }

    [Fact]
    public void FailedQueueSaveRollsBackTheExistingTask()
    {
        var settings = Settings(); using var client = new EhClient(settings);
        using var manager = new DownloadManager(Paths, settings, client);
        var task = manager.EnqueueRepair(Details(), Folder, Source, false);
        task.State = DownloadState.Completed; task.Message = "completed before retry";
        using (var locked = new FileStream(Paths.DownloadTasksFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => manager.EnqueueRepair(new EhGalleryDetails { Gid = 42, Token = "changed", Pages = 5 }, Folder, Source, false));
            Assert.Equal(DownloadState.Completed, task.State);
            Assert.Equal("completed before retry", task.Message);
            Assert.Equal("0123456789", task.Token);
            Assert.Equal(2, task.PageCount);
        }
        Assert.Single(manager.Tasks);
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
        public readonly TaskCompletionSource ImageStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly System.Collections.Concurrent.ConcurrentBag<string> Requests = [];
        public void ReleaseImages() { BlockImages = false; _released.TrySetResult(); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath; Requests.Add(path);
            if (request.RequestUri.Host == "images.example")
            {
                ImageStarted.TrySetResult();
                if (BlockImages) await _released.Task.WaitAsync(cancellationToken);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
            }
            string html = path.StartsWith("/s/", StringComparison.Ordinal)
                ? "<img id='img' src='https://images.example/page.png'>"
                : "<h1 id='gn'>Fixture</h1><div id='gdd'><table><tr><td>Length:</td><td>2 pages</td></tr></table></div><div id='gdt'><a href='https://e-hentai.org/s/1111111111/42-1'><img></a><a href='https://e-hentai.org/s/2222222222/42-2'><img></a></div>";
            return new(HttpStatusCode.OK) { Content = new StringContent(html) };
        }
    }
}

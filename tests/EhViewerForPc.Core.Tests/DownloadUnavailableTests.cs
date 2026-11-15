using System.Net;
using EhViewerForPc.Core.Download;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Core.Tests;

public sealed class DownloadUnavailableTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "../../../../../.qa/gallery-unavailable-20261115/core-" + Guid.NewGuid().ToString("N")));

    public DownloadUnavailableTests() => Paths.EnsureCreated();

    private AppPaths Paths => new(Path.Combine(_root, "data"));

    [Fact]
    public async Task UnavailableFailureMarksTaskSkipsStartAllAndSurvivesRestart()
    {
        var settings = Settings();
        var handler = new FixtureHandler();
        using var client = new EhClient(settings, new HttpClient(handler), new HttpClient(handler));
        using (var manager = new DownloadManager(Paths, settings, client))
        {
            DownloadTask task = manager.Enqueue(Details(), startImmediately: true);
            await WaitForAsync(() => task.State is DownloadState.Failed);

            Assert.True(task.GalleryUnavailable);
            Assert.Equal(EhParser.GalleryUnavailableMessage, task.Message);
            Assert.Equal(1, handler.Requests);

            // 标记后的任务不参与「全部开始」，也不会再次请求站点。
            manager.StartAll();
            Assert.Equal(DownloadState.Failed, task.State);
            Assert.Equal(1, handler.Requests);
        }

        using var restored = new DownloadManager(Paths, settings, client);
        DownloadTask persisted = Assert.Single(restored.Tasks);
        Assert.True(persisted.GalleryUnavailable);
        Assert.Equal(DownloadState.Failed, persisted.State);
        Assert.Equal(EhParser.GalleryUnavailableMessage, persisted.Message);
    }

    [Fact]
    public async Task OtherFailuresAreNotMarkedAsGalleryUnavailable()
    {
        var settings = Settings();
        var handler = new FixtureHandler(body: "<html><body>HTTP error 503</body></html>");
        using var client = new EhClient(settings, new HttpClient(handler), new HttpClient(handler));
        using var manager = new DownloadManager(Paths, settings, client);

        DownloadTask task = manager.Enqueue(Details(), startImmediately: true);
        await WaitForAsync(() => task.State is DownloadState.Failed);

        Assert.False(task.GalleryUnavailable);
        Assert.NotEqual(EhParser.GalleryUnavailableMessage, task.Message);
    }

    [Fact]
    public async Task ClearingTheMarkerReturnsTaskToTheQueueAndResumeClearsIt()
    {
        var settings = Settings();
        var handler = new FixtureHandler(body: "<html><body>This gallery is unavailable</body></html>", gate: true);
        using var client = new EhClient(settings, new HttpClient(handler), new HttpClient(handler));
        using var manager = new DownloadManager(Paths, settings, client);

        DownloadTask task = manager.Enqueue(Details(), startImmediately: false);
        task.State = DownloadState.Failed;
        task.Message = EhParser.GalleryUnavailableMessage;
        manager.MarkGalleryUnavailable(task);

        manager.StartAll();
        Assert.Equal(DownloadState.Failed, task.State);
        Assert.Equal(0, handler.Requests);

        manager.ClearGalleryUnavailable(task);
        Assert.False(task.GalleryUnavailable);
        Assert.Equal(DownloadState.Failed, task.State);

        try
        {
            manager.Resume(task);
            // 显式继续即撤销标记；请求仍被夹具挡住，状态未被失败覆盖。
            Assert.False(task.GalleryUnavailable);
            Assert.True(task.State is DownloadState.Pending or DownloadState.Running);
        }
        finally
        {
            handler.Release();
        }

        await WaitForAsync(() => task.State is DownloadState.Failed);
        Assert.True(task.GalleryUnavailable);
    }

    [Fact]
    public void EnqueueingTheSameGalleryClearsTheMarker()
    {
        var settings = Settings();
        using var client = new EhClient(settings);
        using var manager = new DownloadManager(Paths, settings, client);

        DownloadTask task = manager.Enqueue(Details(), startImmediately: false);
        task.State = DownloadState.Failed;
        task.Message = EhParser.GalleryUnavailableMessage;
        manager.MarkGalleryUnavailable(task);

        DownloadTask again = manager.Enqueue(Details(), startImmediately: false);

        Assert.Same(task, again);
        Assert.False(again.GalleryUnavailable);
        Assert.Equal(DownloadState.Pending, again.State);
    }

    [Fact]
    public void OnlyTheExactUnavailableMessageIsRecognised()
    {
        Assert.True(EhParser.IsGalleryUnavailableMessage(EhParser.GalleryUnavailableMessage));
        Assert.False(EhParser.IsGalleryUnavailableMessage("3 页下载失败（可重试）"));
        Assert.False(EhParser.IsGalleryUnavailableMessage("画廊不可用"));
        Assert.False(EhParser.IsGalleryUnavailableMessage(null));
        Assert.Equal(EhParser.GalleryUnavailableMessage,
            EhParser.DetectMessage("<html><body>This gallery is unavailable</body></html>"));
    }

    private AppSettings Settings()
    {
        var settings = new AppSettings();
        settings.Download.RootPath = Path.Combine(_root, "downloads");
        settings.Download.AutoStart = false;
        settings.Network.ProxyMode = ProxyMode.None;
        return settings;
    }

    private static EhGalleryDetails Details() => new() { Gid = 7788, Token = "token7788", Title = "Unavailable fixture", Pages = 4 };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(condition());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private sealed class FixtureHandler(string body = "<html><body>This gallery is unavailable</body></html>", bool gate = false) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public void Release() => _released.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            if (gate) await _released.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}

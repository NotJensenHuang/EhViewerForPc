using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using EhViewerForPc.Core.Download;
using EhViewerForPc.Core.Format;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Imaging;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Core.Scanning;
using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Core.Tests;

public sealed class RemediationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EhReaderRegression-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6w1QAAAAASUVORK5CYII=");

    public RemediationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void LabeledGallerySurvivesScanAndCacheReload()
    {
        string folder = Path.Combine(_root, "art", "42-work");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "00000001.png"), Png);
        var scanner = new LibraryScanner();
        var result = scanner.Scan(_root, null);
        Assert.Equal(42, Assert.Single(result.Galleries).Gid);
        Assert.Equal(folder, Assert.Single(LibraryScanner.FromCacheOnly(result.Cache, _root)).FolderPath);
        Assert.Equal(1, scanner.Scan(_root, result.Cache).CacheHitCount);
    }

    [Fact]
    public async Task QueueRespectsGalleryConcurrency()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        using var client = Client(async (request, token) =>
        {
            Interlocked.Increment(ref requests);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Html("");
        });
        var settings = Settings();
        using var manager = new DownloadManager(Paths("queue"), settings, client);
        for (int i = 0; i < 24; i++) manager.Enqueue(Details(100 + i), startImmediately: false);
        manager.StartAll();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(100);
        int active = manager.ActiveCount;
        manager.PauseAll();
        await WaitFor(() => manager.ActiveCount == 0);
        Assert.Equal(1, active);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task OriginalFollowsFullimgLinkAndDoesNotUseRetryKeyAsOriginal()
    {
        var urls = new List<string>();
        using var client = Client((request, _) =>
        {
            string url = request.RequestUri!.ToString();
            urls.Add(url);
            if (url.Contains("fullimg.php"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                { Headers = { Location = new Uri("https://images.test/original.png") } });
            return Task.FromResult(Html("<img id='img' src='https://images.test/resampled.png' onerror=\"nl('retry')\"><a href='https://e-hentai.org/fullimg.php?gid=42'>original</a>"));
        });
        string result = await client.GetImageUrlAsync(Page(), true, CancellationToken.None);
        Assert.Equal("https://images.test/original.png", result);
        Assert.Contains(urls, u => u.Contains("fullimg.php"));
        Assert.DoesNotContain(urls, u => u.Contains("nl=retry") && !u.Contains("fullimg.php"));
    }

    [Fact]
    public async Task IncompletePageListCannotReportCompleted()
    {
        using var client = Client((_, _) => Task.FromResult(Html(PageList)));
        using var manager = new DownloadManager(Paths("incomplete"), Settings(), client);
        var task = manager.Enqueue(Details(42, 2));
        await WaitFor(() => task.State is DownloadState.Failed or DownloadState.Completed);
        Assert.Equal(DownloadState.Failed, task.State);
        Assert.Equal(2, task.PageCount);
    }

    [Fact]
    public async Task HtmlResponseCannotBecomeAnImageOrPoisonResume()
    {
        bool valid = false;
        using var client = Client((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.StartsWith("/g/")
            ? Html(PageList)
            : request.RequestUri.AbsolutePath.StartsWith("/s/")
                ? Html("<img id='img' src='https://images.test/image.png'>")
                : valid ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) } : Html("<html>temporary error</html>")));
        using var manager = new DownloadManager(Paths("integrity"), Settings(), client);
        var task = manager.Enqueue(Details(42), quality: ImageQuality.Resampled);
        await WaitFor(() => task.State is DownloadState.Failed or DownloadState.Completed);
        Assert.Equal(DownloadState.Failed, task.State);
        valid = true;
        manager.Retry(task);
        await WaitFor(() => task.State is DownloadState.Failed or DownloadState.Completed);
        Assert.Equal(DownloadState.Completed, task.State);
        Assert.Equal(Png, File.ReadAllBytes(Path.Combine(task.FolderPath, "00000001.png")));
    }

    [Fact]
    public async Task MissingImagePageIsRetriedOnceWithTheSitesNlKey()
    {
        var urls = new List<string>();
        using var client = Client((request, _) =>
        {
            string url = request.RequestUri!.ToString();
            lock (urls) urls.Add(url);
            return Task.FromResult(url.Contains("nl=", StringComparison.Ordinal)
                ? Html("<img id='img' src='https://images.test/page.png'>")
                : Html("<div id='loadfail'>Failed to load the image</div><a href='#' onclick=\"return nl('42-7')\">reload</a>"));
        });

        string url = await client.GetImageUrlAsync(Page(), false, CancellationToken.None);

        Assert.Equal("https://images.test/page.png", url);
        lock (urls) Assert.Contains(urls, u => u.Contains("nl=42-7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QuotaPlaceholderIsRetriedWithNlBeforeReportingQuota()
    {
        var urls = new List<string>();
        using var client = Client((request, _) =>
        {
            string url = request.RequestUri!.ToString();
            lock (urls) urls.Add(url);
            return Task.FromResult(url.Contains("nl=", StringComparison.Ordinal)
                ? Html("<img id='img' src='https://images.test/real.webp'>")
                : Html("<img id='img' src='https://e-hentai.org/img/509.gif' onerror=\"this.onerror=null; nl('42-9')\">"));
        });

        string url = await client.GetImageUrlAsync(Page(), false, CancellationToken.None);

        Assert.Equal("https://images.test/real.webp", url);
        lock (urls) Assert.Contains(urls, u => u.Contains("nl=42-9", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NormalImagePathContaining509IsNotTreatedAsAQuotaError()
    {
        using var client = Client((_, _) =>
            Task.FromResult(Html("<img id='img' src='https://images.test/w/509/abc.webp'>")));

        string url = await client.GetImageUrlAsync(Page(), false, CancellationToken.None);

        Assert.Equal("https://images.test/w/509/abc.webp", url);
    }

    [Fact]
    public async Task QuotaExhaustionPausesTheGalleryWithoutRetryingEveryPage()
    {
        int viewerRequests = 0;
        using var client = Client((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/g/", StringComparison.Ordinal))
                return Task.FromResult(Html(PageList));
            Interlocked.Increment(ref viewerRequests);
            return Task.FromResult(Html("<img id='img' src='https://e-hentai.org/img/509.gif'>"));
        });
        using var manager = new DownloadManager(Paths("quota"), Settings(), client);

        var task = manager.Enqueue(Details(42), quality: ImageQuality.Resampled);
        await WaitFor(() => task.State is DownloadState.Paused or DownloadState.Failed or DownloadState.Completed);

        Assert.Equal(DownloadState.Paused, task.State);
        Assert.Contains("509", task.Message, StringComparison.Ordinal);
        Assert.Equal(1, viewerRequests);
    }

    [Fact]
    public async Task TransientImageFailureIsRetriedWithoutFailingTheGallery()
    {
        int imageRequests = 0;
        using var client = Client((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/g/", StringComparison.Ordinal)) return Task.FromResult(Html(PageList));
            if (path.StartsWith("/s/", StringComparison.Ordinal)) return Task.FromResult(Html("<img id='img' src='https://images.test/image.png'>"));
            return Task.FromResult(Interlocked.Increment(ref imageRequests) == 1
                ? Html("<html>temporary gateway error</html>")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) });
        });
        using var manager = new DownloadManager(Paths("page-retry"), Settings(), client);
        var task = manager.Enqueue(Details(42), quality: ImageQuality.Resampled);

        await WaitFor(() => task.State is DownloadState.Completed or DownloadState.Failed);

        Assert.Equal(DownloadState.Completed, task.State);
        Assert.Empty(task.FailedPages);
        Assert.True(imageRequests >= 2);
        Assert.True(File.Exists(Path.Combine(task.FolderPath, "00000001.png")));
    }

    [Fact]
    public void WebpTrailingPaddingIsAcceptedButTruncationIsRejected()
    {
        string padded = Path.Combine(_root, "padded.webp");
        File.WriteAllBytes(padded, Webp(padded: true));
        Assert.True(ImageSignature.IsValidFile(padded));

        string truncated = Path.Combine(_root, "truncated.webp");
        File.WriteAllBytes(truncated, Webp(padded: true)[..20]);
        Assert.False(ImageSignature.IsValidFile(truncated));
    }

    private static byte[] Webp(bool padded)
    {
        // RIFF + 声明长度 17 + WEBP + VP8L + 5 字节载荷；padded 版本追加 4 字节填充。
        var bytes = new byte[padded ? 29 : 25];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 17);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        "VP8L"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 5);
        bytes[20] = 0x2F;
        return bytes;
    }

    [Fact]
    public async Task OnlineCacheIsReusedByFormalDownload()
    {
        int imageRequests = 0;
        using var client = Client((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/g/")) return Task.FromResult(Html(PageList));
            if (path.StartsWith("/s/")) return Task.FromResult(Html("<img id='img' src='https://images.test/image.png'>"));
            Interlocked.Increment(ref imageRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) });
        });
        var paths = Paths("shared-cache");
        var images = new GalleryImageStore(paths, client);
        await images.GetAsync(Page(), false, CancellationToken.None);
        using var manager = new DownloadManager(paths, Settings(), client, images: images);
        var task = manager.Enqueue(Details(42), quality: ImageQuality.Resampled);
        await WaitFor(() => task.State is DownloadState.Completed or DownloadState.Failed);
        Assert.Equal(DownloadState.Completed, task.State);
        Assert.Equal(1, imageRequests);
    }

    [Fact]
    public async Task CompressedSizesUseSeparateUrlsAndCacheFiles()
    {
        int viewerRequests = 0;
        using var client = Client((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/s/"))
            {
                Interlocked.Increment(ref viewerRequests);
                return Task.FromResult(Html("<img id='img' src='https://images.test/image.png'>"));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) });
        });
        client.Session.Import(new Dictionary<string, string> { ["uconfig"] = "tl_j-xr_1280-oi_y" });
        var images = new GalleryImageStore(Paths("sizes"), client);
        var small = await images.GetAsync(Page(), false, CancellationToken.None, 780);
        var large = await images.GetAsync(Page(), false, CancellationToken.None, 1600);
        var smallAgain = await images.GetAsync(Page(), false, CancellationToken.None, 780);
        Assert.Equal(2, viewerRequests);
        Assert.NotEqual(small.Path, large.Path);
        Assert.Equal(small.Path, smallAgain.Path);
        string cookie = client.Session.Cookies.GetCookies(new Uri("https://e-hentai.org"))["uconfig"]!.Value;
        Assert.Contains("tl_j", cookie); Assert.Contains("xr_1600", cookie); Assert.Contains("oi_n", cookie);
        Assert.DoesNotContain("xr_1280", cookie);
    }

    [Fact]
    public async Task RemovingAnActiveTaskWaitsForItsWriterBeforeDeleting()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client(async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { canceled.TrySetResult(); await finish.Task; throw; }
            return Html("");
        });
        using var manager = new DownloadManager(Paths("remove"), Settings(), client);
        var task = manager.Enqueue(Details(42));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        File.WriteAllText(Path.Combine(task.FolderPath, "owned.txt"), "owned");
        manager.Remove(task, true);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        bool heldUntilWriterFinished = Directory.Exists(task.FolderPath);
        finish.TrySetResult();
        await WaitFor(() => manager.ActiveCount == 0 && !Directory.Exists(task.FolderPath));
        Assert.True(heldUntilWriterFinished);
        Assert.Empty(manager.Tasks);
    }

    [Fact]
    public async Task MovingRootWaitsForActiveWriterAndResumesInNewDirectory()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        using var client = Client(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/g/"))
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    started.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) { canceled.TrySetResult(); await stopped.Task; throw; }
                }
                return Html(PageList);
            }
            if (request.RequestUri.AbsolutePath.StartsWith("/s/")) return Html("<img id='img' src='https://images.test/image.png'>");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
        });
        var settings = Settings();
        using var manager = new DownloadManager(Paths("move-running"), settings, client);
        var task = manager.Enqueue(Details(42));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        string oldFolder = task.FolderPath;
        string target = Path.Combine(_root, "new-downloads");
        var moving = manager.RelocateRootAsync(target, null, CancellationToken.None);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        bool waitedForWriter = !moving.IsCompleted && Directory.Exists(oldFolder);
        stopped.TrySetResult();
        Assert.True(await moving);
        Assert.True(waitedForWriter);
        await WaitFor(() => task.State is DownloadState.Completed or DownloadState.Failed);
        Assert.Equal(DownloadState.Completed, task.State);
        Assert.True(PathIdentity.IsDescendantOf(task.FolderPath, target));
        Assert.True(File.Exists(Path.Combine(task.FolderPath, "00000001.png")));
        Assert.False(Directory.Exists(oldFolder));
    }

    private AppSettings Settings()
    {
        var settings = new AppSettings();
        settings.Download.RootPath = Path.Combine(_root, "downloads");
        settings.Download.MaxConcurrentTasks = 1;
        settings.Download.MaxConcurrentPages = 1;
        settings.Download.WriteThumb = false;
        return settings;
    }

    private AppPaths Paths(string name) { var paths = new AppPaths(Path.Combine(_root, name)); paths.EnsureCreated(); return paths; }
    private static EhGalleryDetails Details(long gid, int pages = 1) => new() { Gid = gid, Token = "tok", Title = "Regression", Pages = pages };
    private static EhPageRef Page() => new() { Gid = 42, Page = 1, Hash = "abc", PageUrl = "https://e-hentai.org/s/abc/42-1" };
    private const string PageList = "<div id='gdt'><a href='https://e-hentai.org/s/abc/42-1'><div title='Page 1'></div></a></div>";
    private static HttpResponseMessage Html(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static EhClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        var client = new EhClient(new AppSettings());
        foreach (string name in new[] { "_web", "_images" })
        {
            var field = typeof(EhClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((HttpClient)field.GetValue(client)!).Dispose();
            field.SetValue(client, new HttpClient(new Handler(send)));
        }
        return client;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private static async Task WaitFor(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
    public void Dispose() => Directory.Delete(_root, true);
}

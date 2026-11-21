using System.Net;
using System.Net.Http.Headers;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Core.Tests;

public sealed class UpstreamHostsServiceTests
{
    private const string Source = """
        static {
            Map<String, List<InetAddress>> map = new HashMap<>();
            if (Settings.getBuiltInHosts()) {
                put(map, "e-hentai.org", "172.66.132.196");
                put(map, "forums.e-hentai.org", "172.66.132.196");
                put(map, "upld.e-hentai.org", "89.149.221.236");
                put(map, "ehgt.org", "185.132.132.79");
            }
            if (Settings.getBuiltEXHosts()) {
                put(map, "exhentai.org", "178.175.128.251");
                put(map, "upld.exhentai.org", "178.175.132.22");
                put(map, "s.exhentai.org", "178.175.129.253");
            }
            builtInHosts = map;
        }
        """;

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(ProxyMode.EhDirect)]
    [InlineData(ProxyMode.Auto)]
    public async Task FirstLookupUsesSavedHostsAndPersistsNotModifiedCheckAcrossRestart(ProxyMode proxyMode)
    {
        using var directory = new TemporaryDirectory();
        var clock = new TestClock();
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SaveCache(directory.Paths, clock.Now.AddDays(-2));
        int requests = 0;
        using var service = CreateService(directory.Paths, clock, async (request, token) =>
        {
            Interlocked.Increment(ref requests);
            Assert.Equal(UpstreamHostsService.SourceUrl, request.RequestUri?.AbsoluteUri);
            Assert.Equal("\"stable\"", Assert.Single(request.Headers.IfNoneMatch).Tag);
            requestReceived.SetResult();
            await releaseResponse.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });

        service.Configure(new NetworkSettings { ProxyMode = proxyMode });
        Assert.Equal(0, Volatile.Read(ref requests));
        Assert.Equal([IPAddress.Parse("172.66.132.196")], await service.GetAddressesAsync("E-HENTAI.org.", default));
        await requestReceived.Task.WaitAsync(Deadline);
        Assert.NotNull(await service.GetAddressesAsync("ehgt.org", default));
        releaseResponse.SetResult();
        await service.RefreshCompletion.WaitAsync(Deadline);

        var saved = JsonFileStore.LoadOrDefault(directory.Paths.UpstreamHostsFile,
            StorageJsonContext.Default.UpstreamHostsCache, static () => new UpstreamHostsCache(), out _);
        Assert.Equal(Source, saved.Source);
        Assert.Equal(clock.Now, saved.SavedAt);
        Assert.Equal("\"stable\"", saved.ETag);
        service.Dispose();

        using var restarted = CreateService(directory.Paths, clock, (_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(ContentResponse(Source));
        });
        restarted.Configure(new NetworkSettings { ProxyMode = proxyMode });
        clock.Now += TimeSpan.FromHours(23);
        Assert.NotNull(await restarted.GetAddressesAsync("ehgt.org", default));
        await restarted.RefreshCompletion.WaitAsync(Deadline);
        Assert.Equal(1, Volatile.Read(ref requests));
    }

    [Fact]
    public async Task OkWithSameHostsPersistsNewTagAndCheckTime()
    {
        using var directory = new TemporaryDirectory();
        var clock = new TestClock();
        SaveCache(directory.Paths, clock.Now.AddDays(-2));
        using var service = CreateService(directory.Paths, clock, (_, _) => Task.FromResult(ContentResponse(Source)));
        service.Configure(new NetworkSettings { ProxyMode = ProxyMode.EhDirect });

        Assert.NotNull(await service.GetAddressesAsync("ehgt.org", default));
        await service.RefreshCompletion.WaitAsync(Deadline);

        var saved = JsonFileStore.LoadOrDefault(directory.Paths.UpstreamHostsFile,
            StorageJsonContext.Default.UpstreamHostsCache, static () => new UpstreamHostsCache(), out _);
        Assert.Equal(Source, saved.Source);
        Assert.Equal(clock.Now, saved.SavedAt);
        Assert.Equal("\"fresh\"", saved.ETag);
    }


    [Fact]
    public async Task InvalidRefreshKeepsLastGoodTableAndRetriesAfterDelay()
    {
        using var directory = new TemporaryDirectory();
        var clock = new TestClock();
        SaveCache(directory.Paths, clock.Now.AddDays(-2));
        string originalCache = File.ReadAllText(directory.Paths.UpstreamHostsFile);
        int requests = 0;
        using var service = CreateService(directory.Paths, clock, (_, _) => Task.FromResult(
            Interlocked.Increment(ref requests) == 1
                ? ContentResponse("invalid")
                : ContentResponse(Source.Replace("172.66.132.196", "104.20.18.168", StringComparison.Ordinal))));
        service.Configure(new NetworkSettings { ProxyMode = ProxyMode.EhDirect });

        Assert.Equal([IPAddress.Parse("172.66.132.196")], await service.GetAddressesAsync("e-hentai.org", default));
        await service.RefreshCompletion.WaitAsync(Deadline);
        Assert.Equal(originalCache, File.ReadAllText(directory.Paths.UpstreamHostsFile));
        _ = await service.GetAddressesAsync("e-hentai.org", default);
        Assert.Equal(1, Volatile.Read(ref requests));

        clock.Now += TimeSpan.FromMinutes(31);
        _ = await service.GetAddressesAsync("e-hentai.org", default);
        await service.RefreshCompletion.WaitAsync(Deadline);
        Assert.Equal([IPAddress.Parse("104.20.18.168")], await service.GetAddressesAsync("e-hentai.org", default));
        Assert.NotEqual(originalCache, File.ReadAllText(directory.Paths.UpstreamHostsFile));
        Assert.Equal(2, Volatile.Read(ref requests));
    }

    [Fact]
    public async Task DisablingDirectCancelsRefreshAndDoesNotPersistLateResponse()
    {
        using var directory = new TemporaryDirectory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = CreateService(directory.Paths, new TestClock(), async (_, token) =>
        {
            using CancellationTokenRegistration registration = token.Register(() => canceled.SetResult());
            started.SetResult();
            await canceled.Task;
            return ContentResponse(Source);
        });
        service.Configure(new NetworkSettings { ProxyMode = ProxyMode.System });
        Assert.Null(await service.GetAddressesAsync("e-hentai.org", default));
        Assert.False(started.Task.IsCompleted);

        service.Configure(new NetworkSettings { ProxyMode = ProxyMode.EhDirect });
        Assert.Null(await service.GetAddressesAsync("e-hentai.org", default));
        await started.Task.WaitAsync(Deadline);
        Task pending = service.RefreshCompletion;
        service.Configure(new NetworkSettings { ProxyMode = ProxyMode.System });
        await pending.WaitAsync(Deadline);

        Assert.True(canceled.Task.IsCompletedSuccessfully);
        Assert.False(File.Exists(directory.Paths.UpstreamHostsFile));
        Assert.Null(await service.GetAddressesAsync("e-hentai.org", default));
    }

    [Fact]
    public async Task DisposeWaitsForCanceledRefreshToFinish()
    {
        using var directory = new TemporaryDirectory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = CreateService(directory.Paths, new TestClock(), async (_, token) =>
        {
            using CancellationTokenRegistration registration = token.Register(() => canceled.TrySetResult());
            started.SetResult();
            await release.Task;
            return ContentResponse(Source);
        });
        service.Configure(new NetworkSettings { ProxyMode = ProxyMode.EhDirect });
        _ = await service.GetAddressesAsync("e-hentai.org", default);
        await started.Task.WaitAsync(Deadline);

        Task stop = Task.Run(service.Dispose);
        try
        {
            await canceled.Task.WaitAsync(Deadline);
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await stop.WaitAsync(Deadline);
        }

        Assert.False(File.Exists(directory.Paths.UpstreamHostsFile));
    }

    private static UpstreamHostsService CreateService(
        AppPaths paths, TestClock clock, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(paths, null, _ => new HttpClient(new TestHandler(send)), clock);

    private static void SaveCache(AppPaths paths, DateTimeOffset savedAt)
    {
        Assert.True(JsonFileStore.Save(paths.UpstreamHostsFile,
            new UpstreamHostsCache { Source = Source, ETag = "\"stable\"", SavedAt = savedAt },
            StorageJsonContext.Default.UpstreamHostsCache, out string? error), error);
    }

    private static HttpResponseMessage ContentResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content),
            Headers = { ETag = new EntityTagHeaderValue("\"fresh\"") },
        };

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("eh-upstream-hosts-");
        public AppPaths Paths => new(_directory.FullName);
        public void Dispose() => _directory.Delete(recursive: true);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Core.Online;

/// <summary>按站点直连请求更新上游地址表，并提供最近一次有效的快照。</summary>
internal sealed class UpstreamHostsService : IDisposable
{
    internal const string SourceUrl =
        "https://raw.githubusercontent.com/xiaojieonly/Ehviewer_CN_SXJ/HEAD/app/src/main/java/com/hippo/ehviewer/client/EhHosts.java";

    private const int MaxSourceBytes = 64 * 1024;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan FailureInterval = TimeSpan.FromMinutes(30);
    private static readonly UTF8Encoding SourceEncoding = new(false, true);

    private readonly AppPaths _paths;
    private readonly Action<string>? _log;
    private readonly Func<NetworkSettings, HttpClient> _createClient;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private Task? _loadTask;
    private Task? _refreshTask;
    private CancellationTokenSource? _refreshCancellation;
    private Snapshot? _snapshot;
    private DateTimeOffset _nextCheck;
    private int _timeoutSeconds;
    private bool _enabled;
    private bool _disposed;

    public UpstreamHostsService(AppPaths paths, Action<string>? log = null)
        : this(paths, log, EhHttp.CreateUpstreamHostsClient, TimeProvider.System)
    {
    }

    internal UpstreamHostsService(
        AppPaths paths, Action<string>? log, Func<NetworkSettings, HttpClient> createClient, TimeProvider time)
    {
        _paths = paths;
        _log = log;
        _createClient = createClient;
        _time = time;
    }

    public void Configure(NetworkSettings network)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _timeoutSeconds = network.TimeoutSeconds;
            // 自动模式只有选中直连处理器时才会请求地址表。
            _enabled = network.ProxyMode is ProxyMode.EhDirect or ProxyMode.Auto;
            if (!_enabled) _refreshCancellation?.Cancel();
        }
    }

    internal async ValueTask<IPAddress[]?> GetAddressesAsync(string host, CancellationToken token)
    {
        Task cacheLoad;
        lock (_gate)
        {
            if (!_enabled) return null;
            cacheLoad = _loadTask ??= Task.Run(LoadCache);
        }

        await cacheLoad.WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_enabled) return null;
            if (_refreshTask is null && _time.GetUtcNow() >= _nextCheck)
            {
                _nextCheck = _time.GetUtcNow() + FailureInterval;
                var cancellation = new CancellationTokenSource();
                _refreshCancellation = cancellation;
                Snapshot? prior = _snapshot;
                int timeoutSeconds = _timeoutSeconds;
                _refreshTask = Task.Run(() => RefreshAsync(prior, timeoutSeconds, cancellation));
            }

            return _snapshot?.Addresses.GetValueOrDefault(host.TrimEnd('.'));
        }
    }

    internal Task RefreshCompletion
    {
        get
        {
            lock (_gate) return _refreshTask ?? Task.CompletedTask;
        }
    }

    private void LoadCache()
    {
        UpstreamHostsCache cache = JsonFileStore.LoadOrDefault(
            _paths.UpstreamHostsFile, StorageJsonContext.Default.UpstreamHostsCache,
            static () => new UpstreamHostsCache(), out string? error);
        if (error is not null) _log?.Invoke("读取上游 hosts 缓存失败：" + error);
        if (cache.Source is null || cache.Source.Length > MaxSourceBytes ||
            !UpstreamHostsParser.TryParse(cache.Source, out Dictionary<string, IPAddress[]> addresses))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed) return;
            DateTimeOffset now = _time.GetUtcNow();
            _snapshot = new Snapshot(addresses, cache.Source, cache.ETag);
            _nextCheck = cache.SavedAt <= now && now - cache.SavedAt < CheckInterval
                ? cache.SavedAt + CheckInterval
                : now;
        }
    }

    private async Task RefreshAsync(Snapshot? prior, int timeoutSeconds, CancellationTokenSource cancellation)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 8, 20)));
            CancellationToken token = timeout.Token;
            token.ThrowIfCancellationRequested();
            using HttpClient client = _createClient(new NetworkSettings
            {
                ProxyMode = ProxyMode.EhDirect,
                TimeoutSeconds = timeoutSeconds,
            });
            using var request = new HttpRequestMessage(HttpMethod.Get, SourceUrl);
            if (prior?.ETag is { } tag && EntityTagHeaderValue.TryParse(tag, out EntityTagHeaderValue? entityTag) &&
                entityTag != EntityTagHeaderValue.Any)
            {
                request.Headers.IfNoneMatch.Add(entityTag);
            }

            using HttpResponseMessage response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            Snapshot fresh;
            if (response.StatusCode == HttpStatusCode.NotModified && prior is not null && request.Headers.IfNoneMatch.Count > 0)
            {
                fresh = prior with { ETag = response.Headers.ETag?.ToString() ?? prior.ETag };
            }
            else
            {
                response.EnsureSuccessStatusCode();
                if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("上游 hosts 响应状态异常。");
                string source = await ReadSourceAsync(response.Content, token).ConfigureAwait(false);
                if (!UpstreamHostsParser.TryParse(source, out Dictionary<string, IPAddress[]> addresses))
                {
                    throw new InvalidDataException("上游 hosts 表无法识别。");
                }

                fresh = new Snapshot(addresses, source, response.Headers.ETag?.ToString());
            }

            token.ThrowIfCancellationRequested();
            SaveSuccess(fresh, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 退出站点直连或关闭应用。
        }
        catch (Exception ex)
        {
            _log?.Invoke("更新上游 hosts 失败：" + ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                if (cancellation.IsCancellationRequested && _enabled) _nextCheck = _time.GetUtcNow();
                _refreshCancellation = null;
                _refreshTask = null;
                cancellation.Dispose();
            }
        }
    }

    private void SaveSuccess(Snapshot snapshot, CancellationTokenSource cancellation)
    {
        DateTimeOffset checkedAt = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_enabled || cancellation.IsCancellationRequested) return;
            _snapshot = snapshot;
            _nextCheck = checkedAt + CheckInterval;
        }

        // 保存新的校验时间和 ETag，重启后仍以这次校验为准。
        cancellation.Token.ThrowIfCancellationRequested();
        var file = new UpstreamHostsCache { Source = snapshot.Source, ETag = snapshot.ETag, SavedAt = checkedAt };
        if (!JsonFileStore.Save(_paths.UpstreamHostsFile, file, StorageJsonContext.Default.UpstreamHostsCache, out string? error))
        {
            lock (_gate) _nextCheck = _time.GetUtcNow() + FailureInterval;
            _log?.Invoke("保存上游 hosts 缓存失败：" + error);
        }
    }

    private static async Task<string> ReadSourceAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > MaxSourceBytes) throw new InvalidDataException("上游 hosts 表超出大小限制。");
        await using Stream stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        byte[] bytes = new byte[MaxSourceBytes + 1];
        int count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, token)
            .ConfigureAwait(false);
        if (count > MaxSourceBytes) throw new InvalidDataException("上游 hosts 表超出大小限制。");
        return SourceEncoding.GetString(bytes, 0, count);
    }

    public void Dispose()
    {
        Task? load;
        Task? refresh;
        lock (_gate)
        {
            _disposed = true;
            _enabled = false;
            load = _loadTask;
            refresh = _refreshTask;
            _refreshCancellation?.Cancel();
        }

        load?.GetAwaiter().GetResult();
        refresh?.GetAwaiter().GetResult();
    }

    private sealed record Snapshot(Dictionary<string, IPAddress[]> Addresses, string Source, string? ETag);
}

public sealed class UpstreamHostsCache
{
    public string? Source { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset SavedAt { get; set; }
}

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EhViewerForPc.Services;

internal sealed class MangaTranslationAssetInstaller(string? cache, IProgress<MangaTranslationInstallProgress>? progress,
    HttpClient? client = null, TimeSpan? inactivityTimeout = null) : IDisposable
{
    private const int RangeSize = 8 * 1024 * 1024;
    private const int CopyBufferSize = 128 * 1024;
    private static readonly Lazy<Asset[]> Manifest = new(ReadManifest);
    private HttpClient? _http;

    private readonly TimeSpan _inactivityTimeout = inactivityTimeout ?? TimeSpan.FromSeconds(20);

    private sealed record Asset(string Name, long Bytes, byte[] Hash, Uri[] Sources);

    private readonly record struct Activity(MangaTranslationInstallStage Stage, long Bytes = 0, Uri? Source = null, string? Detail = null);

    private HttpClient Http => client ?? (_http ??= new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(20),
    }) { Timeout = Timeout.InfiniteTimeSpan });

    public async Task<string> InstallAsync(string models, CancellationToken token)
    {
        Asset[] assets = Manifest.Value;
        long total = assets.Sum(static asset => asset.Bytes);
        long completed = 0;
        var signature = new StringBuilder();
        foreach (Asset asset in assets)
        {
            token.ThrowIfCancellationRequested();
            string destination = Path.Combine(models, asset.Name);
            void Report(Activity activity) => progress?.Report(new MangaTranslationInstallProgress(
                Describe(asset, activity), activity.Stage is MangaTranslationInstallStage.Downloading or MangaTranslationInstallStage.Copying
                    ? 0.96 * (completed + activity.Bytes) / total : -1,
                activity.Stage, asset.Name, activity.Bytes, asset.Bytes, activity.Source?.Host));
            Report(new Activity(MangaTranslationInstallStage.Preparing));
            await PrepareAssetAsync(asset, destination, Report, token).ConfigureAwait(false);
            if (!TryAppendModelSignature(signature, destination, asset)) throw new IOException("已验证的模型文件已变化。");
            completed += asset.Bytes;
        }
        return signature.ToString();
    }

    private static string Describe(Asset asset, Activity activity) => activity.Stage switch
    {
        MangaTranslationInstallStage.Connecting => $"正在连接 {activity.Source!.Host}，准备下载 {asset.Name}…",
        MangaTranslationInstallStage.Downloading => $"正在下载 {asset.Name}（{FormatBytes(activity.Bytes)} / {FormatBytes(asset.Bytes)}）…",
        MangaTranslationInstallStage.Retrying => $"{asset.Name}：{activity.Detail}，正在重试…",
        MangaTranslationInstallStage.Copying => $"正在复制本地缓存 {asset.Name}（{FormatBytes(activity.Bytes)} / {FormatBytes(asset.Bytes)}）…",
        MangaTranslationInstallStage.Verifying => $"正在校验 {asset.Name}…",
        _ => $"正在准备 {asset.Name}…",
    };

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KiB",
        _ => $"{bytes / (1024d * 1024):0.#} MiB",
    };

    public static string? GetModelSignature(string directory)
    {
        if (!MangaTranslationBackendService.IsLocalDirectory(directory)) return null;
        var signature = new StringBuilder();
        foreach (Asset asset in Manifest.Value)
        {
            if (!TryAppendModelSignature(signature, Path.Combine(directory, asset.Name), asset))
                return null;
        }
        return signature.Length > 0 ? signature.ToString() : null;
    }

    private static bool TryAppendModelSignature(StringBuilder signature, string path, Asset asset)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length != asset.Bytes)
                return false;
            signature.Append(CultureInfo.InvariantCulture,
                $"{asset.Name}\t{file.Length}\t{file.CreationTimeUtc.Ticks}\t{file.LastWriteTimeUtc.Ticks}\n");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private async Task PrepareAssetAsync(Asset asset, string destination, Action<Activity> report, CancellationToken token)
    {
        if (File.Exists(destination))
        {
            report(new Activity(MangaTranslationInstallStage.Verifying));
            if (!await MatchesAsync(destination, asset, token).ConfigureAwait(false))
                throw ConflictingModel(asset);
            return;
        }

        string directory = Path.GetDirectoryName(destination)!;
        string temporary = Path.Combine(directory, ".translation-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            if (cache is not null)
            {
                string source = Path.Combine(cache, "models", asset.Name);
                if (!MangaTranslationBackendService.IsLocalFile(source, asset.Bytes))
                    throw new MangaTranslationException($"离线资源缓存缺少 {asset.Name} 或文件大小不符。");
                await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, true);
                report(new Activity(MangaTranslationInstallStage.Copying));
                await CopyAssetAsync(input, temporary, asset.Bytes,
                    bytes => report(new Activity(MangaTranslationInstallStage.Copying, bytes)), token).ConfigureAwait(false);
                report(new Activity(MangaTranslationInstallStage.Verifying));
                if (!await MatchesAsync(temporary, asset, token).ConfigureAwait(false))
                    throw new MangaTranslationException($"离线资源缓存中的 {asset.Name} 校验失败。");
            }
            else
            {
                await DownloadVerifiedAsync(asset, temporary, report, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            if (!MangaTranslationBackendService.IsLocalDirectory(directory)) throw new IOException("资源目录已变更。");
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination))
            {
                if (!await MatchesAsync(destination, asset, token).ConfigureAwait(false))
                    throw ConflictingModel(asset);
            }
        }
        finally { TryDeleteTemporary(temporary); }
    }

    private static MangaTranslationException ConflictingModel(Asset asset) => new(
        $"现有 {asset.Name} 校验失败。请选择其他模型目录，或将该文件移出当前目录后重试。");

    private async Task DownloadVerifiedAsync(Asset asset, string temporary, Action<Activity> report, CancellationToken token)
    {
        string? lastDetail = null;
        foreach (Uri source in asset.Sources)
        {
            if (lastDetail is not null) report(new Activity(MangaTranslationInstallStage.Retrying, Source: source, Detail: lastDetail));
            if (asset.Bytes > 32 * 1024 * 1024)
            {
                lastDetail = await TryDownloadAsync(asset, source, temporary, report, true, token).ConfigureAwait(false);
                if (lastDetail is null) return;
                report(new Activity(MangaTranslationInstallStage.Retrying, Source: source, Detail: lastDetail));
            }
            lastDetail = await TryDownloadAsync(asset, source, temporary, report, false, token).ConfigureAwait(false);
            if (lastDetail is null) return;
        }
        throw new MangaTranslationException($"{asset.Name} 下载失败。最后一个下载源 {asset.Sources[^1].Host}：{lastDetail}。请检查网络后重试。");
    }

    private async Task<string?> TryDownloadAsync(Asset asset, Uri source, string temporary, Action<Activity> report,
        bool byRange, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        report(new Activity(MangaTranslationInstallStage.Connecting, Source: source));
        TryDeleteTemporary(temporary);
        try
        {
            if (byRange)
                await DownloadRangesAsync(asset, source, temporary, report, token).ConfigureAwait(false);
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(_inactivityTimeout);
                using HttpResponseMessage response = await SendAsync(source, null, null, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } length && length != asset.Bytes)
                    throw new InvalidDataException("下载资源大小不符");
                await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await CopyAssetAsync(body, temporary, asset.Bytes,
                    bytes => report(new Activity(MangaTranslationInstallStage.Downloading, bytes, source)),
                    token, timeout).ConfigureAwait(false);
            }

            report(new Activity(MangaTranslationInstallStage.Verifying, asset.Bytes, source));
            return await MatchesAsync(temporary, asset, token).ConfigureAwait(false) ? null : "SHA-256 校验不符";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return "等待响应或数据超时";
        }
        catch (HttpRequestException ex)
        {
            return ex.StatusCode is { } status ? $"服务器返回 HTTP {(int)status}" : "网络连接失败或下载中断";
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return ex.Message;
        }
    }

    private async Task DownloadRangesAsync(Asset asset, Uri source, string temporary, Action<Activity> report, CancellationToken token)
    {
        using (var probe = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            probe.CancelAfter(_inactivityTimeout);
            using HttpResponseMessage response = await SendAsync(source, 0, 0, probe.Token).ConfigureAwait(false);
            RequireRange(response, 0, 0, asset.Bytes);
        }

        using SafeFileHandle file = File.OpenHandle(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.Asynchronous);
        RandomAccess.SetLength(file, asset.Bytes);
        long received = 0;
        var reporting = new object();
        long lastReported = 0;
        long lastUpdate = Stopwatch.GetTimestamp();
        int ranges = (int)((asset.Bytes + RangeSize - 1) / RangeSize);
        var options = new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token };
        await Parallel.ForEachAsync(Enumerable.Range(0, ranges), options, async (index, stop) =>
        {
            long first = (long)index * RangeSize;
            long last = Math.Min(first + RangeSize, asset.Bytes) - 1;
            long mostReceived = 0;
            void ReportRange(long bytes)
            {
                if (bytes <= mostReceived) return;
                lock (reporting)
                {
                    received += bytes - mostReceived;
                    mostReceived = bytes;
                    if (lastReported == 0 || received == asset.Bytes ||
                        Stopwatch.GetElapsedTime(lastUpdate) >= TimeSpan.FromMilliseconds(250))
                    {
                        report(new Activity(MangaTranslationInstallStage.Downloading, received, source));
                        lastReported = received;
                        lastUpdate = Stopwatch.GetTimestamp();
                    }
                }
            }

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    timeout.CancelAfter(_inactivityTimeout);
                    using HttpResponseMessage response = await SendAsync(source, first, last, timeout.Token).ConfigureAwait(false);
                    RequireRange(response, first, last, asset.Bytes);
                    await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    await CopyRangeAsync(body, file, first, last - first + 1, ReportRange, stop, timeout).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (attempt == 0 && mostReceived > 0 && !stop.IsCancellationRequested &&
                    ex is HttpRequestException { StatusCode: null } or OperationCanceledException or EndOfStreamException)
                {
                    lock (reporting)
                        report(new Activity(MangaTranslationInstallStage.Retrying, received, source, "下载分段中断"));
                }
            }
        }).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, long? first, long? last, CancellationToken token)
    {
        for (int redirects = 0; redirects < 8; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps) throw new HttpRequestException("下载源必须使用 HTTPS。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // ModelScope 的文件 CDN 要求请求声明客户端身份，重定向后同样发送。
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("EhViewerForPc", "1.0"));
            if (first is not null) request.Headers.Range = new RangeHeaderValue(first, last);
            HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            Uri? location = response.Headers.Location;
            response.Dispose();
            if (location is null) break;
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new HttpRequestException("无法解析下载地址重定向。");
    }

    private static void RequireRange(HttpResponseMessage response, long start, long end, long length)
    {
        response.EnsureSuccessStatusCode();
        ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" ||
            range.From != start || range.To != end || range.Length != length)
            throw new InvalidDataException("下载源返回的文件区间不符。");
    }

    private async Task CopyAssetAsync(Stream input, string temporary, long length, Action<long> report,
        CancellationToken token, CancellationTokenSource? networkTimeout = null)
    {
        using SafeFileHandle output = File.OpenHandle(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.Asynchronous);
        await CopyRangeAsync(input, output, 0, length, report, token, networkTimeout).ConfigureAwait(false);
    }

    private async Task CopyRangeAsync(Stream input, SafeFileHandle output, long start, long length, Action<long> report,
        CancellationToken token, CancellationTokenSource? networkTimeout)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long copied = 0;
            long lastReport = Stopwatch.GetTimestamp();
            while (copied < length)
            {
                int size = (int)Math.Min(CopyBufferSize, length - copied);
                int count = await ReadChunkAsync(input, buffer.AsMemory(0, size), token, networkTimeout).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("传输提前中断，文件不完整");
                await RandomAccess.WriteAsync(output, buffer.AsMemory(0, count), start + copied, token).ConfigureAwait(false);
                copied += count;
                if (copied == count || copied == length || Stopwatch.GetElapsedTime(lastReport) >= TimeSpan.FromMilliseconds(250))
                {
                    report(copied);
                    lastReport = Stopwatch.GetTimestamp();
                }
            }
            if (await ReadChunkAsync(input, buffer.AsMemory(0, 1), token, networkTimeout).ConfigureAwait(false) != 0)
                throw new InvalidDataException("下载资源超过了预期大小");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async ValueTask<int> ReadChunkAsync(Stream stream, Memory<byte> buffer, CancellationToken token,
        CancellationTokenSource? networkTimeout)
    {
        if (networkTimeout is null) return await stream.ReadAsync(buffer, token).ConfigureAwait(false);
        networkTimeout.CancelAfter(_inactivityTimeout);
        try { return await stream.ReadAsync(buffer, networkTimeout.Token).ConfigureAwait(false); }
        catch (IOException ex) { throw new HttpRequestException("读取下载内容时连接中断。", ex); }
        finally { networkTimeout.CancelAfter(Timeout.InfiniteTimeSpan); }
    }

    private static async Task<bool> MatchesAsync(string file, Asset asset, CancellationToken token)
    {
        if (!MangaTranslationBackendService.IsLocalFile(file, asset.Bytes)) return false;
        await using FileStream input = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, true);
        byte[] actual = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
        return actual.AsSpan().SequenceEqual(asset.Hash);
    }

    private static Asset[] ReadManifest()
    {
        var assets = new List<Asset>();
        using Stream input = typeof(MangaTranslationAssetInstaller).Assembly.GetManifestResourceStream("MangaTranslation.Assets")
            ?? throw new InvalidDataException("发行包缺少内置翻译模型清单。");
        using var reader = new StreamReader(input);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] fields = line.Split('\t');
            if (fields.Length < 5 || fields[0] != "model" ||
                fields[1] != Path.GetFileName(fields[1]) ||
                !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) || bytes <= 0)
                throw new InvalidDataException("内置翻译资源清单无效。");
            assets.Add(new Asset(fields[1], bytes, Convert.FromHexString(fields[3]),
                fields.Skip(4).Select(static value => new Uri(value)).ToArray()));
        }
        return assets.Count > 0 ? assets.ToArray() : throw new InvalidDataException("内置翻译模型清单为空。");
    }

    private static void TryDeleteTemporary(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => _http?.Dispose();
}

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;
using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Services;

internal readonly record struct ReaderTranslationImage(string Path, long Length, DateTime ModifiedUtc, bool IsAnimated = false)
{
    public static ReaderTranslationImage? TryCapture(string path, FileStream stream)
    {
        try
        {
            return new ReaderTranslationImage(path, stream.Length, File.GetLastWriteTimeUtc(stream.SafeFileHandle));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 无法取得文件版本时仍可阅读，翻译需要能确认是同一份图片。
            return null;
        }
    }

    public bool Matches(FileStream stream) => TryCapture(Path, stream) is { } current &&
        Length == current.Length && ModifiedUtc == current.ModifiedUtc;

    public FileStream? TryOpenCurrentSource()
    {
        // 用同一个句柄校验和读取，允许下载原子替换目录项，同时阻止原地写入此输入。
        FileStream source = ImageDecoder.OpenRead(Path, FileShare.Read | FileShare.Delete);
        if (Matches(source)) return source;
        source.Dispose();
        return null;
    }
}

internal sealed class MangaTranslationService(AppPaths paths, ImageDecoder decoder, LogService log,
    MangaTranslationBackendService backend, Func<MangaTranslationPreferences> getPreferences) : IDisposable
{
    private const int CachedPages = 8;
    private static readonly TimeSpan WorkerIdleTimeout = TimeSpan.FromSeconds(30);
    internal const int MaxResponseBytes = 512 * 1024;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task _lastJob = Task.CompletedTask;
    private bool _disposed;
    private readonly LinkedList<CachedPage> _cachedPages = new();
    private Timer? _idleTimer;
    private long _lastWorkerUse;
    private MangaTranslationConnection? _worker;

    private sealed record CachedPage(ReaderTranslationImage Image, MangaTranslationBackendPaths Resources,
        MangaTranslationLanguageOption Language, MangaTranslationResponse Result);

    public Task<MangaTranslationResponse> TranslateAsync(ReaderTranslationImage image, CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            MangaTranslationPreferences preferences = getPreferences();
            Task previous = _lastJob;
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _lastJob = completed.Task;
            return Task.Run(async () =>
            {
                try
                {
                    // 快速翻页后可立即提交新页，等前一次推理完成取消后再占用 GPU。
                    await previous.ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
                    timeout.Token.ThrowIfCancellationRequested();
                    timeout.CancelAfter(TimeSpan.FromMinutes(5));
                    try
                    {
                        return await TranslateCoreAsync(image, preferences, timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                    {
                        throw new MangaTranslationException("本页翻译超时，请重试。");
                    }
                }
                finally
                {
                    lock (_gate)
                    {
                        completed.SetResult();
                        if (!_disposed && ReferenceEquals(_lastJob, completed.Task) && _worker is not null)
                        {
                            _lastWorkerUse = Stopwatch.GetTimestamp();
                            _idleTimer ??= new Timer(static state => ((MangaTranslationService)state!).ReleaseIdleWorker(),
                                this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                            _idleTimer.Change(WorkerIdleTimeout, Timeout.InfiniteTimeSpan);
                        }
                    }
                }
            });
        }
    }

    private async Task<MangaTranslationResponse> TranslateCoreAsync(ReaderTranslationImage image, MangaTranslationPreferences preferences,
        CancellationToken token)
    {
        MangaTranslationBackendPaths resources;
        try
        {
            if (!MangaTranslationBackendService.HasNativeLibraries())
                throw new MangaTranslationException(MangaTranslationBackendService.MissingNativeLibrariesMessage, requiresSetup: true);
            resources = backend.Resolve(preferences.ModelDirectory, preferences.Model) ??
                throw new MangaTranslationException("本地翻译模型尚未就绪，请在漫画翻译设置中下载或校验。", requiresSetup: true);
        }
        catch (MangaTranslationException)
        {
            await StopWorkerAsync().ConfigureAwait(false);
            throw;
        }

        if (_worker is { } existing && !existing.Matches(resources, preferences.Language))
            await StopWorkerAsync().ConfigureAwait(false);

        string inputRoot = Path.GetFullPath(Path.Combine(paths.MangaTranslationDirectory, "input"));
        if (!IsLocalInputDirectory(inputRoot))
        {
            throw new MangaTranslationException("本页翻译需要本机磁盘上的实际应用数据目录。请检查数据目录设置后重试。");
        }

        string jobDirectory = Path.GetFullPath(Path.Combine(inputRoot, Guid.NewGuid().ToString("N")));
        string snapshot = Path.Combine(jobDirectory, "page.png");
        bool createdJobDirectory = false;
        try
        {
            for (LinkedListNode<CachedPage>? item = _cachedPages.First; item is not null; item = item.Next)
            {
                CachedPage cached = item.Value;
                if (cached.Image != image || cached.Resources != resources || cached.Language != preferences.Language) continue;
                using FileStream source = OpenCurrentSource(image);
                token.ThrowIfCancellationRequested();
                _cachedPages.Remove(item);
                _cachedPages.AddFirst(item);
                return cached.Result;
            }

            Directory.CreateDirectory(jobDirectory);
            createdJobDirectory = true;
            int width;
            int height;
            using (DecodedImage decoded = DecodeSnapshot(image, token))
            {
                token.ThrowIfCancellationRequested();
                using var output = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                decoded.Bitmap.Save(output, PngBitmapEncoderOptions.Default);
                width = decoded.DecodedWidth;
                height = decoded.DecodedHeight;
            }

            token.ThrowIfCancellationRequested();
            _worker ??= new MangaTranslationConnection(
                MangaTranslationWorker.CreateStartInfo(snapshot, resources, preferences.Language, persistent: true),
                resources, preferences.Language);
            MangaTranslationResponse result;
            try
            {
                result = await _worker.TranslateAsync(snapshot, width, height, token).ConfigureAwait(false);
                if (result.ModelId != preferences.Model.Id || result.TargetLanguage != preferences.Language.Id)
                    throw InvalidResponse();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested && _worker.IsUsable) { throw; }
            catch
            {
                await StopWorkerAsync().ConfigureAwait(false);
                throw;
            }

            _cachedPages.AddFirst(new CachedPage(image, resources, preferences.Language, result));
            if (_cachedPages.Count > CachedPages) _cachedPages.RemoveLast();
            return result;
        }
        catch (FileNotFoundException)
        {
            throw new MangaTranslationException("本页图片已不可用，请重新打开这一页。");
        }
        catch (DirectoryNotFoundException)
        {
            throw new MangaTranslationException("本页图片已不可用，请重新打开这一页。");
        }
        catch (UnauthorizedAccessException)
        {
            throw new MangaTranslationException("本页翻译无法写入暂存目录，请检查应用数据目录权限。");
        }
        finally
        {
            // 页图暂存于独占的一级目录，工作进程确认处理结束后再清理。
            if (createdJobDirectory && PathIdentity.Comparer.Equals(Path.GetDirectoryName(jobDirectory), inputRoot))
            {
                try { Directory.Delete(jobDirectory, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.Warn("漫画翻译临时文件清理失败：" + ex.GetType().Name);
                }
            }
        }
    }

    internal static bool IsLocalInputDirectory(string path) => MangaTranslationBackendService.IsLocalDirectory(path);

    private DecodedImage DecodeSnapshot(ReaderTranslationImage image, CancellationToken token)
    {
        using FileStream source = OpenCurrentSource(image);
        return decoder.DecodeReaderImage(image.Path, source, token);
    }

    private static FileStream OpenCurrentSource(ReaderTranslationImage image) => image.TryOpenCurrentSource() ??
        throw new MangaTranslationException("本页图片已变更，请重新打开这一页。");

    internal static MangaTranslationException WorkerError(int code) => new(code switch
    {
        MangaTranslationWorker.ResourcesUnavailable => "本地翻译资源不完整，请检查模型设置与发行包。",
        MangaTranslationWorker.InvalidImage => "本页图片无法识别，或超出翻译后端的大小限制。",
        MangaTranslationWorker.OcrFailed => "GPU 文字识别失败，请检查 DirectML 运行时和显卡驱动。",
        MangaTranslationWorker.TranslationFailed => "GPU 翻译失败，请检查 Vulkan 运行时和翻译模型。",
        _ => "本地漫画翻译后端运行失败。",
    }, requiresSetup: code == MangaTranslationWorker.ResourcesUnavailable);

    internal static MangaTranslationResponse ParseResponse(string json, int width, int height)
    {
        MangaTranslationResponse? result;
        try { result = JsonSerializer.Deserialize(json, MangaTranslationJsonContext.Default.MangaTranslationResponse); }
        catch (JsonException) { throw InvalidResponse(); }
        if (result is not { SchemaVersion: 1, OcrProvider: MangaOcrOrt.Provider, Regions: not null } ||
            result.Width != width || result.Height != height || result.Regions.Length > 120)
            throw InvalidResponse();

        bool valid = result.Regions.Length == 0 ? result.TranslationBackend is null :
            result.TranslationBackend?.StartsWith("Vulkan", StringComparison.Ordinal) == true &&
            Array.TrueForAll(result.Regions, region => region?.SourceText is not null && region.TranslatedText is not null &&
                region.HasValidBounds(width, height));
        if (!valid) throw InvalidResponse();
        return result;
    }

    internal static MangaTranslationException InvalidResponse() => new("本地漫画翻译后端返回了无法使用的结果。");

    internal void RequestWorkerRelease()
    {
        lock (_gate)
        {
            if (_disposed) return;
            QueueWorkerRelease();
        }
    }

    private void ReleaseIdleWorker()
    {
        lock (_gate)
        {
            if (_disposed || !_lastJob.IsCompleted) return;
            TimeSpan remaining = WorkerIdleTimeout - Stopwatch.GetElapsedTime(_lastWorkerUse);
            if (remaining > TimeSpan.Zero)
            {
                _idleTimer?.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }
            QueueWorkerRelease();
        }
    }

    // 和页请求共用同一条任务链，先等在途快照和原生调用收尾，再关闭工作进程。
    private void QueueWorkerRelease()
    {
        _idleTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Task pending = _lastJob;
        _lastJob = Task.Run(async () =>
        {
            await pending.ConfigureAwait(false);
            await StopWorkerAsync().ConfigureAwait(false);
        });
    }

    private async Task StopWorkerAsync()
    {
        MangaTranslationConnection? worker = _worker;
        _worker = null;
        if (worker is null) return;
        try { await worker.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log.Warn("漫画翻译后端清理失败：" + ex.GetType().Name);
        }
    }

    public void Dispose()
    {
        Task pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _idleTimer?.Dispose();
            pending = _lastJob;
        }

        _lifetime.Cancel();
        try
        {
            pending.GetAwaiter().GetResult();
            StopWorkerAsync().GetAwaiter().GetResult();
        }
        finally
        {
            _cachedPages.Clear();
            _lifetime.Dispose();
        }
    }
}

internal sealed record MangaTranslationRegion(string SourceText, string TranslatedText,
    int X, int Y, int Width, int Height)
{
    public bool HasValidBounds(int imageWidth, int imageHeight) =>
        X >= 0 && Y >= 0 && Width > 0 && Height > 0 && X <= imageWidth && Y <= imageHeight &&
        Width <= imageWidth - X && Height <= imageHeight - Y;
}

internal sealed record MangaTranslationResponse(int SchemaVersion, int Width, int Height, string OcrProvider,
    string? TranslationBackend, MangaTranslationRegion[] Regions)
{
    public string ModelId { get; init; } = "";
    public string TargetLanguage { get; init; } = "zh-CN";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MangaTranslationResponse))]
internal sealed partial class MangaTranslationJsonContext : JsonSerializerContext;

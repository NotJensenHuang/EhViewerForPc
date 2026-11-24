using System.Diagnostics;
using System.Text;
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
}

internal sealed class MangaTranslationService(AppPaths paths, ImageDecoder decoder, LogService log,
    MangaTranslationBackendService backend, Func<string?> getModelDirectory) : IDisposable
{
    private const int MaxResponseCharacters = 512 * 1024;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task _lastJob = Task.CompletedTask;
    private bool _disposed;

    public Task<MangaTranslationResponse> TranslateAsync(ReaderTranslationImage image, CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Task previous = _lastJob;
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _lastJob = completed.Task;
            return Task.Run(async () =>
            {
                try
                {
                    // 快速翻页后可立即提交新页，等前一个进程退出再占用 GPU。
                    await previous.ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
                    timeout.Token.ThrowIfCancellationRequested();
                    timeout.CancelAfter(TimeSpan.FromMinutes(5));
                    try
                    {
                        return await TranslateCoreAsync(image, timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                    {
                        throw new MangaTranslationException("本页翻译超时，请重试。");
                    }
                }
                finally
                {
                    completed.SetResult();
                }
            });
        }
    }

    private async Task<MangaTranslationResponse> TranslateCoreAsync(ReaderTranslationImage image, CancellationToken token)
    {
        if (!MangaTranslationBackendService.HasNativeLibraries())
            throw new MangaTranslationException(MangaTranslationBackendService.MissingNativeLibrariesMessage);

        MangaTranslationBackendPaths resources = backend.Resolve(getModelDirectory()) ??
            throw new MangaTranslationException("本地翻译模型尚未就绪，请在漫画翻译设置中下载或校验。", requiresSetup: true);

        string inputRoot = Path.GetFullPath(Path.Combine(paths.MangaTranslationDirectory, "input"));
        if (!IsLocalInputDirectory(inputRoot))
        {
            throw new MangaTranslationException("本页翻译需要本机磁盘上的实际应用数据目录。请检查数据目录设置后重试。");
        }

        string jobDirectory = Path.GetFullPath(Path.Combine(inputRoot, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(jobDirectory);
        string snapshot = Path.Combine(jobDirectory, "page.png");
        try
        {
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

            return await RunWorkerAsync(MangaTranslationWorker.CreateStartInfo(snapshot, resources), width, height, token)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            throw new MangaTranslationException("本页图片已不可用，请重新打开这一页。");
        }
        catch (DirectoryNotFoundException)
        {
            throw new MangaTranslationException("本页图片已不可用，请重新打开这一页。");
        }
        finally
        {
            // 页图暂存于独占的一级目录，工作进程退出后再清理。
            if (PathIdentity.Comparer.Equals(Path.GetDirectoryName(jobDirectory), inputRoot))
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
        // 校验和解码共用文件句柄，允许下载服务原子更换路径，同时阻止原地写入这份输入。
        using FileStream source = ImageDecoder.OpenRead(image.Path, FileShare.Read | FileShare.Delete);
        if (!image.Matches(source))
        {
            throw new MangaTranslationException("本页图片已变更，请重新打开这一页。");
        }

        return decoder.DecodeTranslationPage(image.Path, source, token);
    }

    internal static async Task<MangaTranslationResponse> RunWorkerAsync(
        ProcessStartInfo start, int width, int height, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using Process process = Process.Start(start) ?? throw new MangaTranslationException("无法启动本地漫画翻译后端。");
        Task errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
        Task<string> output = ReadResponseAsync(process.StandardOutput, token);
        try
        {
            // 等待可以独立于管道 I/O 取消，进程树退出后再收尾输出读取。
            string json = await output.WaitAsync(token).ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                throw new MangaTranslationException(process.ExitCode switch
                {
                    MangaTranslationWorker.ResourcesUnavailable => "本地翻译资源不完整，请检查模型设置与发行包。",
                    MangaTranslationWorker.InvalidImage => "本页图片无法识别，或超出翻译后端的大小限制。",
                    MangaTranslationWorker.OcrFailed => "GPU 文字识别失败，请检查 DirectML 运行时和显卡驱动。",
                    MangaTranslationWorker.TranslationFailed => "GPU 翻译失败，请检查 Vulkan 运行时和翻译模型。",
                    _ => "本地漫画翻译后端运行失败。",
                }, requiresSetup: process.ExitCode == MangaTranslationWorker.ResourcesUnavailable);
            }

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
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
            }

            try { await Task.WhenAll(errors, output).ConfigureAwait(false); }
            catch (Exception) { /* 上方已处理进程结果，收尾只观察管道读取。 */ }
        }
    }

    private static MangaTranslationException InvalidResponse() => new("本地漫画翻译后端返回了无法使用的结果。");

    private static async Task<string> ReadResponseAsync(StreamReader reader, CancellationToken token)
    {
        var response = new StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (count > MaxResponseCharacters - response.Length)
            {
                throw new MangaTranslationException("本页翻译结果超出大小限制。");
            }

            response.Append(buffer, 0, count);
        }

        return response.ToString();
    }

    public void Dispose()
    {
        Task pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _lastJob;
        }

        _lifetime.Cancel();
        try { pending.GetAwaiter().GetResult(); }
        finally { _lifetime.Dispose(); }
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
    string? TranslationBackend, MangaTranslationRegion[] Regions);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MangaTranslationResponse))]
internal sealed partial class MangaTranslationJsonContext : JsonSerializerContext;

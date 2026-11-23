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

internal sealed class MangaTranslationService(AppPaths paths, ImageDecoder decoder, LogService log) : IDisposable
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
        string? configured = Environment.GetEnvironmentVariable("EHVIEWER_MANGA_TRANSLATION_BACKEND");
        if (!string.IsNullOrWhiteSpace(configured) && !Path.IsPathFullyQualified(configured))
        {
            throw new MangaTranslationException("本地翻译后端路径需要是绝对路径。");
        }

        string backend = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(paths.MangaTranslationDirectory, "backend")
            : Path.GetFullPath(configured);
        if (!IsOnLocalDrive(backend))
        {
            throw new MangaTranslationException("本地漫画翻译后端需要安装在本机磁盘。");
        }

        string python = Path.Combine(backend, "python", "python.exe");
        string worker = Path.Combine(backend, "worker.py");
        if (!File.Exists(python) || !File.Exists(worker))
        {
            throw new MangaTranslationException("未安装本地漫画翻译后端，请先准备 GPU 运行时和模型。");
        }

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

            return await RunWorkerAsync(python, worker, backend, snapshot, width, height, token).ConfigureAwait(false);
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
            // ONNX 性能记录也放在作业目录，退出后一起移除。只删除独占的一级子目录。
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

    private static bool IsOnLocalDrive(string path)
    {
        if (!OperatingSystem.IsWindows()) return true;
        string? drive = Path.GetPathRoot(path);
        if (drive?.StartsWith(@"\\?\", StringComparison.Ordinal) == true) drive = drive[4..];
        return drive is { Length: 3 } && drive[1] == ':' &&
            new DriveInfo(drive).DriveType is DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram;
    }

    internal static bool IsLocalInputDirectory(string path)
    {
        if (!IsOnLocalDrive(path)) return false;

        string root = Path.GetPathRoot(path)!;
        string directory = root;
        // 从盘符向下检查，遇到联接立即返回，避免通过它访问后续目录。
        foreach (string segment in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            directory = Path.Combine(directory, segment);
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        return true;
    }

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

    private static async Task<MangaTranslationResponse> RunWorkerAsync(
        string python, string worker, string backend, string snapshot, int width, int height, CancellationToken token)
    {
        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = backend,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
        };
        foreach (string argument in new[] { "-I", worker, "--image", snapshot, "--backend-dir", backend, "--source", "ja", "--target", "zh-Hans" })
        {
            start.ArgumentList.Add(argument);
        }

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
                    3 => "本地漫画翻译后端的运行时或模型不完整。",
                    4 => "本页图片无法识别，或超出翻译后端的大小限制。",
                    5 => "GPU 文字识别失败，请检查 DirectML 运行时和显卡驱动。",
                    6 => "GPU 翻译失败，请检查 Vulkan 运行时和翻译模型。",
                    _ => "本地漫画翻译后端运行失败。",
                });
            }

            MangaTranslationResponse? result = JsonSerializer.Deserialize(json, MangaTranslationJsonContext.Default.MangaTranslationResponse);
            if (result is not { SchemaVersion: 1, OcrProvider: "DmlExecutionProvider", Regions: not null } ||
                result.Width != width || result.Height != height || result.Regions.Length > 1000 ||
                result.Regions.Length > 0 && (result.TranslationBackend?.StartsWith("Vulkan", StringComparison.Ordinal) != true ||
                    Array.Exists(result.Regions, static region => region?.SourceText is null || region.TranslatedText is null)))
            {
                throw new MangaTranslationException("本地漫画翻译后端返回了无法使用的结果。");
            }

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

internal sealed class MangaTranslationException(string message) : Exception(message);

internal sealed record MangaTranslationRegion(string SourceText, string TranslatedText);

internal sealed record MangaTranslationResponse(int SchemaVersion, int Width, int Height, string OcrProvider,
    string? TranslationBackend, MangaTranslationRegion[] Regions);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MangaTranslationResponse))]
internal sealed partial class MangaTranslationJsonContext : JsonSerializerContext;

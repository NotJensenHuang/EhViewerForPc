using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Services;

internal sealed class MangaTranslationRegionLimitException : Exception;

internal static class MangaTranslationWorker
{
    public const string Invocation = "--manga-translation-worker";
    public const int ResourcesUnavailable = 3;
    public const int InvalidImage = 4;
    public const int OcrFailed = 5;
    public const int TranslationFailed = 6;
    internal const int Canceled = 7;
    internal const string SessionMode = "--session";
    internal static readonly UTF8Encoding PipeEncoding = new(false, true);

    internal static ProcessStartInfo CreateStartInfo(string snapshot, MangaTranslationBackendPaths resources, MangaTranslationLanguageOption language, bool persistent = false)
    {
        string executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "EhViewerForPc.exe");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = PipeEncoding,
            RedirectStandardInput = persistent,
        };
        // llama.cpp 的显式设备环境变量会绕过软件 Vulkan 设备的默认过滤。
        start.Environment.Remove("GGML_VK_VISIBLE_DEVICES");
        // dotnet 启动的托管版本需要显式传入应用程序集。NativeAOT 直接启动当前 exe。
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "EhViewerForPc.dll"));
        start.ArgumentList.Add(Invocation);
        if (persistent) start.StandardInputEncoding = PipeEncoding;
        start.ArgumentList.Add(persistent ? SessionMode : snapshot);
        start.ArgumentList.Add(resources.ModelDirectory);
        start.ArgumentList.Add(resources.RuntimeDirectory);
        start.ArgumentList.Add(resources.Model.Id);
        start.ArgumentList.Add(language.Id);
        return start;
    }

    public static int Run(string[] arguments)
    {
        // 旧版诊断入口只有三个参数，继续使用原默认模型与语言。
        if (arguments.Length == 3) arguments = [.. arguments, MangaTranslationModelOption.Choices[0].Id,
            MangaTranslationLanguageOption.Choices[0].Id];
        if (arguments is not [var image, var models, var native, var modelId, var languageId])
            return InvalidImage;
        bool persistent = image == SessionMode;
        if (!persistent && !IsLocalImage(image)) return InvalidImage;
        if (!Path.IsPathFullyQualified(models) || !MangaTranslationBackendService.IsLocalDirectory(models) || !Directory.Exists(models) ||
            !Path.IsPathFullyQualified(native) || !PathIdentity.Comparer.Equals(
                native, Path.Combine(AppContext.BaseDirectory, "manga-native")) || !Directory.Exists(native))
            return ResourcesUnavailable;

        if (MangaTranslationModelOption.Find(modelId) is not { } model ||
            MangaTranslationLanguageOption.Find(languageId) is not { } language) return ResourcesUnavailable;

        using var session = new InferenceSession(models, native, model, language);
        if (persistent) return RunSessionAsync(session).GetAwaiter().GetResult();
        int code = session.Infer(image, CancellationToken.None, out MangaTranslationResponse? response);
        if (response is not null)
        {
            using Stream output = Console.OpenStandardOutput();
            JsonSerializer.Serialize(output, response, MangaTranslationJsonContext.Default.MangaTranslationResponse);
            output.WriteByte((byte)'\n');
        }
        return code;
    }

    private static bool IsLocalImage(string path) =>
        Path.IsPathFullyQualified(path) && MangaTranslationBackendService.IsLocalFile(path);

    private static async Task<int> RunSessionAsync(InferenceSession session)
    {
        using var input = new StreamReader(Console.OpenStandardInput(), PipeEncoding, detectEncodingFromByteOrderMarks: false);
        using var output = new StreamWriter(Console.OpenStandardOutput(), PipeEncoding) { NewLine = "\n" };
        using var shutdown = new CancellationTokenSource();
        var jobs = Channel.CreateBounded<WorkerJob>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true });
        var gate = new object();
        WorkerJob? active = null;
        Task commands = Task.Run(ReadCommandsAsync);
        try
        {
            await foreach (WorkerJob job in jobs.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                int code;
                MangaTranslationResponse? response;
                try
                {
                    code = session.Infer(job.Image, job.Cancellation.Token, out response);
                }
                finally
                {
                    lock (gate)
                    {
                        active = null;
                        job.Cancellation.Dispose();
                    }
                }

                // 一行一页，JSON 转义了正文中的换行，父进程始终只持有一个待处理请求。
                string frame = response is null ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) :
                    "0 " + JsonSerializer.Serialize(response, MangaTranslationJsonContext.Default.MangaTranslationResponse);
                await output.WriteLineAsync(frame).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
            await commands.ConfigureAwait(false);
            return 0;
        }
        finally
        {
            shutdown.Cancel();
            await commands.ConfigureAwait(false);
        }

        async Task ReadCommandsAsync()
        {
            try
            {
                while (await input.ReadLineAsync(shutdown.Token).ConfigureAwait(false) is { } line)
                {
                    lock (gate)
                    {
                        if (line.Length == 0)
                        {
                            active?.Cancellation.Cancel();
                            continue;
                        }
                        if (active is not null || line.Length > 32767) break;
                        active = new WorkerJob(line, new CancellationTokenSource());
                        if (!jobs.Writer.TryWrite(active))
                        {
                            active.Cancellation.Dispose();
                            active = null;
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            catch (IOException) { }
            finally
            {
                lock (gate) active?.Cancellation.Cancel();
                jobs.Writer.TryComplete();
            }
        }
    }

    private sealed record WorkerJob(string Image, CancellationTokenSource Cancellation);

    private sealed class InferenceSession(string models, string native, MangaTranslationModelOption model,
        MangaTranslationLanguageOption language) : IDisposable
    {
        private readonly bool _timings = Environment.GetEnvironmentVariable("EHVIEWER_MANGA_TRANSLATION_TIMINGS") == "1";
        private MangaOcr.Engine? _ocr;
        private HyMtTranslator? _translator;

        internal int Infer(string image, CancellationToken token, out MangaTranslationResponse? response)
        {
            response = null;
            if (!IsLocalImage(image)) return InvalidImage;

            long started = Stopwatch.GetTimestamp();
            bool coldOcr = _ocr is null;
            MangaOcrResult ocr;
            try
            {
                token.ThrowIfCancellationRequested();
                _ocr ??= new MangaOcr.Engine(models, native);
                ocr = _ocr.Read(image, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _ocr?.DiscardUnverifiedSessions();
                return Canceled;
            }
            catch (MangaTranslationException ex) when (ex.RequiresSetup) { return ResourcesUnavailable; }
            catch (FileNotFoundException) { return InvalidImage; }
            catch (InvalidDataException) { return InvalidImage; }
            catch (Exception ex) { return Fail(OcrFailed, ex); }

            long ocrDone = Stopwatch.GetTimestamp();
            long translationReady = ocrDone;
            bool coldModel = _translator is null && ocr.Regions.Count > 0;
            try
            {
                token.ThrowIfCancellationRequested();
                if (ocr.Regions.Count == 0)
                {
                    response = BuildResponse(ocr, null, null, model, language);
                }
                else
                {
                    HyMtTranslator translator = _translator ??= HyMtTranslator.Load(native, models, model, token);
                    translationReady = Stopwatch.GetTimestamp();
                    response = BuildResponse(ocr, translator.Device, text => translator.Translate(text, language, token),
                        model, language);
                }

                if (_timings)
                {
                    long translationDone = Stopwatch.GetTimestamp();
                    Console.Error.WriteLine(FormattableString.Invariant(
                        $"manga-timing coldOcr={coldOcr} coldModel={coldModel} ocrMs={Stopwatch.GetElapsedTime(started, ocrDone).TotalMilliseconds:F0} modelMs={Stopwatch.GetElapsedTime(ocrDone, translationReady).TotalMilliseconds:F0} translateMs={Stopwatch.GetElapsedTime(translationReady, translationDone).TotalMilliseconds:F0}"));
                }
                return 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return Canceled; }
            catch (MangaTranslationException ex) when (ex.RequiresSetup) { return ResourcesUnavailable; }
            catch (Exception ex) { return Fail(TranslationFailed, ex); }
        }

        public void Dispose()
        {
            try { _translator?.Dispose(); }
            finally { _ocr?.Dispose(); }
        }
    }

    internal static MangaTranslationResponse BuildResponse(MangaOcrResult ocr, string? device, Func<string, string>? translate,
        MangaTranslationModelOption? model = null, MangaTranslationLanguageOption? language = null)
    {
        var regions = new MangaTranslationRegion[ocr.Regions.Count];
        for (int i = 0; i < regions.Length; i++)
        {
            MangaOcrRegion region = ocr.Regions[i];
            string original = region.Text.Trim();
            string translated = "";
            if (original.Length > 0 && translate is not null)
            {
                try { translated = translate(original).Trim(); }
                catch (MangaTranslationRegionLimitException)
                {
                    // 单个对白超出上下文时保留可复制的原文，并继续翻译其他区块。
                }
            }
            if (SameDisplayText(original, translated)) translated = "";
            regions[i] = new MangaTranslationRegion(original, translated, region.X, region.Y, region.Width, region.Height);
        }

        return new MangaTranslationResponse(1, ocr.Width, ocr.Height, ocr.Provider, device, regions)
        {
            ModelId = (model ?? MangaTranslationModelOption.Choices[0]).Id,
            TargetLanguage = (language ?? MangaTranslationLanguageOption.Choices[0]).Id,
        };
    }

    private static bool SameDisplayText(string original, string translated)
    {
        if (string.Equals(original, translated, StringComparison.Ordinal)) return true;
        return string.Equals(WithoutWhitespace(original), WithoutWhitespace(translated), StringComparison.OrdinalIgnoreCase);
    }

    private static string WithoutWhitespace(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC)
        .Where(static character => !char.IsWhiteSpace(character)));

    private static int Fail(int code, Exception error)
    {
        // 进程日志只记录异常类型，避免输出页文字或路径。
        Console.Error.WriteLine(error.GetType().Name);
        return code;
    }
}

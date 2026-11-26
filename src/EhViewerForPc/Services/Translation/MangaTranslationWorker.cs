using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Services;

internal sealed class MangaTranslationRegionLimitException(string? partialTranslation = null) : Exception
{
    internal string? PartialTranslation { get; } = partialTranslation;
}

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
        if (arguments is [MangaTranslationEvaluation.Mode, var cases, var evalModels, var evalNative, var evalModel])
        {
            if (MangaTranslationModelOption.FindForEvaluation(evalModel) is not { } evaluationModel || !HasLocalResources(evalModels, evalNative))
                return ResourcesUnavailable;
            return MangaTranslationEvaluation.Run(cases, evalModels, evalNative, evaluationModel);
        }
        if (arguments.Length == 3) return ResourcesUnavailable;
        if (arguments is not [var image, var models, var native, var modelId, var languageId])
            return InvalidImage;
        if (MangaTranslationModelOption.Find(modelId) is not { } model ||
            MangaTranslationLanguageOption.Find(languageId) is not { } language || !model.Supports(language)) return ResourcesUnavailable;

        bool persistent = image == SessionMode;
        if (!persistent && !IsLocalImage(image)) return InvalidImage;
        if (!HasLocalResources(models, native)) return ResourcesUnavailable;

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

    private static bool HasLocalResources(string models, string native) =>
        Path.IsPathFullyQualified(models) && MangaTranslationBackendService.IsLocalDirectory(models) && Directory.Exists(models) &&
        Path.IsPathFullyQualified(native) && PathIdentity.Comparer.Equals(native, MangaTranslationBackendService.NativeDirectory) && Directory.Exists(native);

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
                    code = session.Infer(job.Image, job.Cancellation.Token, out response, job.Context, job.UsePriorPage);
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
                        if (!TryParsePage(line, out string page, out MangaTranslationPageContext context, out bool usePriorPage)) break;
                        active = new WorkerJob(page, new CancellationTokenSource(), context, usePriorPage);
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

    private static bool TryParsePage(string line, out string page, out MangaTranslationPageContext context, out bool usePriorPage)
    {
        string[] request = line.Split('\t');
        page = request[0];
        context = default;
        usePriorPage = false;
        if (request.Length == 1) return true;
        if (request.Length != 4 || request[3] is not ("0" or "1") || !Guid.TryParseExact(request[1], "N", out Guid scope) || scope == Guid.Empty ||
            !int.TryParse(request[2], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int number) || number < 1) return false;
        context = new MangaTranslationPageContext(scope, number);
        usePriorPage = request[3] == "1";
        return true;
    }

    private sealed record WorkerJob(string Image, CancellationTokenSource Cancellation, MangaTranslationPageContext Context, bool UsePriorPage);

    private sealed class InferenceSession(string models, string native, MangaTranslationModelOption model,
        MangaTranslationLanguageOption language) : IDisposable
    {
        private readonly bool _timings = Environment.GetEnvironmentVariable("EHVIEWER_MANGA_TRANSLATION_TIMINGS") == "1";
        private MangaOcr.Engine? _ocr;
        private LocalGgufTranslator? _translator;
        private JMangaTranslator? _jManga;
        private MangaTranslationPageContext _previousPage;
        private string _previousDialogue = "";

        internal int Infer(string image, CancellationToken token, out MangaTranslationResponse? response, MangaTranslationPageContext context = default, bool usePriorPage = false)
        {
            response = null;
            if (!IsLocalImage(image)) return InvalidImage;

            long started = Stopwatch.GetTimestamp();
            bool coldOcr = _ocr is null;
            MangaOcrResult ocr;
            try
            {
                token.ThrowIfCancellationRequested();
                _ocr ??= new MangaOcr.Engine(models, native, useDirectMl: !model.UsesCpu);
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
            bool coldModel = _translator is null && _jManga is null && ocr.Regions.Count > 0;
            try
            {
                token.ThrowIfCancellationRequested();
                if (ocr.Regions.Count == 0)
                {
                    response = BuildResponse(ocr, null, null, model, language);
                }
                else if (model.UsesCpu)
                {
                    JMangaTranslator translator = _jManga ??= JMangaTranslator.Load(native,
                        Path.Combine(models, MangaTranslationModelOption.JMangaDirectory), token);
                    translationReady = Stopwatch.GetTimestamp();
                    response = BuildResponse(ocr, JMangaTranslator.Provider, text => translator.Translate(text, token), model, language);
                }
                else
                {
                    LocalGgufTranslator translator = _translator ??= LocalGgufTranslator.Load(native, models, model, token);
                    translationReady = Stopwatch.GetTimestamp();
                    string previous = usePriorPage && context.Reader != Guid.Empty && context.Reader == _previousPage.Reader &&
                        context.Page > 1 && _previousPage.Page == context.Page - 1 ? _previousDialogue : "";
                    response = BuildResponse(ocr, translator.Device, null, model, language,
                        (text, index, reviewed) => translator.Translate(text, GetPageContext(ocr.Regions, index, reviewed, previous), token));
                }

                token.ThrowIfCancellationRequested();
                _previousPage = context;
                _previousDialogue = context.Reader == Guid.Empty ? "" : RememberPage(response);
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
            finally
            {
                try { _jManga?.Dispose(); }
                finally { _ocr?.Dispose(); }
            }
        }
    }

    private static string GetPageContext(IReadOnlyList<MangaOcrRegion> regions, int index,
        IReadOnlyList<MangaTranslationRegion> reviewed, string previousPage)
    {
        var text = new StringBuilder(512);
        if (previousPage.Length > 0) text.Append("Previous page:\n").AppendLine(previousPage);
        int first = Math.Max(0, index - 2);
        int last = Math.Min(regions.Count - 1, index + 2);
        for (int i = first; i <= last; i++)
        {
            if (i == index) continue;
            text.Append(i < index ? "Before: " : "After: ")
                .Append(PrefixWithoutSplittingSurrogates(regions[i].Text, 160));
            if (i < index && reviewed[i] is { Review: MangaTranslationRegionReview.None, TranslatedText.Length: > 0 } before)
                text.Append(" → ").Append(PrefixWithoutSplittingSurrogates(before.TranslatedText, 80));
            text.AppendLine();
        }
        return text.ToString();
    }

    private static string RememberPage(MangaTranslationResponse response)
    {
        var text = new StringBuilder(480);
        foreach (MangaTranslationRegion region in response.Regions.TakeLast(3))
        {
            if (region.Review != MangaTranslationRegionReview.None || string.IsNullOrWhiteSpace(region.TranslatedText)) continue;
            text.Append(PrefixWithoutSplittingSurrogates(region.SourceText, 80))
                .Append(" → ")
                .Append(PrefixWithoutSplittingSurrogates(region.TranslatedText, 80)).AppendLine();
        }
        return text.ToString();
    }

    private static ReadOnlySpan<char> PrefixWithoutSplittingSurrogates(string text, int limit)
    {
        if (text.Length <= limit) return text;
        return text.AsSpan(0, char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit);
    }

    internal static MangaTranslationResponse BuildResponse(MangaOcrResult ocr, string? device, Func<string, string>? translate,
        MangaTranslationModelOption? model = null, MangaTranslationLanguageOption? language = null,
        Func<string, int, IReadOnlyList<MangaTranslationRegion>, string>? translateInContext = null)
    {
        var regions = new MangaTranslationRegion[ocr.Regions.Count];
        var translatedSources = new Dictionary<string, int>(StringComparer.Ordinal);
        var ambiguousOutputs = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < regions.Length; i++)
        {
            MangaOcrRegion region = ocr.Regions[i];
            string original = region.Text.Trim();
            string translated = "";
            MangaTranslationRegionReview review = MangaTranslationRegionReview.None;
            string? rejectedOutput = null;
            if (original.Length > 0 && (translate is not null || translateInContext is not null))
            {
                try { translated = (translateInContext?.Invoke(original, i, regions) ?? translate!(original)).Trim(); }
                catch (MangaTranslationRegionLimitException ex) { rejectedOutput = ex.PartialTranslation; }
                review = ReviewOutput(original, translated);
                if (review is MangaTranslationRegionReview.Unchanged or MangaTranslationRegionReview.SourceLanguageResidue or MangaTranslationRegionReview.SymbolOnly)
                {
                    rejectedOutput = translated;
                    translated = "";
                }
                else if (review == MangaTranslationRegionReview.None && !translatedSources.TryAdd(translated, i))
                {
                    int priorIndex = translatedSources[translated];
                    MangaTranslationRegion prior = regions[priorIndex];
                    if (prior.Review == MangaTranslationRegionReview.Duplicate || !SameDisplayText(prior.SourceText, original))
                    {
                        review = MangaTranslationRegionReview.Duplicate;
                        ambiguousOutputs.Add(translated);
                        regions[priorIndex] = prior with { Review = MangaTranslationRegionReview.Duplicate };
                    }
                }
            }
            regions[i] = new MangaTranslationRegion(original, translated, region.X, region.Y, region.Width, region.Height)
            {
                Id = i + 1,
                SourceRegionIds = region.SourceRegionIds,
                Review = review,
                RejectedOutput = rejectedOutput is { Length: > 0 } ? PrefixWithoutSplittingSurrogates(rejectedOutput, 512).ToString() : null,
            };
        }

        if (ambiguousOutputs.Count > 0)
        {
            for (int i = 0; i < regions.Length; i++)
            {
                if (ambiguousOutputs.Contains(regions[i].TranslatedText))
                    regions[i] = regions[i] with { Review = MangaTranslationRegionReview.Duplicate };
            }
        }
        return new MangaTranslationResponse(1, ocr.Width, ocr.Height, ocr.Provider, device, regions)
        {
            OcrObservations = ocr.Observations,
            RawDetections = ocr.RawDetections,
            ModelId = model?.Id ?? "",
            TargetLanguage = (language ?? MangaTranslationLanguageOption.Choices[0]).Id,
        };
    }

    internal static MangaTranslationRegionReview ReviewOutput(string original, string translated)
    {
        if (string.IsNullOrWhiteSpace(translated)) return MangaTranslationRegionReview.EmptyOrOverLimit;
        if (SameDisplayText(original, translated)) return MangaTranslationRegionReview.Unchanged;
        if (ContainsLettersOrDigits(original) && !ContainsLettersOrDigits(translated))
            return MangaTranslationRegionReview.SymbolOnly;
        bool kana = false;
        foreach (char character in translated)
        {
            if (character is >= '\u3400' and <= '\u9fff') return MangaTranslationRegionReview.None;
            kana |= character is >= '\u3041' and <= '\u30fa';
        }
        return kana ? MangaTranslationRegionReview.SourceLanguageResidue : MangaTranslationRegionReview.None;
    }

    private static bool ContainsLettersOrDigits(string text)
    {
        foreach (Rune character in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(character)) return true;
        }
        return false;
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

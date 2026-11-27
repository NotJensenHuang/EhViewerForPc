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

    internal static bool TryParsePage(string line, out string page, out MangaTranslationPageContext context, out bool usePriorPage)
    {
        string[] request = line.Split('\t');
        page = request[0];
        context = default;
        usePriorPage = false;
        if (request.Length == 1) return true;
        if (request.Length is not (4 or 5) || request[3] is not ("0" or "1") ||
            !Guid.TryParseExact(request[1], "N", out Guid scope) || scope == Guid.Empty ||
            !int.TryParse(request[2], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int number) || number < 1) return false;
        string? scene = null;
        if (request.Length == 5)
        {
            if (request[4].Length > 960) return false;
            Span<byte> sceneBytes = stackalloc byte[720];
            if (!Convert.TryFromBase64String(request[4], sceneBytes, out int length)) return false;
            try { scene = PipeEncoding.GetString(sceneBytes[..length]); }
            catch (DecoderFallbackException) { return false; }
            if (scene.Length > MangaTranslationPageContext.MaximumSceneLength || scene.Any(char.IsControl)) return false;
            scene = scene.Trim();
        }
        context = new MangaTranslationPageContext(scope, number, scene);
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
        private DialoguePage _latest;
        private DialoguePage _older;

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
                    string previous = PreviousDialogue(context, usePriorPage);
                    MangaTranslationContextSources[]? regionContext = model.SupportsSceneContext
                        ? new MangaTranslationContextSources[ocr.Regions.Count] : null;
                    response = BuildResponse(ocr, translator.Device, null, model, language, (text, index, reviewed) =>
                    {
                        (string reference, MangaTranslationContextSources sources) = GetPageContext(ocr.Regions, index,
                            reviewed, previous, model.SupportsSceneContext, context.Scene);
                        try
                        {
                            string output = translator.Translate(text, reference, token);
                            if (regionContext is not null) regionContext[index] = sources;
                            return output;
                        }
                        catch (MangaTranslationRegionLimitException ex) when (regionContext is not null && ex.PartialTranslation is not null)
                        {
                            // 已生成的待复核或截断结果同样使用了语境，只有送入前超长才不记。
                            regionContext[index] = sources;
                            throw;
                        }
                    });
                    if (regionContext is not null)
                    {
                        MangaTranslationContextSources pageContext = MangaTranslationContextSources.None;
                        for (int i = 0; i < response.Regions.Length; i++)
                        {
                            if (regionContext[i] == MangaTranslationContextSources.None) continue;
                            response.Regions[i] = response.Regions[i] with { ContextSources = regionContext[i] };
                            pageContext |= regionContext[i];
                        }
                        response = response with { ContextSources = pageContext };
                    }
                }

                token.ThrowIfCancellationRequested();
                RememberPage(context, response, usePriorPage);
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

        private string PreviousDialogue(MangaTranslationPageContext context, bool usePriorPage)
        {
            if (!usePriorPage || context.Reader == Guid.Empty || context.Page <= 1) return "";
            int previousNumber = context.Page - 1;
            if (_latest.Reader == context.Reader && _latest.Number == previousNumber) return _latest.Text;
            if (_older.Reader == context.Reader && _older.Number == previousNumber) return _older.Text;
            return "";
        }

        private void RememberPage(MangaTranslationPageContext context, MangaTranslationResponse response, bool usePriorPage)
        {
            if (context.Reader == Guid.Empty)
            {
                _latest = _older = default;
                return;
            }
            // 同页重试替换当前页，不挤掉之前的相邻页。
            string text = PreviousDialogue(context, usePriorPage);
            _older = text.Length == 0 ? default : new(context.Reader, context.Page - 1, text);
            _latest = new(context.Reader, context.Page, MangaTranslationWorker.RememberPage(response, model.SupportsSceneContext));
        }

        private readonly record struct DialoguePage(Guid Reader, int Number, string Text);

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

    internal static (string, MangaTranslationContextSources) GetPageContext(IReadOnlyList<MangaOcrRegion> regions, int index,
        IReadOnlyList<MangaTranslationRegion> reviewed, string previousPage, bool sourceOnly, string? scene)
    {
        var text = new StringBuilder(512);
        MangaTranslationContextSources sources = MangaTranslationContextSources.None;
        if (sourceOnly && !string.IsNullOrWhiteSpace(scene))
        {
            text.Append("读者补充的场景事实：").AppendLine(scene);
            sources |= MangaTranslationContextSources.Scene;
        }
        if (previousPage.Length > 0)
        {
            text.Append(sourceOnly ? "前一页对白：\n" : "Previous page:\n").AppendLine(previousPage);
            sources |= MangaTranslationContextSources.PreviousPage;
        }
        int first = Math.Max(0, index - 2);
        int last = Math.Min(regions.Count - 1, index + 2);
        for (int i = first; i <= last; i++)
        {
            if (i == index || (sourceOnly && !HasReferenceText(regions[i].Text))) continue;
            sources |= MangaTranslationContextSources.NearbyDialogue;
            text.Append(sourceOnly ? (i < index ? "本页前方对白：" : "本页后方对白：") : (i < index ? "Before: " : "After: "))
                .Append(PrefixWithoutSplittingSurrogates(regions[i].Text, 160));
            if (!sourceOnly && i < index && reviewed[i] is { Review: MangaTranslationRegionReview.None, TranslatedText.Length: > 0 } before)
                text.Append(" → ").Append(PrefixWithoutSplittingSurrogates(before.TranslatedText, 80));
            text.AppendLine();
        }
        return (text.ToString(), sources);
    }

    private static string RememberPage(MangaTranslationResponse response, bool sourceOnly)
    {
        var text = new StringBuilder(480);
        IEnumerable<MangaTranslationRegion> regions = response.Regions;
        if (sourceOnly) regions = regions.Where(static region => HasReferenceText(region.SourceText));
        foreach (MangaTranslationRegion region in regions.TakeLast(3))
        {
            if (sourceOnly)
            {
                text.Append(PrefixWithoutSplittingSurrogates(region.SourceText, 80)).AppendLine();
                continue;
            }
            if (region.Review != MangaTranslationRegionReview.None || string.IsNullOrWhiteSpace(region.TranslatedText)) continue;
            text.Append(PrefixWithoutSplittingSurrogates(region.SourceText, 80))
                .Append(" → ")
                .Append(PrefixWithoutSplittingSurrogates(region.TranslatedText, 80)).AppendLine();
        }
        return text.ToString();
    }

    private static bool HasReferenceText(string text)
    {
        int letters = 0;
        foreach (Rune character in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(character) && ++letters >= 2) return true;
        }
        return false;
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
                if (review == MangaTranslationRegionReview.None && model?.UsesCpu == true &&
                    translate is not null && translateInContext is null &&
                    TryFindCollapsedDialogue(ocr, region, original, translated, out string first, out string second))
                {
                    // 同一气泡中两句明确停顿的短对白，有时会被日中模型压成相同的两句。
                    // 分句原文来自 OCR 顺序，仍调用同一模型，保留原稿供复核。
                    string? revised = RetryDialogue(first, second, translate);
                    if (revised is null) review = MangaTranslationRegionReview.Duplicate;
                    else
                    {
                        rejectedOutput = translated;
                        translated = revised;
                    }
                }
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
                BubbleInterior = region.BubbleInterior,
                BalancedBubbleInterior = region.BalancedBubbleInterior,
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

    private const string DialoguePauses = "・･…‥.";

    private static bool TryFindCollapsedDialogue(MangaOcrResult ocr, MangaOcrRegion region, string original,
        string translated, out string first, out string second)
    {
        first = second = "";
        if (region.BubbleInterior is null || region.SourceRegionIds is not [var firstId, var secondId] ||
            firstId == secondId) return false;

        ReadOnlySpan<char> target = translated.AsSpan();
        int breakAt = target.IndexOfAny(DialoguePauses);
        if (breakAt <= 0) return false;
        ReadOnlySpan<char> targetFirst = target[..breakAt].Trim();
        ReadOnlySpan<char> targetSecond = target[breakAt..].Trim("・･…‥. \t\r\n");
        if (targetFirst.IsEmpty || !targetFirst.SequenceEqual(targetSecond)) return false;

        foreach (MangaOcrObservation observation in ocr.Observations)
        {
            if (observation.RequiresReview || observation.Direction != MangaOcrDirection.Vertical) continue;
            if (observation.Id == firstId) first = observation.FinalText.Trim();
            else if (observation.Id == secondId) second = observation.FinalText.Trim();
        }

        ReadOnlySpan<char> firstStem = first.AsSpan().TrimEnd(DialoguePauses);
        ReadOnlySpan<char> secondStem = second.AsSpan().TrimEnd(DialoguePauses);
        return firstStem.Length is > 0 and <= 8 && secondStem.Length is > 0 and <= 8 &&
            firstStem.Length != first.Length && secondStem.Length != second.Length &&
            !SameDisplayText(firstStem.ToString(), secondStem.ToString()) &&
            SameDisplayText(original, first + second);
    }

    private static string? RetryDialogue(string first, string second, Func<string, string> translate)
    {
        try
        {
            string translatedFirst = translate(first).Trim();
            if (ReviewOutput(first, translatedFirst) != MangaTranslationRegionReview.None) return null;
            string translatedSecond = translate(second).Trim();
            if (ReviewOutput(second, translatedSecond) != MangaTranslationRegionReview.None) return null;

            string firstStem = translatedFirst.AsSpan().TrimEnd(DialoguePauses).ToString();
            string secondStem = translatedSecond.AsSpan().TrimEnd(DialoguePauses).ToString();
            return SameDisplayText(firstStem, secondStem) ? null : string.Concat(translatedFirst, "\n", translatedSecond);
        }
        catch (MangaTranslationRegionLimitException)
        {
            return null;
        }
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

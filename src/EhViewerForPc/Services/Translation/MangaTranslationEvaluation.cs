using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EhViewerForPc.Services;

internal static class MangaTranslationEvaluation
{
    internal const string Mode = "--evaluate";

    internal static int Run(string cases, string models, string native, MangaTranslationModelOption model)
    {
        if (!Path.IsPathFullyQualified(cases) || !MangaTranslationBackendService.IsLocalFile(cases))
            return MangaTranslationWorker.InvalidImage;

        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, args) =>
        {
            args.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            if (!MangaTranslationAssetInstaller.VerifyTranslationWeightAsync(models, model, stop.Token).GetAwaiter().GetResult())
                return MangaTranslationWorker.ResourcesUnavailable;
            using var translator = LocalGgufTranslator.Load(native, models, model, stop.Token);
            using var reader = new StreamReader(cases, MangaTranslationWorker.PipeEncoding);
            if (reader.ReadLine() is not { } header ||
                !header.StartsWith("case_id\tsource_lang\ttarget_lang\tcontext_mode\tcontext\tsource\treference", StringComparison.Ordinal))
                return MangaTranslationWorker.InvalidImage;

            using var output = new StreamWriter(Console.OpenStandardOutput(), MangaTranslationWorker.PipeEncoding);
            for (int i = 0; i < 1000 && reader.ReadLine() is { } line; i++)
            {
                stop.Token.ThrowIfCancellationRequested();
                if (line.Length > 8192) return MangaTranslationWorker.InvalidImage;
                string[] fields = line.Split('\t');
                if (fields.Length < 7 || fields[0].Length == 0) return MangaTranslationWorker.InvalidImage;
                if (fields[1] != "ja" || fields[2] != "zh-CN") continue;
                long start = Stopwatch.GetTimestamp();
                string translated = "";
                string status = "review";
                try
                {
                    translated = translator.Translate(fields[5], fields[3] == "single" ? "" : fields[4], stop.Token);
                    if (MangaTranslationWorker.ReviewOutput(fields[5], translated) == MangaTranslationRegionReview.None)
                        status = "ok";
                }
                catch (MangaTranslationRegionLimitException ex) { translated = ex.PartialTranslation ?? ""; }
                var result = new MangaTranslationEvaluationRow(fields[0], model.Id, fields[3], translated,
                    Stopwatch.GetElapsedTime(start).TotalMilliseconds, status);
                output.WriteLine(JsonSerializer.Serialize(result, MangaTranslationEvaluationJsonContext.Default.MangaTranslationEvaluationRow));
            }
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return MangaTranslationWorker.Canceled; }
        catch (MangaTranslationException ex) when (ex.RequiresSetup) { return MangaTranslationWorker.ResourcesUnavailable; }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().Name);
            return MangaTranslationWorker.TranslationFailed;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}

internal sealed record MangaTranslationEvaluationRow(string CaseId, string ModelId, string ContextMode, string Output,
    double ElapsedMs, string Status);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(MangaTranslationEvaluationRow))]
internal sealed partial class MangaTranslationEvaluationJsonContext : JsonSerializerContext;

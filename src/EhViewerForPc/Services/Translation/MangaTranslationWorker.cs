using System.Diagnostics;
using System.Text;
using System.Text.Json;
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

    internal static ProcessStartInfo CreateStartInfo(string snapshot, MangaTranslationBackendPaths resources, MangaTranslationLanguageOption language)
    {
        string executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "EhViewerForPc.exe");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(snapshot)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
        };
        // llama.cpp 的显式设备环境变量会绕过软件 Vulkan 设备的默认过滤。
        start.Environment.Remove("GGML_VK_VISIBLE_DEVICES");
        // dotnet 启动的托管版本需要显式传入应用程序集。NativeAOT 直接启动当前 exe。
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "EhViewerForPc.dll"));
        start.ArgumentList.Add(Invocation);
        start.ArgumentList.Add(snapshot);
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
        if (arguments is not [var image, var models, var native, var modelId, var languageId] ||
            !Path.IsPathFullyQualified(image) || !MangaTranslationBackendService.IsLocalFile(image)) return InvalidImage;
        if (!Path.IsPathFullyQualified(models) || !MangaTranslationBackendService.IsLocalDirectory(models) || !Directory.Exists(models) ||
            !Path.IsPathFullyQualified(native) || !PathIdentity.Comparer.Equals(
                native, Path.Combine(AppContext.BaseDirectory, "manga-native")) || !Directory.Exists(native))
            return ResourcesUnavailable;

        if (MangaTranslationModelOption.Find(modelId) is not { } model ||
            MangaTranslationLanguageOption.Find(languageId) is not { } language) return ResourcesUnavailable;

        MangaOcrResult ocr;
        try
        {
            // 父进程拥有取消信号和工作进程生命周期。此分支不初始化 Avalonia 或用户数据。
            ocr = MangaOcr.Read(image, models, native, CancellationToken.None);
        }
        catch (MangaTranslationException ex) when (ex.RequiresSetup) { return ResourcesUnavailable; }
        catch (FileNotFoundException) { return InvalidImage; }
        catch (InvalidDataException) { return InvalidImage; }
        catch (Exception ex) { return Fail(OcrFailed, ex); }

        try
        {
            MangaTranslationResponse response;
            if (ocr.Regions.Count == 0)
            {
                response = BuildResponse(ocr, null, null, model, language);
            }
            else
            {
                using var translator = HyMtTranslator.Load(native, models, model, CancellationToken.None);
                response = BuildResponse(ocr, translator.Device, text => translator.Translate(text, language, CancellationToken.None),
                    model, language);
            }

            using Stream output = Console.OpenStandardOutput();
            JsonSerializer.Serialize(output, response, MangaTranslationJsonContext.Default.MangaTranslationResponse);
            output.WriteByte((byte)'\n');
            return 0;
        }
        catch (MangaTranslationException ex) when (ex.RequiresSetup) { return ResourcesUnavailable; }
        catch (Exception ex) { return Fail(TranslationFailed, ex); }
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

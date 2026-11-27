namespace EhViewerForPc.Services;

public sealed record MangaTranslationModelOption(string Id, string Name, string FileName, long Bytes)
{
    internal const string QwenContextId = "qwen3-4b-context";
    internal const string JMangaId = "jmanga-fast-v1";
    internal const string JMangaDirectory = "jmanga-v1";
    internal bool UsesCpu => Id == JMangaId;
    internal bool SupportsSceneContext => Id == QwenContextId;

    public static IReadOnlyList<MangaTranslationModelOption> Choices { get; } = Array.AsReadOnly<MangaTranslationModelOption>(
    [
        new(JMangaId, "JMangaTranslator-Fast v1 · CPU · 约 1.55 GB", JMangaDirectory + "/onnx/encoder_fp32.onnx", 1_294_638_243),
        new(QwenContextId, "Qwen3 4B 语境翻译（试用）· 约 2.50 GB", "Qwen3-4B-Q4_K_M.gguf", 2_497_280_256),
        new("hy-mt2-manga-zh-hans", "Hy-MT2 漫画日中微调（试用）· 约 1.13 GB", "manga-zh-Hans-v1-Q4_K_M.gguf", 1_133_080_416),
        new("qwen2.5-7b-ja2zh", "Qwen2.5 7B 日中字幕微调（试用）· 约 4.68 GB", "qwen2.5-7b-ja2zh-q4_k_m.gguf", 4_683_073_440),
    ]);

    // Hugging Face requires user approval. These models consume only manually downloaded weights in evaluation mode.
    internal static IReadOnlyList<MangaTranslationModelOption> EvaluationOnly { get; } = Array.AsReadOnly<MangaTranslationModelOption>(
    [
        new("qwen3-4b-ja2zh-eval", "Qwen3 4B 日中字幕微调", "qwen3-4b-ja2zh-q4_k_m.gguf", 2_497_278_784),
        new("qwen3-8b-ja2zh-eval", "Qwen3 8B 日中字幕微调", "qwen3-8b-ja2zh-q4_k_m.gguf", 5_027_783_392),
    ]);

    internal static MangaTranslationModelOption? FindForEvaluation(string? id) =>
        Find(id) ?? EvaluationOnly.FirstOrDefault(model => model.Id == id);

    internal bool Supports(MangaTranslationLanguageOption language) => language.Id == "zh-CN";

    internal static MangaTranslationModelOption? Find(string? id) => Choices.FirstOrDefault(model => model.Id == id);
}

public sealed record MangaTranslationLanguageOption(string Id, string Name, string EnglishName)
{
    public static IReadOnlyList<MangaTranslationLanguageOption> Choices { get; } = Array.AsReadOnly<MangaTranslationLanguageOption>(
    [
        new("zh-CN", "简体中文", "Simplified Chinese"),
        new("zh-Hant", "繁体中文", "Traditional Chinese"),
        new("en", "英语", "English"),
        new("ko", "韩语", "Korean"),
        new("fr", "法语", "French"),
        new("de", "德语", "German"),
        new("es", "西班牙语", "Spanish"),
        new("pt", "葡萄牙语", "Portuguese"),
        new("it", "意大利语", "Italian"),
        new("ru", "俄语", "Russian"),
        new("th", "泰语", "Thai"),
        new("vi", "越南语", "Vietnamese"),
        new("id", "印度尼西亚语", "Indonesian"),
        new("tr", "土耳其语", "Turkish"),
        new("ar", "阿拉伯语", "Arabic"),
        new("nl", "荷兰语", "Dutch"),
        new("pl", "波兰语", "Polish"),
        new("uk", "乌克兰语", "Ukrainian"),
    ]);

    internal static MangaTranslationLanguageOption? Find(string? id) => Choices.FirstOrDefault(language => language.Id == id);
    internal static MangaTranslationLanguageOption Resolve(string? id) => Find(id) ?? Choices[0];

    // Hy-MT2 文档提供中英两种提示模板，目标语名称使用与模板一致的完整名称。
    internal string CreatePrompt(string text) => Id is "zh-CN" or "zh-Hant"
        ? $"将以下文本翻译为{Name}，注意只需要输出翻译后的结果，不要额外解释：\n{text}"
        : $"Translate the following text into {EnglishName}. Note that you should only output the translated result without any additional explanation:\n{text}";
}

internal sealed record MangaTranslationPreferences(string? ModelDirectory, MangaTranslationModelOption? Model, MangaTranslationLanguageOption Language);

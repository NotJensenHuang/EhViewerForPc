namespace EhViewerForPc.Services;

public sealed record MangaTranslationModelOption(string Id, string Name, string FileName, long Bytes)
{
    public static IReadOnlyList<MangaTranslationModelOption> Choices { get; } = Array.AsReadOnly<MangaTranslationModelOption>(
    [
        new("hy-mt2-1.8b", "Hy-MT2 1.8B Q4_K_M · 约 1.13 GB", "Hy-MT2-1.8B-Q4_K_M.gguf", 1_133_080_448),
        new("hy-mt2-7b", "Hy-MT2 7B Q4_K_M · 约 4.62 GB", "Hy-MT2-7B-Q4_K_M.gguf", 4_624_648_896),
    ]);

    internal static MangaTranslationModelOption? Find(string? id) => Choices.FirstOrDefault(model => model.Id == id);
    internal static MangaTranslationModelOption Resolve(string? id) => Find(id) ?? Choices[0];
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

internal sealed record MangaTranslationPreferences(string? ModelDirectory, MangaTranslationModelOption Model, MangaTranslationLanguageOption Language);

namespace EhViewerReader.Core.Models;

public static class GalleryTags
{
    public static readonly string[] KnownLanguages =
    [
        "Chinese", "Traditional Chinese", "English", "Japanese", "Korean",
        "Spanish", "French", "German", "Russian", "Portuguese", "Italian",
        "Vietnamese", "Thai", "Indonesian",
    ];

    public static readonly string[] KnownTags =
    [
        "Digital", "AI Generated", "Decensored", "Uncensored", "Sample",
        "Ongoing", "Incomplete", "Full Color", "Colorized", "MTL",
    ];

    public static string GetLanguageDisplayName(string language) => language switch
    {
        "Chinese" => "中文",
        "Traditional Chinese" => "繁中",
        "English" => "英文",
        "Japanese" => "日文",
        "Korean" => "韩文",
        "Spanish" => "西语",
        "French" => "法语",
        "German" => "德语",
        "Russian" => "俄语",
        "Portuguese" => "葡语",
        "Italian" => "意语",
        "Vietnamese" => "越语",
        "Thai" => "泰语",
        "Indonesian" => "印尼语",
        _ => language,
    };

    public static (string[] Languages, string[] Tags) Parse(string title)
    {
        var languages = new List<string>();
        var tags = new List<string>();

        foreach (var language in KnownLanguages)
        {
            if (title.Contains($"[{language}]", StringComparison.OrdinalIgnoreCase))
            {
                languages.Add(language);
            }
        }

        foreach (var tag in KnownTags)
        {
            if (title.Contains($"[{tag}]", StringComparison.OrdinalIgnoreCase))
            {
                tags.Add(tag);
            }
        }

        // Chinese translations sometimes carry a short [中文] marker instead of [Chinese].
        if (languages.Count == 0 && (title.Contains("[中文]", StringComparison.Ordinal) ||
                                     title.Contains("汉化", StringComparison.Ordinal)))
        {
            languages.Add("Chinese");
        }

        return (languages.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}

using System.Globalization;
using EhViewerForPc.Core.Models;

namespace EhViewerForPc.Core.Format;

public static partial class PageFileParser
{
    // EhViewer's GalleryProvider2.SUPPORT_IMAGE_EXTENSIONS order.
    private static readonly Dictionary<string, int> ExtensionPriority = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = 0,
        [".jpeg"] = 1,
        [".png"] = 2,
        [".gif"] = 3,
        [".webp"] = 4,
    };

    /// <summary>
    /// 枚举支持的图片一次；Scan 决定使用原始页号或普通名称的自然顺序。
    /// 同时携带大小与 mtime，扫描器可直接构造缓存签名。
    /// </summary>
    public static IReadOnlyList<PageCandidate> Enumerate(string folderPath, out string? error)
    {
        error = null;
        var files = new List<PageCandidate>();
        try
        {
            var directory = new DirectoryInfo(folderPath);
            foreach (FileInfo file in directory.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                string extension = file.Extension.ToLowerInvariant();
                if (!IsSupportedExtension(extension)) continue;

                files.Add(new PageCandidate
                {
                    FilePath = file.FullName,
                    FileName = file.Name,
                    Extension = extension,
                    Length = file.Length,
                    MtimeTicks = file.LastWriteTimeUtc.Ticks,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException or DirectoryNotFoundException)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return [];
        }

        return files;
    }

    public static PageScanResult Scan(string folderPath, SpiderHeader? header, out string? error)
    {
        var files = Enumerate(folderPath, out error);
        header ??= SpiderInfoParser.TryParseFile(Path.Combine(folderPath, ".ehviewer"), out _);
        return Scan(files, header, out _);
    }

    public static PageScanResult Scan(IReadOnlyList<PageCandidate> files, SpiderHeader? header, out string? error)
    {
        error = null;
        bool numbered = files.Any(file => TryParsePageName(file.FileName, out _, out _));
        // 编号页本身就是原始页号的依据；缺少元数据或额外封面不能压缩缺页位置。
        if (files.Count > 0 && !numbered)
            return ScanNaturalPages(files);
        var grouped = new Dictionary<int, List<PageCandidate>>();
        int zeroByteFiles = 0;

        foreach (PageCandidate file in files)
        {
            if (!TryParsePageName(file.FileName, out int number, out string extension)) continue;
            if (number <= 0) continue;

            if (!grouped.TryGetValue(number, out var list))
            {
                list = [];
                grouped[number] = list;
            }

            if (file.Length == 0) zeroByteFiles++;
            list.Add(new PageCandidate
            {
                FilePath = file.FilePath,
                FileName = file.FileName,
                Extension = extension,
                Length = file.Length,
                MtimeTicks = file.MtimeTicks,
            });
        }

        var pages = new List<GalleryPage>(grouped.Count);
        int displayIndex = 0;
        foreach (var pair in grouped.OrderBy(p => p.Key))
        {
            var candidates = pair.Value
                .OrderByDescending(c => c.Length > 0)
                .ThenBy(c => ExtensionPriority[c.Extension])
                .ThenBy(c => c.FileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            bool allEmpty = candidates.All(c => c.Length == 0);
            pages.Add(new GalleryPage
            {
                Number = pair.Key,
                DisplayIndex = displayIndex++,
                Candidates = candidates,
                State = allEmpty ? PageState.Empty : PageState.Ok,
            });
        }

        int maxPage = pages.Count == 0 ? 0 : pages[^1].Number;
        int minPage = pages.Count == 0 ? 0 : pages[0].Number;
        bool hasExpected = header is { HasPages: true, Pages: > 0 };
        int expected = hasExpected ? header!.Pages : 0;
        bool headerOutdated = hasExpected && maxPage > expected;

        // 同页有可读候选时，遗留空文件只作诊断；只有整页为空才需要补页。
        var present = pages.Where(p => p.State != PageState.Empty).Select(p => p.Number).ToHashSet();
        int missingUpperBound = Math.Max(expected, maxPage);
        var missing = new List<PageRange>();
        int rangeStart = 0;
        for (int i = 1; i <= missingUpperBound; i++)
        {
            if (!present.Contains(i))
            {
                if (rangeStart == 0) rangeStart = i;
            }
            else if (rangeStart != 0)
            {
                missing.Add(new PageRange(rangeStart, i - 1));
                rangeStart = 0;
            }
        }

        if (rangeStart != 0)
        {
            missing.Add(new PageRange(rangeStart, missingUpperBound));
        }

        var extensionCounts = files
            .GroupBy(c => c.Extension, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key.TrimStart('.').ToLowerInvariant()}: {g.Count()}");

        return new PageScanResult
        {
            Pages = pages,
            MissingRanges = missing,
            ExpectedPages = expected,
            HasExpectedPages = hasExpected,
            MaxPageNumber = maxPage,
            MinPageNumber = minPage,
            ExtensionSummary = string.Join(", ", extensionCounts),
            ZeroByteFiles = zeroByteFiles,
            HeaderPageCountOutdated = headerOutdated,
        };
    }

    public static bool TryParsePageName(string fileName, out int number, out string extension)
    {
        number = 0;
        extension = string.Empty;
        if (fileName.Length < 10) return false; // 8 digits + dot + at least 1 char
        if (fileName[8] != '.') return false;

        for (int i = 0; i < 8; i++)
        {
            char ch = fileName[i];
            if (ch is < '0' or > '9') return false;
        }

        if (!int.TryParse(fileName.AsSpan(0, 8), NumberStyles.None, CultureInfo.InvariantCulture, out number) ||
            number <= 0)
        {
            return false;
        }

        extension = fileName[8..].ToLowerInvariant();
        return ExtensionPriority.ContainsKey(extension);
    }

    public static bool IsSupportedExtension(string extension) => ExtensionPriority.ContainsKey(extension);

    public static int GetExtensionPriority(string extension) =>
        ExtensionPriority.TryGetValue(extension, out int priority) ? priority : int.MaxValue;

    public static string? GetImageKindFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "jpeg",
        ".png" => "png",
        ".gif" => "gif",
        ".webp" => "webp",
        _ => null,
    };
}

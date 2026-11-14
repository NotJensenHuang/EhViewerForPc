using EhViewerForPc.Core.Models;

namespace EhViewerForPc.Core.Format;

public static partial class PageFileParser
{
    private static PageScanResult ScanNaturalPages(IReadOnlyList<PageCandidate> files)
    {
        var pages = new List<GalleryPage>();
        var missing = new List<PageRange>();
        foreach (var group in files.Where(file => IsSupportedExtension(file.Extension))
            .GroupBy(file => Path.GetFileNameWithoutExtension(file.FileName), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, NaturalNameComparer.Instance))
        {
            var candidates = group.OrderByDescending(c => c.Length > 0).ThenBy(c => GetExtensionPriority(c.Extension))
                .ThenBy(c => c.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
            bool empty = candidates.All(c => c.Length == 0);
            int number = pages.Count + 1;
            pages.Add(new() { Number = number, DisplayIndex = number - 1, Candidates = candidates, State = empty ? PageState.Empty : PageState.Ok });
            if (empty) missing.Add(new(number, number));
        }
        return new()
        {
            HasNaturalPageNames = true, Pages = pages, MaxPageNumber = pages.Count, MinPageNumber = pages.Count > 0 ? 1 : 0,
            MissingRanges = missing, ZeroByteFiles = files.Count(f => f.Length == 0),
            ExtensionSummary = string.Join(", ", files.GroupBy(f => f.Extension, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => $"{g.Key.TrimStart('.').ToLowerInvariant()}: {g.Count()}")),
        };
    }

    private sealed class NaturalNameComparer : IComparer<string>
    {
        internal static readonly NaturalNameComparer Instance = new();
        public int Compare(string? first, string? second)
        {
            if (first is null || second is null) return StringComparer.OrdinalIgnoreCase.Compare(first, second);
            int a = 0, b = 0;
            while (a < first.Length && b < second.Length)
            {
                if (char.IsAsciiDigit(first[a]) && char.IsAsciiDigit(second[b]))
                {
                    int aStart = a, bStart = b;
                    while (a < first.Length && char.IsAsciiDigit(first[a])) a++;
                    while (b < second.Length && char.IsAsciiDigit(second[b])) b++;
                    int aSignificant = aStart, bSignificant = bStart;
                    while (aSignificant < a && first[aSignificant] == '0') aSignificant++;
                    while (bSignificant < b && second[bSignificant] == '0') bSignificant++;
                    int length = (a - aSignificant).CompareTo(b - bSignificant);
                    if (length != 0) return length;
                    int value = first.AsSpan(aSignificant, a - aSignificant).SequenceCompareTo(second.AsSpan(bSignificant, b - bSignificant));
                    if (value != 0) return value;
                    int padding = (a - aStart).CompareTo(b - bStart);
                    if (padding != 0) return padding;
                }
                else
                {
                    int value = char.ToUpperInvariant(first[a]).CompareTo(char.ToUpperInvariant(second[b]));
                    if (value != 0) return value;
                    a++; b++;
                }
            }
            int remaining = (first.Length - a).CompareTo(second.Length - b);
            return remaining != 0 ? remaining : StringComparer.Ordinal.Compare(first, second);
        }
    }
}

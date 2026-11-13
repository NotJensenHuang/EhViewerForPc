using EhViewerForPc.Core.Models;

namespace EhViewerForPc.Core.Online;

/// <summary>
/// Gallery list filtering, mirroring EhViewer's include/exclude rules.
///
/// Exclude rules hide a gallery as soon as one matches. When at least one
/// include rule is enabled, a gallery must match one of them; this lets a user
/// keep a list focused on a tag or uploader without blocking everything else.
/// </summary>
public static class EhFilter
{
    public static bool IsVisible(EhGallerySummary item, IReadOnlyList<FilterRule>? rules)
    {
        if (rules is null || rules.Count == 0) return true;

        bool hasInclude = false;
        bool includeHit = false;

        foreach (FilterRule rule in rules)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Text)) continue;
            if (rule.Include) hasInclude = true;

            if (!Matches(item, rule)) continue;
            if (!rule.Include) return false;
            includeHit = true;
        }

        return !hasInclude || includeHit;
    }

    private static bool Matches(EhGallerySummary item, FilterRule rule)
    {
        string needle = rule.Text.Trim();
        return rule.Target switch
        {
            FilterTarget.Title => item.Title.Contains(needle, StringComparison.OrdinalIgnoreCase),
            FilterTarget.Uploader => item.Uploader.Contains(needle, StringComparison.OrdinalIgnoreCase),
            _ => MatchesTag(item, needle),
        };
    }

    private static bool MatchesTag(EhGallerySummary item, string needle)
    {
        foreach (string tag in item.Tags)
        {
            if (tag.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        }

        // Compact layouts occasionally omit the tag chips; falling back to the
        // title keeps "tag" filters useful instead of silently hiding nothing.
        return item.Tags.Count == 0 && item.Title.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}

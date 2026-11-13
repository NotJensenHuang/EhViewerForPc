namespace EhViewerReader.Core.Online;

/// <summary>Represents one linked entry in an upstream leaderboard overview.</summary>
public sealed record EhRankingEntry(string Label, string Href);
/// <summary>Represents one time period in a leaderboard overview.</summary>
public sealed record EhRankingPeriod(string Label, int GalleryListIndex, IReadOnlyList<EhRankingEntry> Items, bool HasGalleryList = false);
/// <summary>Represents a leaderboard kind, such as galleries, uploaders or tagging.</summary>
public sealed record EhRankingKind(string Label, IReadOnlyList<EhRankingPeriod> Periods);

/// <summary>Parses the seven upstream ranking kinds and their four time periods.</summary>
public static class EhRankingParser
{
    /// <summary>Reads the same overview sections used by Android TopListParser.</summary>
    public static IReadOnlyList<EhRankingKind> Parse(string html)
    {
        var document = HtmlLite.Parse(html);
        var content = document.FirstDescendantByClass("ido");
        if (content is null) return [];
        string[] names = ["画廊", "上传者", "标签贡献", "Hentai@Home", "EHTracker", "清理贡献", "评分与评论"];
        string[] periods = ["全部", "今年", "本月", "昨日"];
        int[] indices = [11, 12, 13, 15];
        var sections = content.Children.Where(c => c.IsElement).ToArray();
        var result = new List<EhRankingKind>();
        for (int kind = 0; kind < names.Length; kind++)
        {
            int sectionIndex = 1 + kind * 2;
            if (sectionIndex >= sections.Length) break;
            var cards = sections[sectionIndex].Children.Where(c => c.IsElement).Skip(1).Take(4).ToArray();
            var lists = new List<EhRankingPeriod>();
            for (int period = 0; period < cards.Length; period++)
            {
                var entries = cards[period].DescendantsByClass("tun").Select(n => n.FirstDescendant("a"))
                    .Where(a => a is not null).Select((a, rank) => new EhRankingEntry($"{rank + 1}. {a!.InnerText}", EhUrls.Absolute(EhSite.EHentai, a.GetAttribute("href"))))
                    .Take(10).ToArray();
                lists.Add(new(periods[period], indices[period], entries, kind == 0));
            }
            if (lists.Any(p => p.Items.Count > 0)) result.Add(new(names[kind], lists.AsEnumerable().Reverse().ToArray()));
        }
        return result;
    }
}

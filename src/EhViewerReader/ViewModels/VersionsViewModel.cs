using EhViewerReader.Core.Models;
using EhViewerReader.Services;

namespace EhViewerReader.ViewModels;

/// <summary>
/// Version management for works that exist in more than one source. The list is
/// ordered by the same rule the library uses to pick the default copy, so the
/// first row is always what "阅读" would open without an explicit choice.
/// </summary>
public sealed class VersionsViewModel
{
    public VersionsViewModel(GalleryItemViewModel item, AppServices services)
    {
        Item = item;
        Title = item.Title;
        GidText = item.Gid is long gid ? $"#{gid}" : "无 GID";
        Versions = item.Versions
            .Select((info, index) => new GalleryVersionViewModel(info, index == 0))
            .ToList();

        int sourceCount = item.SourceCount;
        SummaryText = $"{Versions.Count} 个版本 · {sourceCount} 个数据源";
        DefaultHint = "默认打开：" + Versions[0].SourceName +
                      (item.IsPrimaryVersionPreferred ? "（已手动指定）" : "（自动选择）");

        int best = Versions.Max(v => v.Info.PageCount);
        bool pageCountsDiffer = Versions.Any(v => v.Info.PageCount != best);
        DifferHint = pageCountsDiffer
            ? $"页数不一致：最多 {best} 页，最少 {Versions.Min(v => v.Info.PageCount)} 页；阅读进度按作品共享，切换版本会定位到相同页码。"
            : "各版本页数一致；阅读进度与收藏按作品共享。";
    }

    public GalleryItemViewModel Item { get; }
    public string Title { get; }
    public string GidText { get; }
    public string SummaryText { get; }
    public string DefaultHint { get; }
    public string DifferHint { get; }
    public IReadOnlyList<GalleryVersionViewModel> Versions { get; }

    public GalleryInfo? ReadVersion { get; set; }
    public GalleryInfo? DefaultVersion { get; set; }
}

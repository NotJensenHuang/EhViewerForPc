using EhViewerForPc.Core.Models;

namespace EhViewerForPc.Core.Scanning;

/// <summary>One work, possibly present in several sources (device backups).</summary>
public sealed class GalleryGroup
{
    public required string WorkId { get; init; }
    public required IReadOnlyList<GalleryInfo> Versions { get; init; }

    /// <summary>Copy the reader opens; the best one unless the user picked another.</summary>
    public GalleryInfo Primary => Versions[0];

    public bool IsMultiVersion => Versions.Count > 1;

    public int SourceCount => Versions
        .Select(v => v.SourceId)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();
}

/// <summary>
/// Collapses copies of the same work that live in different sources into one
/// library entry. Copies inside a single source are never merged: two folders
/// with the same gallery id there are distinct downloads (for example different
/// scanlations), and merging them would hide a gallery from the grid.
/// </summary>
public static class LibraryMerger
{
    public static List<GalleryGroup> Group(
        IEnumerable<GalleryInfo> galleries,
        Func<string, string?>? preferredSource = null)
    {
        var buckets = new Dictionary<string, List<GalleryInfo>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        int total = 0;

        foreach (GalleryInfo gallery in galleries)
        {
            total++;
            string key = string.IsNullOrEmpty(gallery.WorkId)
                ? "stable:" + gallery.StableId
                : gallery.WorkId;
            if (!buckets.TryGetValue(key, out List<GalleryInfo>? bucket))
            {
                bucket = [];
                buckets[key] = bucket;
                order.Add(key);
            }

            bucket.Add(gallery);
        }

        var result = new List<GalleryGroup>(total);
        foreach (string key in order)
        {
            List<GalleryInfo> bucket = buckets[key];
            if (CountSources(bucket) < 2)
            {
                // Single source: keep every copy as its own card (previous behaviour).
                foreach (GalleryInfo gallery in bucket)
                {
                    result.Add(new GalleryGroup { WorkId = key, Versions = [gallery] });
                }

                continue;
            }

            string? preferred = preferredSource?.Invoke(key);
            bucket.Sort((a, b) => Compare(a, b, preferred));
            result.Add(new GalleryGroup { WorkId = key, Versions = bucket.ToArray() });
        }

        return result;
    }

    private static int CountSources(List<GalleryInfo> bucket)
    {
        if (bucket.Count < 2) return 1;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GalleryInfo gallery in bucket)
        {
            if (seen.Add(gallery.SourceId ?? string.Empty) && seen.Count > 1) return 2;
        }

        return 1;
    }

    /// <summary>
    /// Ordering used both for picking the default copy and for the version list:
    /// explicit choice, then completeness, then page count, then cover presence,
    /// then recency.
    /// </summary>
    private static int Compare(GalleryInfo a, GalleryInfo b, string? preferred)
    {
        int queued = a.IsQueuedDownload.CompareTo(b.IsQueuedDownload);
        if (queued != 0) return queued;

        int preferredA = Matches(a, preferred) ? 0 : 1;
        int preferredB = Matches(b, preferred) ? 0 : 1;
        if (preferredA != preferredB) return preferredA - preferredB;

        int incompleteA = a.IsIncomplete ? 1 : 0;
        int incompleteB = b.IsIncomplete ? 1 : 0;
        if (incompleteA != incompleteB) return incompleteA - incompleteB;

        if (a.PageCount != b.PageCount) return b.PageCount.CompareTo(a.PageCount);

        int thumbA = a.HasThumb ? 0 : 1;
        int thumbB = b.HasThumb ? 0 : 1;
        if (thumbA != thumbB) return thumbA - thumbB;

        if (a.LastWriteUtc != b.LastWriteUtc) return b.LastWriteUtc.CompareTo(a.LastWriteUtc);

        int byName = string.Compare(a.FolderName, b.FolderName, StringComparison.OrdinalIgnoreCase);
        return byName != 0
            ? byName
            : string.Compare(a.StableId, b.StableId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Matches(GalleryInfo gallery, string? sourceId) =>
        !string.IsNullOrEmpty(sourceId) &&
        string.Equals(gallery.SourceId, sourceId, StringComparison.OrdinalIgnoreCase);
}

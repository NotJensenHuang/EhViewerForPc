namespace EhViewerReader.Core.Models;

/// <summary>Metadata belongs to a work, independently of download tasks and local copies.</summary>
public sealed class WorkRecord
{
    public string WorkId { get; set; } = string.Empty;
    public long? Gid { get; set; }
    public string Token { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public ImageQuality Quality { get; set; }
    public WorkRecord Clone() => (WorkRecord)MemberwiseClone();
}

public sealed class WorkCatalogFile
{
    public Dictionary<string, WorkRecord> Works { get; set; } = new(StringComparer.Ordinal);
}

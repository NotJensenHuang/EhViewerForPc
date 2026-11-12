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
    public GalleryMetadata? Metadata { get; set; }
    public WorkRecord Clone()
    {
        var copy = (WorkRecord)MemberwiseClone();
        copy.Metadata = Metadata?.Clone();
        return copy;
    }

    public static WorkRecord FromDetails(Online.EhGalleryDetails details) => new()
    {
        WorkId = details.Gid.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Gid = details.Gid, Token = details.Token, Title = details.DisplayTitle,
        ThumbnailUrl = details.ThumbUrl, PageCount = details.Pages,
        Metadata = new GalleryMetadata
        {
            JapaneseTitle = details.JapaneseTitle, Category = details.Category, Language = details.Language,
            Uploader = details.Uploader, Posted = details.Posted, FileSize = details.FileSize,
            Rating = details.Rating, Tags = details.Tags.Select(t => t.SearchTerm).ToArray(),
        },
    };
}

/// <summary>Offline presentation data; excludes API keys and account-specific actions.</summary>
public sealed class GalleryMetadata
{
    public string JapaneseTitle { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Uploader { get; set; } = string.Empty;
    public string Posted { get; set; } = string.Empty;
    public string FileSize { get; set; } = string.Empty;
    public double Rating { get; set; }
    public string[] Tags { get; set; } = [];
    public GalleryMetadata Clone()
    {
        var copy = (GalleryMetadata)MemberwiseClone();
        copy.Tags = (string[])Tags.Clone();
        return copy;
    }
}

public sealed class WorkCatalogFile
{
    public Dictionary<string, WorkRecord> Works { get; set; } = new(StringComparer.Ordinal);
}

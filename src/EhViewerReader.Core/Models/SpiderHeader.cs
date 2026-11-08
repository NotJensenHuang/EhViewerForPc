namespace EhViewerReader.Core.Models;

public sealed class SpiderHeader
{
    public int Version { get; set; }
    public bool IsCbor { get; set; }
    public int StartPage { get; set; }
    public bool HasGid { get; set; }
    public long Gid { get; set; }
    public string? Token { get; set; }
    public bool HasPages { get; set; }
    public int Pages { get; set; }
    public int PreviewPages { get; set; }
    public int PreviewPerPage { get; set; }
    public bool IsValid { get; set; }
    public string? Error { get; set; }

    public bool HasAnyUsefulData => HasGid || HasPages;
}

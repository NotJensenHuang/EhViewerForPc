namespace EhViewerForPc.Core.Models;

/// <summary>Stores opt-in local image search preferences without credentials.</summary>
public sealed class LocalImageSearchSettings
{
    /// <summary>Gets or sets whether local image search is enabled.</summary>
    public bool Enabled { get; set; }
    /// <summary>Gets or sets the index root, with an empty value using application data.</summary>
    public string StorageDirectory { get; set; } = "";
    /// <summary>Gets or sets selected sources; an empty list means all enabled sources.</summary>
    public List<string> SourceIds { get; set; } = [];
    /// <summary>Gets or sets the probe count (4, 16 or 64).</summary>
    public int NProbe { get; set; } = 16;
    /// <summary>Gets or sets whether completed downloads schedule a background update.</summary>
    public bool AutoUpdateAfterDownload { get; set; } = true;
    /// <summary>Normalizes preferences without creating or opening an index.</summary>
    public void Normalize()
    {
        StorageDirectory = StorageDirectory?.Trim() ?? "";
        SourceIds = (SourceIds ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToList();
        if (NProbe is not (4 or 16 or 64)) NProbe = 16;
    }
}

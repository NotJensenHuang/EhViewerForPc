using System.Text.Json.Serialization;

namespace EhViewerForPc.Core.Models;

/// <summary>
/// One root folder that holds EhViewer downloads. Several sources are supported
/// because the same library is commonly mirrored to per-device backups on
/// different drives; the app never writes to any of them.
/// </summary>
public sealed class LibrarySource
{
    /// <summary>Stable identifier; also used for the per-source scan cache file.</summary>
    public string Id { get; set; } = string.Empty;

    public string RootPath { get; set; } = string.Empty;

    /// <summary>Optional user label; falls back to the folder name.</summary>
    public string? Name { get; set; }

    public bool Enabled { get; set; } = true;

    public string? LastScanUtc { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? FolderName : Name!;

    [JsonIgnore]
    public string FolderName
    {
        get
        {
            string trimmed = RootPath.TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            string leaf = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(leaf) ? trimmed : leaf;
        }
    }

    [JsonIgnore]
    public bool Exists => !string.IsNullOrWhiteSpace(RootPath) && Directory.Exists(RootPath);

    public LibrarySource Clone() => new()
    {
        Id = Id,
        RootPath = RootPath,
        Name = Name,
        Enabled = Enabled,
        LastScanUtc = LastScanUtc,
    };
}

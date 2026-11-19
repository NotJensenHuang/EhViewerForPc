namespace EhViewerForPc.Core.Models;

/// <summary>应用退出后仍可接续的图片索引任务；不持有任何源文件写入权限。</summary>
public sealed class ImageIndexBuildCheckpoint
{
    public int SchemaVersion { get; set; } = 1;
    public string GenerationId { get; set; } = string.Empty;
    public string PreviousGenerationId { get; set; } = string.Empty;
    public string ConfigurationId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public bool Rebuild { get; set; }
    public bool ResumeOnStartup { get; set; } = true;
    public ImageIndexBuildPhase Phase { get; set; }
    public int TotalPages { get; set; }
    public bool PlanPrepared { get; set; }
    public bool FeaturesMigrated { get; set; }
    public bool BackendPrepared { get; set; }
    public bool SourcesVerified { get; set; }
    public ImageIndexGalleryCheckpoint[] Galleries { get; set; } = [];
    /// <summary>下载完成触发的定向任务。旧任务和手动建库沿用全库计划。</summary>
    public string[]? ChangedFolders { get; set; }
}

public enum ImageIndexBuildPhase { Preparing, Extracting, Training, Building, CheckingSources }

public sealed record ImageIndexGalleryCheckpoint(string SourceId, string StableId, string WorkId, string Folder);

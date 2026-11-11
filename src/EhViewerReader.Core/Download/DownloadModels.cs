using System.Globalization;
using System.Text.Json.Serialization;
using EhViewerReader.Core.Models;

namespace EhViewerReader.Core.Download;

public enum DownloadState
{
    Pending = 0,
    Running = 1,
    Paused = 2,
    Completed = 3,
    Failed = 4,
}

/// <summary>
/// One gallery download. Persisted as-is, so every runtime-only value is a
/// computed property; the page list is re-fetched when a task resumes.
/// </summary>
public sealed class DownloadTask
{
    public long Gid { get; set; }
    public string Token { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string ThumbUrl { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public string DestinationRoot { get; set; } = string.Empty;

    /// <summary>Image size this task was queued with (original vs site-resampled).</summary>
    public ImageQuality Quality { get; set; } = ImageQuality.Original;

    public int ResampledWidth { get; set; } = 1280;
    public int PageCount { get; set; }
    public int DownloadedPages { get; set; }
    public long TotalBytes { get; set; }
    public DownloadState State { get; set; } = DownloadState.Pending;
    public string Message { get; set; } = string.Empty;
    public string AddedUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public string? FinishedUtc { get; set; }
    public List<int> FailedPages { get; set; } = [];
    public List<int> ResampledPages { get; set; } = [];

    [JsonIgnore]
    public string Id => Gid.ToString(CultureInfo.InvariantCulture);

    [JsonIgnore]
    public bool IsFinished => State == DownloadState.Completed;

    [JsonIgnore]
    public double Progress => PageCount <= 0 ? 0 : Math.Clamp(DownloadedPages / (double)PageCount, 0, 1);

    [JsonIgnore]
    public string ProgressText => PageCount <= 0
        ? $"{DownloadedPages} 页"
        : $"{DownloadedPages}/{PageCount} 页";

    [JsonIgnore]
    public string StateText => State switch
    {
        DownloadState.Pending => "等待中",
        DownloadState.Running => "下载中",
        DownloadState.Paused => "已暂停",
        DownloadState.Completed => "已完成",
        DownloadState.Failed => "失败",
        _ => State.ToString(),
    };

    [JsonIgnore]
    public string SizeText => TotalBytes <= 0 ? "—" : FormatSize(TotalBytes);

    [JsonIgnore]
    public string DisplayTitle => Title.Length > 0 ? Title : $"#{Gid}";

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F0", CultureInfo.InvariantCulture) + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + " MB";
        return (bytes / (1024.0 * 1024 * 1024)).ToString("F2", CultureInfo.InvariantCulture) + " GB";
    }
}

public sealed class DownloadArchiveEntry
{
    public long Gid { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public long TotalBytes { get; set; }
    public string FinishedUtc { get; set; } = DateTime.UtcNow.ToString("O");

    [JsonIgnore]
    public string DisplayTitle => Title.Length > 0 ? Title : $"#{Gid}";

    [JsonIgnore]
    public string SizeText => TotalBytes <= 0 ? "—" : DownloadTask.FormatSize(TotalBytes);

    [JsonIgnore]
    public string FinishedText => DateTime.TryParse(FinishedUtc, out DateTime value)
        ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : FinishedUtc;
}

public sealed class DownloadFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<DownloadTask> Tasks { get; set; } = [];
    public List<DownloadArchiveEntry> Archive { get; set; } = [];
}

public sealed class DownloadProgressEventArgs : EventArgs
{
    public required DownloadTask Task { get; init; }
    public int Page { get; init; }
}

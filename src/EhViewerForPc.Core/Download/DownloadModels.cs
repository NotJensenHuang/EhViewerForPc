using System.Globalization;
using System.Text.Json.Serialization;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;

namespace EhViewerForPc.Core.Download;

public enum DownloadState
{
    Pending = 0,
    Running = 1,
    Paused = 2,
    Completed = 3,
    Failed = 4,
}

/// <summary>
/// One gallery download. Runtime-only transfer metrics are excluded from JSON;
/// the page list is re-fetched when a task resumes.
/// </summary>
public sealed class DownloadTask
{
    private double _bytesPerSecond;
    [JsonIgnore]
    public double BytesPerSecond
    {
        get => State == DownloadState.Running ? Volatile.Read(ref _bytesPerSecond) : 0;
        internal set => Volatile.Write(ref _bytesPerSecond, value);
    }
    public string TaskKey { get; set; } = string.Empty;
    /// <summary>仅在用户主动补页时写入指定的既有副本，不覆盖原文件或元数据。</summary>
    public bool FillMissingOnly { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NeedsGalleryDetails { get; set; }
    public long Gid { get; set; }
    public string Token { get; set; } = string.Empty;
    /// <summary>随画廊链接入队时记录其站点。旧任务仍使用当前站点。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EhSite? Site { get; set; }
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
    /// <summary>
    /// 站点明确回答「画廊不可用」时置位。任务仍保留在队列和磁盘上，但归入
    /// 「画廊不可用」分类并跳过自动开始与重试，直到用户移除标记或重新入队。
    /// </summary>
    public bool GalleryUnavailable { get; set; }
    public string AddedUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public string? FinishedUtc { get; set; }
    public List<int> FailedPages { get; set; } = [];
    /// <summary>
    /// 目录里存在文件但容器不完整（0 字节以外的截断/错误内容）的页。与
    /// <see cref="FailedPages"/> 分开保存：前者可被重新下载替换，后者缺失
    /// 需要新增，两者的提示与修复方式不同。
    /// </summary>
    public List<int> CorruptPages { get; set; } = [];
    public List<int> ResampledPages { get; set; } = [];

    [JsonIgnore]
    public string Id => string.IsNullOrEmpty(TaskKey) ? Gid.ToString(CultureInfo.InvariantCulture) : TaskKey;

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

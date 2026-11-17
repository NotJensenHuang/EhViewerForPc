using System.Globalization;
using EhViewerForPc.Core.Download;
using EhViewerForPc.Core.Format;
using EhViewerForPc.Core.Models;

namespace EhViewerForPc.Services;

/// <summary>
/// Builds a reader source straight from a download task's folder. The local
/// library is deliberately not consulted: reading a partially downloaded task
/// must work with auto-import off, with the library still scanning, or with the
/// library never configured at all.
/// </summary>
public static class DownloadReader
{
    /// <summary>Scans the task folder for valid completed page files.</summary>
    public static GalleryInfo? TryBuildGalleryInfo(DownloadTask task, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(task.FolderPath) || !Directory.Exists(task.FolderPath))
        {
            error = "下载目录不存在：" + (task.FolderPath.Length > 0 ? task.FolderPath : "（未设置）");
            return null;
        }

        // 下载中的副本同样要按容器校验：任务失败后残留的截断文件不能冒充完成页，
        // 否则阅读器会静默跳过它而不给出任何提示。
        PageScanResult scan = PageFileParser.Scan(task.FolderPath, null, out string? scanError, validateContainers: true);
        if (scan.PageCount == 0)
        {
            error = string.IsNullOrEmpty(scanError)
                ? "该下载目录还没有可阅读的图片（可能仍在等待首张图片落盘）"
                : "无法读取下载目录：" + scanError;
            return null;
        }

        int expected = task.PageCount > 0 ? task.PageCount : scan.ExpectedPages;
        return new GalleryInfo
        {
            StableId = "dl-" + StableIdFactory.PathHash(task.FolderPath),
            WorkId = task.Gid.ToString(CultureInfo.InvariantCulture),
            Gid = task.Gid > 0 ? task.Gid : null,
            Token = task.Token,
            Title = task.DisplayTitle,
            FolderName = Path.GetFileName(task.FolderPath),
            FolderPath = task.FolderPath,
            PageCount = scan.PageCount,
            MaxPageNumber = scan.MaxPageNumber,
            ExpectedPages = expected > 0 ? expected : null,
            MissingRanges = scan.MissingRanges,
            IsIncomplete = scan.IsIncomplete || (expected > 0 && scan.PageCount < expected),
            SourceName = "下载任务",
            LastWriteUtc = Directory.GetLastWriteTimeUtc(task.FolderPath),
            ExtensionSummary = scan.ExtensionSummary,
            ZeroByteFiles = scan.ZeroByteFiles,
            CorruptPages = scan.CorruptPages,
            HeaderError = scanError,
        };
    }
}

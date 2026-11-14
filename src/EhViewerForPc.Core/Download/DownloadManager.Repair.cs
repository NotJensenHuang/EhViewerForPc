using System.Globalization;
using EhViewerForPc.Core.Format;
using EhViewerForPc.Core.Imaging;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Core.Download;

public sealed partial class DownloadManager
{
    /// <summary>仅由用户主动操作入队：补到选定副本的原路径，普通扫描不调用此入口。</summary>
    public DownloadTask EnqueueRepair(EhGalleryDetails details, string folder, string sourceRoot, bool startImmediately = true)
    {
        folder = Path.GetFullPath(folder);
        sourceRoot = Path.GetFullPath(sourceRoot);
        ValidateRepairTarget(folder, sourceRoot);
        if (details.Gid < 1 || string.IsNullOrWhiteSpace(details.Token)) throw new ArgumentException("画廊链接无效。");
        DownloadTask task;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_relocating) throw new IOException("下载目录正在迁移，请稍后继续。");
            task = _tasks.FirstOrDefault(t => PathIdentity.Comparer.Equals(t.FolderPath, folder))!;
            bool added = task is null;
            if (task is not null && task.Gid != details.Gid) throw new IOException("原目录与下载任务的画廊不一致。");
            if (task is not null && (task.State == DownloadState.Running ||
                (task.State == DownloadState.Pending && task.FillMissingOnly)))
            {
                if (startImmediately) SchedulePump();
                return task;
            }
            if (task is { FillMissingOnly: false } && _running.ContainsKey(task.Id))
                throw new IOException("原下载仍在停止写入，请稍后继续。");
            task ??= new DownloadTask
            {
                TaskKey = Guid.NewGuid().ToString("N"), Gid = details.Gid, Title = details.DisplayTitle,
                Category = details.Category, ThumbUrl = details.ThumbUrl, FolderPath = folder, DestinationRoot = sourceRoot,
                Quality = _settings.ImageQuality, ResampledWidth = _settings.ResampledWidth,
            };
            var previous = (task.State, task.Message, task.FillMissingOnly, task.Token, task.PageCount, task.FinishedUtc, task.FailedPages, task.DestinationRoot);
            task.FillMissingOnly = true;
            task.DestinationRoot = sourceRoot;
            task.Token = details.Token;
            task.PageCount = details.Pages;
            task.State = DownloadState.Pending;
            task.Message = "等待补齐缺页";
            task.FinishedUtc = null;
            task.FailedPages = [];
            if (added) _tasks.Insert(0, task);
            if (!SaveLocked())
            {
                if (added) _tasks.Remove(task);
                (task.State, task.Message, task.FillMissingOnly, task.Token, task.PageCount, task.FinishedUtc, task.FailedPages, task.DestinationRoot) = previous;
                throw new IOException("无法保存补页任务。");
            }
        }
        RaiseChanged();
        RaiseTask(task);
        if (startImmediately) SchedulePump();
        return task;
    }

    public bool IsQueued(DownloadTask task) { lock (_gate) return _tasks.Contains(task); }

    private void ValidateRepairTarget(string folder, string sourceRoot)
    {
        string target = Path.GetFullPath(folder), root = Path.GetFullPath(sourceRoot);
        if (!PathIdentity.IsDescendantOf(target, root) || !Directory.Exists(target) || !Directory.Exists(root))
            throw new IOException("原副本目录不可用，请重新扫描本地库。");
        if (PathIdentity.Comparer.Equals(target, _paths.BaseDirectory) || PathIdentity.IsDescendantOf(target, _paths.BaseDirectory))
            throw new IOException("补页目录不能位于应用数据目录。");
        for (DirectoryInfo? current = new(target); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("补页目录不能经过链接或目录联接。");
    }

    private static HashSet<int> ReadExistingRepairPages(string folder)
    {
        var scan = PageFileParser.Scan(folder, null, out string? error);
        if (error is not null) throw new IOException("无法读取原副本目录。");
        var pages = new HashSet<int>();
        foreach (var page in scan.Pages)
            foreach (var candidate in page.Candidates)
            {
                if ((File.GetAttributes(candidate.FilePath) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("补页目录不能包含链接页文件。");
                if (!ImageSignature.IsValidFile(candidate.FilePath)) continue;
                pages.Add(page.Number);
                break;
            }
        return pages;
    }

    private static string GetRepairPagePath(string folder, int page, string extension)
    {
        string path = Path.Combine(folder, page.ToString("D8", CultureInfo.InvariantCulture) + extension);
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("原目录存在同名文件，已保留，请检查后重试。");
        return path;
    }
}

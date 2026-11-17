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
        var request = ValidateRepairRequest(new(details, folder, sourceRoot));
        DownloadTask task;
        bool changed;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_relocating) throw new IOException("下载目录正在迁移，请稍后继续。");
            (task, var undo) = PrepareRepairLocked(request);
            changed = undo is not null;
            if (undo is not null && !SaveLocked())
            {
                undo.Restore(_tasks);
                throw new IOException("无法保存补页任务。");
            }
        }
        if (changed)
        {
            RaiseChanged();
            RaiseTask(task);
            if (startImmediately) SchedulePump();
        }
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

    private static HashSet<int> ReadExistingRepairPages(string folder, CancellationToken token = default)
    {
        var files = PageFileParser.Enumerate(folder, out string? error);
        if (error is not null) throw new IOException("无法读取原副本目录。");
        var pages = new HashSet<int>();
        foreach (var candidate in files)
        {
            token.ThrowIfCancellationRequested();
            if (!PageFileParser.TryParsePageName(candidate.FileName, out int page, out _)) continue;
            if ((File.GetAttributes(candidate.FilePath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("补页目录不能包含链接页文件。");
            if (pages.Contains(page) || !ImageSignature.IsValidFile(candidate.FilePath, FileShare.ReadWrite | FileShare.Delete)) continue;
            pages.Add(page);
        }
        return pages;
    }

    /// <summary>
    /// 补页目标路径。同名有效图片必须保留并报告冲突；0 字节、截断或错误内容
    /// 本身就是不可读文件，继续保留只会让「补齐」永远失败，因此允许就地替换。
    /// </summary>
    private static (string Path, bool ReplaceInvalid) GetRepairPagePath(string folder, int page, string extension)
    {
        string path = Path.Combine(folder, page.ToString("D8", CultureInfo.InvariantCulture) + extension);
        if (Directory.Exists(path)) throw new IOException("原目录存在同名目录，已保留，请检查后重试。");
        if (!File.Exists(path)) return (path, false);
        // 判定要与读取方共享：文件被阅读器打开时不能误判成「可替换」。
        if (ImageSignature.IsValidFile(path, FileShare.ReadWrite | FileShare.Delete))
            throw new IOException("原目录存在同名文件，已保留，请检查后重试。");
        return (path, true);
    }

    /// <summary>
    /// 一页写入成功后，清掉同页号其他扩展名的无效遗留文件。重下换容器
    /// （webp → jpg）时旧文件会留在目录里：目录看起来 171 个文件全齐，
    /// 但其中一个仍是截断的；别的阅读器按扩展名挑页时还可能读到它。
    /// 只删除容器校验判定无效的文件，有效候选一律保留。
    /// </summary>
    private static void RemoveInvalidSamePageFiles(string folder, int page, string keepPath)
    {
        string prefix = page.ToString("D8", CultureInfo.InvariantCulture);
        foreach (string extension in new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" })
        {
            string path = Path.Combine(folder, prefix + extension);
            if (PathIdentity.Comparer.Equals(path, keepPath)) continue;
            try
            {
                if (!File.Exists(path)) continue;
                if (ImageSignature.IsValidFile(path, FileShare.ReadWrite | FileShare.Delete)) continue;
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不影响这一页已经写入成功的事实，下次补页还会再判一次。
            }
        }
    }
}

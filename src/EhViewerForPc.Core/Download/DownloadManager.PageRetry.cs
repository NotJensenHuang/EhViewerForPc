using EhViewerForPc.Core.Format;
using EhViewerForPc.Core.Imaging;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Core.Download;

public sealed partial class DownloadManager
{
    private readonly CancellationTokenSource _pageRetryStop = new();
    private readonly Dictionary<string, PageRetryOperation> _pageRetries = new(PathIdentity.Comparer);
    private sealed record PageRetryResult(string? Path, Exception? Error);
    private sealed record PageRetryOperation(int Page, Task<PageRetryResult> Work)
    {
        public bool ResumeQueue { get; set; }
    }

    /// <summary>用户明确重试一页：等待同目录在途写入，原子替换该页，其他页及元数据保持原样。</summary>
    public async Task<string> RedownloadPageAsync(EhGalleryDetails details, int pageNumber, string folder,
        string sourceRoot, string? existingPath, CancellationToken token)
    {
        folder = Path.GetFullPath(folder);
        sourceRoot = Path.GetFullPath(sourceRoot);
        if (details.Gid <= 0 || string.IsNullOrWhiteSpace(details.Token) || pageNumber <= 0 ||
            details.Pages > 0 && pageNumber > details.Pages) throw new ArgumentException("当前页缺少有效的画廊链接或原始页号。");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            PageRetryOperation operation;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (IsLocalCopyDeleting(folder)) throw new IOException("该副本正在删除。");
                if (_relocating) throw new IOException("下载目录正在迁移，请稍后重试。");
                if (!_pageRetries.TryGetValue(folder, out operation!))
                {
                    var tasks = _tasks.Where(t => PathIdentity.Comparer.Equals(t.FolderPath, folder)).ToArray();
                    var active = _running.Values.Where(writer => FoldersOverlap(writer.Download.FolderPath, folder)).ToArray();
                    if (tasks.Any(t => t.Gid != details.Gid) || active.Any(writer => writer.Download.Gid != details.Gid))
                        throw new IOException("原目录与下载任务的画廊不一致。");
                    var writers = active.Select(writer => writer.Work).ToArray();
                    var original = tasks.FirstOrDefault() ?? active.FirstOrDefault().Download;
                    ImageQuality quality = original?.Quality ?? _settings.ImageQuality;
                    int width = original?.ResampledWidth ?? _settings.ResampledWidth;
                    operation = new(pageNumber, Task.Run(() => RunPageRetryAsync(details, pageNumber, folder, sourceRoot, existingPath, quality, width, writers)));
                    _pageRetries.Add(folder, operation);
                }
            }
            var result = await operation.Work.WaitAsync(token).ConfigureAwait(false);
            if (operation.Page != pageNumber) continue;
            if (result.Error is not null) throw new IOException("无法重新下载当前页：" + result.Error.Message, result.Error);
            return result.Path!;
        }
    }

    private async Task<PageRetryResult> RunPageRetryAsync(EhGalleryDetails details, int pageNumber, string folder,
        string root, string? existingPath, ImageQuality quality, int width, Task[] writers)
    {
        string temporary = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".retry");
        CancellationToken token = _pageRetryStop.Token;
        try
        {
            await Task.WhenAll(writers).WaitAsync(token).ConfigureAwait(false);
            ValidateRepairTarget(folder, root);
            string? target = existingPath is null ? null : Path.GetFullPath(existingPath);
            if (target is null && writers.Length > 0)
            {
                var files = PageFileParser.Enumerate(folder, out string? error);
                if (error is not null) throw new IOException(error);
                target = files.FirstOrDefault(f => PageFileParser.TryParsePageName(f.FileName, out int page, out _) && page == pageNumber)?.FilePath;
            }
            if (target is not null && (!PathIdentity.Comparer.Equals(Path.GetDirectoryName(target), folder) ||
                !PageFileParser.TryParsePageName(Path.GetFileName(target), out int number, out _) || number != pageNumber))
                throw new IOException("当前页路径与原始页号不一致。");
            var before = ReadRetryFileStamp(target);
            bool replace = target is not null;
            var list = await _client.GetPageListAsync(details.Gid, details.Token, 0, token).ConfigureAwait(false);
            EhPageRef? page = list.Pages.FirstOrDefault(p => p.Page == pageNumber);
            if (page is null && list.HasMore && list.Pages.Count > 0)
            {
                // 预览每页条数由站点决定，解析也可能少一两条；先按首页条数定位，
                // 再查相邻分页，避免固定步长猜错就把这一页判成「在线画廊中不存在」。
                int guess = (pageNumber - 1) / list.Pages.Count;
                foreach (int index in new[] { guess, guess - 1, guess + 1 }.Where(i => i > 0).Distinct())
                {
                    var candidate = await _client.GetPageListAsync(details.Gid, details.Token, index, token).ConfigureAwait(false);
                    page = candidate.Pages.FirstOrDefault(p => p.Page == pageNumber);
                    if (page is not null) break;
                }
            }

            if (page is null)
            {
                var all = await _client.GetAllPagesAsync(details.Gid, details.Token, details.Pages, null, token).ConfigureAwait(false);
                page = all.FirstOrDefault(p => p.Page == pageNumber);
            }

            if (page is null) throw new IOException("在线画廊中找不到当前页。");
            var resolved = await _client.ResolveImageAsync(page, quality == ImageQuality.Original, token, width, refresh: true).ConfigureAwait(false);
            // 手动重下载不命中本地副本或图片缓存，避免把坏页再次复制回来。
            await _client.DownloadToFileAsync(resolved.Url, page.PageUrl, temporary, (IProgress<ImageLoadStatus>?)null, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ValidateRepairTarget(folder, root);
            string extension = ImageSignature.DetectFile(temporary) switch
            {
                ImageKind.Png => ".png", ImageKind.Gif => ".gif", ImageKind.Webp => ".webp", _ => ".jpg"
            };
            if (target is null) target = GetRepairPagePath(folder, pageNumber, extension);
            else if (ReadRetryFileStamp(target) != before) throw new IOException("当前页文件已被其他操作修改，请重新打开后重试。");
            File.Move(temporary, target, overwrite: replace);
            UpdateTaskAfterPageRetry(details.Gid, folder, pageNumber);
            return new(target, null);
        }
        catch (Exception error)
        {
            _log?.Invoke($"Page retry failed ({details.Gid}-{pageNumber}): {error.Message}");
            return new(null, error);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            bool resume;
            lock (_gate)
            {
                _pageRetries.Remove(folder, out var operation);
                resume = operation?.ResumeQueue == true;
            }
            if (resume) SchedulePump();
        }
    }

    /// <summary>
    /// 阅读器单页重试成功后立即回填任务：移除该失败页、推进页数；全部补齐时
    /// 直接完成任务，避免下载页继续显示旧的失败页与失败消息。
    /// </summary>
    private void UpdateTaskAfterPageRetry(long gid, string folder, int pageNumber)
    {
        DownloadTask? updated = null;
        bool completed = false;
        lock (_gate)
        {
            var task = _tasks.FirstOrDefault(t => t.Gid == gid && PathIdentity.Comparer.Equals(t.FolderPath, folder));
            // 运行中的任务由下载流程自己维护计数，这里不插手。
            if (task is null || task.State == DownloadState.Running) return;

            bool wasFailed = task.FailedPages.Remove(pageNumber);
            bool changed = wasFailed;
            if (wasFailed && task.DownloadedPages < task.PageCount) task.DownloadedPages++;

            if (task.PageCount > 0 && task.FailedPages.Count == 0 && task.DownloadedPages >= task.PageCount &&
                task.State is DownloadState.Failed or DownloadState.Paused)
            {
                MarkCompletedLocked(task, "已完成");
                completed = true;
                changed = true;
            }
            else if (changed)
            {
                SaveLocked();
            }

            if (!changed) return;
            updated = task;
        }

        if (updated is null) return;
        RaiseTask(updated);
        if (completed) TaskCompleted?.Invoke(this, updated);
        RaiseChanged();
    }

    private static (long Length, long Mtime)? ReadRetryFileStamp(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        var file = new FileInfo(path);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("当前页不能是链接文件。");
        return (file.Length, file.LastWriteTimeUtc.Ticks);
    }
}

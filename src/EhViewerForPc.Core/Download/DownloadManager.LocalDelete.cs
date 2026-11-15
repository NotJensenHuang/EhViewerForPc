using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Core.Download;

public sealed partial class DownloadManager
{
    private readonly Dictionary<string, TaskCompletionSource> _localDeletes = new(PathIdentity.Comparer);

    private static bool FolderWithin(string path, string root)
    {
        if (path.Length == 0 || root.Length == 0) return false;
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return PathIdentity.Comparer.Equals(path, root) || PathIdentity.IsDescendantOf(path, root);
    }
    private static bool FoldersOverlap(string left, string right) => FolderWithin(left, right) || FolderWithin(right, left);
    private bool IsLocalCopyDeleting(string folder) => _localDeletes.Count > 0 && _localDeletes.Keys.Any(target => FoldersOverlap(folder, target));

    /// <summary>用户确认删除副本：阻止新写入，取消下载，等待在途文件关闭，再由平台回收目录。</summary>
    public async Task DeleteLocalCopyAsync(string folder, string sourceRoot, Action<string> recycle, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        ValidateRepairTarget(folder, sourceRoot);
        if (FolderWithin(_paths.BaseDirectory, folder)) throw new IOException("不能删除包含应用数据的目录。");
        Task[] writers;
        bool registered = false;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_relocating || _archiveWriters != 0) throw new IOException("目录迁移或归档传输进行中，请完成后再删除。");
                if (IsLocalCopyDeleting(folder)) throw new IOException("该副本或其父目录正在删除。");
                if (!_localDeletes.TryAdd(folder, completion)) throw new IOException("该副本正在删除。");
                registered = true;
                // 活跃资源按写入者持有，不能依赖可提前移除的可见队列。
                var active = _running.Where(pair => FoldersOverlap(pair.Value.Download.FolderPath, folder)).ToArray();
                writers = active.Select(pair => pair.Value.Work).ToArray();
                writers = [.. writers, .. _pageRetries.Where(p => FoldersOverlap(p.Key, folder)).Select(p => p.Value.Work)];
                foreach (var task in _tasks.Where(t => FoldersOverlap(t.FolderPath, folder)))
                {
                    if (task.State != DownloadState.Completed)
                    {
                        task.State = DownloadState.Paused;
                        task.Message = "正在删除本地副本";
                    }
                }
                foreach (var writer in active)
                    if (_cancellations.TryGetValue(writer.Key, out var cancellation)) cancellation.Cancel();
                SaveLocked();
            }
            RaiseChanged();
            await Task.WhenAll(writers).WaitAsync(token).ConfigureAwait(false);
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                ValidateRepairTarget(folder, sourceRoot);
                recycle(folder);
                if (Directory.Exists(folder)) throw new IOException("副本目录仍存在，未移除本地记录。");
                lock (_gate)
                {
                    _tasks.RemoveAll(t => FolderWithin(t.FolderPath, folder));
                    _archive.RemoveAll(a => FolderWithin(a.FolderPath, folder));
                    SaveLocked();
                }
            }, token).ConfigureAwait(false);
        }
        finally
        {
            if (registered)
            {
                lock (_gate)
                {
                    foreach (var task in _tasks.Where(t => FoldersOverlap(t.FolderPath, folder)))
                        if (task.State == DownloadState.Paused) task.Message = "已暂停";
                    try { if (_tasks.Any(t => FoldersOverlap(t.FolderPath, folder))) SaveLocked(); }
                    finally
                    {
                        _localDeletes.Remove(folder);
                        completion.TrySetResult();
                    }
                }
                RaiseChanged();
            }
        }
    }
}

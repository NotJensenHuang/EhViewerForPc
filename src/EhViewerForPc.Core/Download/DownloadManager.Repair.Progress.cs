using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Core.Download;

public sealed partial class DownloadManager
{
    private readonly CancellationTokenSource _repairProgressStop = new();
    private Task _repairProgressTask = Task.CompletedTask;

    /// <summary>启动时只核对旧补页任务的有效页数，不启动下载；所有工作由下载服务观察并在退出时收尾。</summary>
    public Task RefreshRepairProgressAsync(CancellationToken token = default)
    {
        lock (_gate)
        {
            if (_disposed || !_repairProgressTask.IsCompleted) return _repairProgressTask;
            _repairProgressTask = Task.Run(() =>
            {
                try
                {
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _repairProgressStop.Token);
                    RefreshRepairProgress(cancellation.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { _log?.Invoke("Repair progress refresh failed: " + error.Message); }
            });
            return _repairProgressTask;
        }
    }

    private void RefreshRepairProgress(CancellationToken token)
    {
        (DownloadTask Task, string Folder, string Root, int Total)[] snapshot;
        lock (_gate) snapshot = _tasks.Where(t => t.FillMissingOnly && t.State is not (DownloadState.Completed or DownloadState.Running))
            .Select(t => (t, t.FolderPath, t.DestinationRoot, t.PageCount)).ToArray();
        var checkedTasks = new List<(DownloadTask Task, string Folder, int Total, int Count)>();
        foreach (var item in snapshot)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                ValidateRepairTarget(item.Folder, item.Root);
                int count = ReadExistingRepairPages(item.Folder, token).Count(page => item.Total <= 0 || page <= item.Total);
                checkedTasks.Add((item.Task, item.Folder, item.Total, count));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { _log?.Invoke("Repair folder progress unavailable: " + error.GetType().Name); }
        }
        var changed = new List<(DownloadTask Task, int Previous)>();
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            foreach (var item in checkedTasks)
            {
                var task = item.Task;
                if (!_tasks.Contains(task) || _running.ContainsKey(task.Id) || task.State is DownloadState.Running or DownloadState.Completed ||
                    !PathIdentity.Comparer.Equals(task.FolderPath, item.Folder) || task.PageCount != item.Total || task.DownloadedPages == item.Count) continue;
                changed.Add((task, task.DownloadedPages));
                task.DownloadedPages = item.Count;
            }
            if (changed.Count > 0 && !SaveLocked())
            {
                foreach (var item in changed) item.Task.DownloadedPages = item.Previous;
                return;
            }
        }
        foreach (var item in changed) RaiseTask(item.Task);
    }
}

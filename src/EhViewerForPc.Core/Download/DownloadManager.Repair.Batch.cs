using EhViewerForPc.Core.Online;
using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Core.Download;

public sealed record DownloadRepairRequest(EhGalleryDetails Details, string Folder, string Root)
{
    internal int ExistingPages { get; init; }
}
public sealed record DownloadRepairFailure(string Folder, string Message);
public sealed record DownloadRepairBatchResult(int Added, int Continued, int Existing, IReadOnlyList<DownloadRepairFailure> Failures);

public sealed partial class DownloadManager
{
    /// <summary>校验在锁外执行；有效请求统一保存一次，提交失败时回滚整个批次。</summary>
    public DownloadRepairBatchResult EnqueueRepairs(IReadOnlyList<DownloadRepairRequest> requests,
        IProgress<int>? progress = null, CancellationToken token = default, bool startImmediately = true)
    {
        var valid = new List<DownloadRepairRequest>(requests.Count);
        var failures = new List<DownloadRepairFailure>();
        var folders = new HashSet<string>(PathIdentity.Comparer);
        long lastReport = Environment.TickCount64;
        for (int i = 0; i < requests.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var request = ValidateRepairRequest(requests[i], token);
                if (folders.Add(request.Folder)) valid.Add(request);
            }
            catch (Exception error) { failures.Add(new(requests[i].Folder, error.Message)); }
            long now = Environment.TickCount64;
            if (now - lastReport >= 250 || i == requests.Count - 1) { progress?.Report(i + 1); lastReport = now; }
        }
        int added = 0, continued = 0, existing = 0;
        var changes = new List<RepairUndo>(valid.Count);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_relocating) throw new IOException("下载目录正在迁移，请稍后继续。");
            var byFolder = new Dictionary<string, DownloadTask>(PathIdentity.Comparer);
            foreach (var task in _tasks) byFolder.TryAdd(task.FolderPath, task);
            try
            {
                foreach (var request in valid)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var (_, undo) = PrepareRepairLocked(request, byFolder);
                        if (undo is null) existing++;
                        else
                        {
                            changes.Add(undo);
                            if (undo.Added) added++;
                            else continued++;
                        }
                    }
                    catch (IOException error) { failures.Add(new(request.Folder, error.Message)); }
                }
                token.ThrowIfCancellationRequested();
                if (changes.Count > 0 && !SaveLocked()) throw new IOException("无法保存补页任务，批次已回滚。");
            }
            catch
            {
                for (int i = changes.Count - 1; i >= 0; i--) changes[i].Restore(_tasks);
                throw;
            }
        }
        if (changes.Count > 0) RaiseChanged();
        foreach (var change in changes) RaiseTask(change.Task);
        if (startImmediately) SchedulePump();
        return new(added, continued, existing, failures);
    }

    private DownloadRepairRequest ValidateRepairRequest(DownloadRepairRequest request, CancellationToken token = default)
    {
        request = request with { Folder = Path.GetFullPath(request.Folder), Root = Path.GetFullPath(request.Root) };
        ValidateRepairTarget(request.Folder, request.Root);
        if (request.Details.Gid < 1 || string.IsNullOrWhiteSpace(request.Details.Token)) throw new ArgumentException("画廊链接无效。");
        var pages = ReadExistingRepairPages(request.Folder, token);
        return request with { ExistingPages = pages.Count(page => request.Details.Pages <= 0 || page <= request.Details.Pages) };
    }

    private (DownloadTask Task, RepairUndo? Undo) PrepareRepairLocked(DownloadRepairRequest request, Dictionary<string, DownloadTask>? byFolder = null)
    {
        var details = request.Details;
        var task = byFolder is null ? _tasks.FirstOrDefault(t => PathIdentity.Comparer.Equals(t.FolderPath, request.Folder)) : byFolder.GetValueOrDefault(request.Folder);
        bool added = task is null;
        if (task is not null && task.Gid != details.Gid) throw new IOException("原目录与下载任务的画廊不一致。");
        if (task is not null && (task.State == DownloadState.Running || (task.State == DownloadState.Pending && task.FillMissingOnly))) return (task, null);
        if (task is { FillMissingOnly: false } && _running.ContainsKey(task.Id)) throw new IOException("原下载仍在停止写入，请稍后继续。");
        task ??= new DownloadTask
        {
            TaskKey = Guid.NewGuid().ToString("N"), Gid = details.Gid, Title = details.DisplayTitle,
            Category = details.Category, ThumbUrl = details.ThumbUrl, FolderPath = request.Folder, DestinationRoot = request.Root,
            Quality = _settings.ImageQuality, ResampledWidth = _settings.ResampledWidth,
        };
        var undo = new RepairUndo(task, added, task.State, task.Message, task.FillMissingOnly, task.Token, task.PageCount, task.DownloadedPages, task.FinishedUtc, task.FailedPages, task.DestinationRoot);
        task.FillMissingOnly = true;
        task.DestinationRoot = request.Root;
        task.Token = details.Token;
        task.PageCount = details.Pages;
        task.DownloadedPages = request.ExistingPages;
        task.State = DownloadState.Pending;
        task.Message = "等待补齐缺页";
        task.FinishedUtc = null;
        task.FailedPages = [];
        if (added) _tasks.Insert(0, task);
        return (task, undo);
    }

    private sealed record RepairUndo(DownloadTask Task, bool Added, DownloadState State, string Message, bool FillMissingOnly,
        string Token, int PageCount, int DownloadedPages, string? FinishedUtc, List<int> FailedPages, string Root)
    {
        internal void Restore(List<DownloadTask> tasks)
        {
            if (Added) tasks.Remove(Task);
            (Task.State, Task.Message, Task.FillMissingOnly, Task.Token, Task.PageCount, Task.DownloadedPages, Task.FinishedUtc, Task.FailedPages, Task.DestinationRoot) =
                (State, Message, FillMissingOnly, Token, PageCount, DownloadedPages, FinishedUtc, FailedPages, Root);
        }
    }
}

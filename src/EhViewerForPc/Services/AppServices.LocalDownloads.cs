using EhViewerForPc.Core.Download;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Services;

public sealed record LocalRepairFailure(string Folder, string Message);
public sealed record LocalRepairProgress(int Processed, int Total);
public sealed record LocalRepairResult(int Added, int Continued, int Existing, IReadOnlyList<LocalRepairFailure> Failures);

public sealed partial class AppServices
{
    public bool CanContinueLocalDownload(GalleryInfo info) => info.IsIncomplete && info.Gid is > 0 && !info.HasGidConflict &&
        Settings.Sources.Any(source => source.Id == info.SourceId && source.Enabled);

    /// <summary>由明确的用户操作授权为该副本补页；捕获路径后交给既有下载服务，不修改全局下载根目录。</summary>
    public async Task<DownloadTask> ContinueLocalDownloadAsync(GalleryInfo info, CancellationToken token)
    {
        var request = CreateRepairRequest(info);
        token.ThrowIfCancellationRequested();
        // 点击已授权入队；关闭详情页只停止页面观察，不撤销已提交的任务。
        return await Task.Run(() => Downloads.EnqueueRepair(request.Details, request.Folder, request.Root));
    }

    public async Task<LocalRepairResult> ContinueLocalDownloadsAsync(IReadOnlyList<GalleryInfo> copies,
        IProgress<LocalRepairProgress>? progress, CancellationToken token)
    {
        var requests = new List<DownloadRepairRequest>();
        var failures = new List<LocalRepairFailure>();
        var folders = new HashSet<string>(PathIdentity.Comparer);
        int existing = 0;
        var queued = new Dictionary<string, DownloadTask>(PathIdentity.Comparer);
        foreach (var task in Downloads.Tasks) queued.TryAdd(task.FolderPath, task);
        foreach (var info in copies)
        {
            if (!info.IsIncomplete || !folders.Add(info.FolderPath)) continue;
            if (queued.TryGetValue(info.FolderPath, out var task) && task.State is DownloadState.Pending or DownloadState.Running)
            {
                existing++;
                continue;
            }
            try { requests.Add(CreateRepairRequest(info)); }
            catch (Exception error) { failures.Add(new(info.FolderPath, error.Message)); }
        }
        return await Task.Run(() =>
        {
            var result = Downloads.EnqueueRepairs(requests, new RepairBatchProgress(progress, requests.Count), token);
            failures.AddRange(result.Failures.Select(f => new LocalRepairFailure(f.Folder, f.Message)));
            return new LocalRepairResult(result.Added, result.Continued, existing + result.Existing, failures);
        }, token);
    }

    private DownloadRepairRequest CreateRepairRequest(GalleryInfo info)
    {
        if (!CanContinueLocalDownload(info)) throw new InvalidOperationException("此副本无法继续下载，请检查数据源和画廊链接。");
        var source = Settings.Sources.First(s => s.Id == info.SourceId && s.Enabled);
        string root = source.RootPath, folder = info.FolderPath;
        long gid = info.Gid!.Value;
        string galleryToken = info.Token.Length > 0 ? info.Token : Works.Get(info.WorkId)?.Token ?? string.Empty;
        if (galleryToken.Length == 0) throw new InvalidOperationException("本地副本缺少画廊 token，请先补充原画廊链接。");
        if (!PathIdentity.IsDescendantOf(folder, root)) throw new IOException("副本路径已变化，请重新扫描本地库。");
        var details = new EhGalleryDetails { Gid = gid, Token = galleryToken, Title = info.Title, Pages = info.ExpectedPages ?? 0 };
        return new(details, folder, root);
    }

    private sealed class RepairBatchProgress(IProgress<LocalRepairProgress>? observer, int total) : IProgress<int>
    {
        public void Report(int value) => observer?.Report(new(value, total));
    }
}

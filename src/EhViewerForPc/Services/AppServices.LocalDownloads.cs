using EhViewerForPc.Core.Download;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.Core.Format;
using Avalonia.Threading;

namespace EhViewerForPc.Services;

public sealed record LocalRepairFailure(string Folder, string Message);
public sealed record LocalRepairProgress(int Processed, int Total);
public sealed record LocalRepairResult(int Added, int Continued, int Existing, IReadOnlyList<LocalRepairFailure> Failures)
{
    public int Complete { get; init; }
}

public sealed partial class AppServices
{
    public event EventHandler<GalleryInfo>? LocalCopyChecked;
    public bool CanContinueLocalDownload(GalleryInfo info) => info.IsIncomplete && info.Gid is > 0 && !info.HasGidConflict &&
        Settings.Sources.Any(source => source.Id == info.SourceId && source.Enabled);

    /// <summary>由明确的用户操作授权为该副本补页；捕获路径后交给既有下载服务，不修改全局下载根目录。</summary>
    public async Task<DownloadTask?> ContinueLocalDownloadAsync(GalleryInfo info, CancellationToken token)
    {
        var result = await ContinueLocalDownloadsAsync([info], null, token);
        if (result.Failures.Count > 0) throw new IOException(result.Failures[0].Message);
        return result.Complete > 0 ? null : Downloads.Tasks.FirstOrDefault(t => PathIdentity.Comparer.Equals(t.FolderPath, info.FolderPath));
    }

    public async Task<LocalRepairResult> ContinueLocalDownloadsAsync(IReadOnlyList<GalleryInfo> copies,
        IProgress<LocalRepairProgress>? progress, CancellationToken token)
    {
        var requests = new List<DownloadRepairRequest>();
        var failures = new List<LocalRepairFailure>();
        var folders = new HashSet<string>(PathIdentity.Comparer);
        int existing = 0;
        var candidates = new List<(GalleryInfo Info, LibrarySource Source, int? Expected, DownloadRepairRequest? Request, string? Error)>();
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
            var source = Settings.Sources.FirstOrDefault(s => s.Id == info.SourceId && s.Enabled)?.Clone();
            if (source is null) { failures.Add(new(info.FolderPath, "数据源不可用")); continue; }
            int? expected = task?.PageCount > 0 ? task.PageCount : info.ExpectedPages ?? Works.Get(info.WorkId)?.PageCount;
            DownloadRepairRequest? request = null;
            string? message = null;
            try { request = CreateRepairRequest(info); }
            catch (Exception error) { message = error.Message; }
            candidates.Add((info, source, expected, request, message));
        }
        bool qualify = Settings.ActiveSources.Count > 1;
        var checkedCopies = new List<GalleryInfo>();
        var summary = await Task.Run(() =>
        {
            int complete = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var candidate = candidates[i];
                try
                {
                    var info = candidate.Info;
                    if (!PathIdentity.IsDescendantOf(info.FolderPath, candidate.Source.RootPath)) throw new IOException("副本路径已变化，请重新扫描本地库。");
                    var copy = Scanner.Scan(candidate.Source.RootPath, null, true, null, token, candidate.Source.Id,
                        candidate.Source.DisplayName, qualify, info.FolderPath).Galleries.Single();
                    copy.StableId = info.StableId;
                    if (!Directory.Exists(copy.FolderPath) || copy.HeaderError is not null && copy.PageCount == 0) throw new IOException("原副本目录不可用。");
                    if (candidate.Expected is > 0 && candidate.Expected != copy.ExpectedPages)
                    {
                        var scan = PageFileParser.Scan(copy.FolderPath, new SpiderHeader { HasPages = true, Pages = candidate.Expected.Value }, out string? error);
                        if (error is not null) throw new IOException(error);
                        copy.ExpectedPages = candidate.Expected;
                        copy.IsIncomplete = scan.IsIncomplete;
                        copy.MissingRanges = scan.MissingRanges;
                        copy.HasHeaderPageCountOutdated = scan.HeaderPageCountOutdated;
                    }
                    checkedCopies.Add(copy);
                    if (!copy.IsIncomplete) { complete++; continue; }
                    if (candidate.Request is null) throw new IOException(candidate.Error);
                    requests.Add(candidate.Request);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { failures.Add(new(candidate.Info.FolderPath, error.Message)); }
                progress?.Report(new(i + 1, candidates.Count));
            }
            var result = Downloads.EnqueueRepairs(requests, new RepairBatchProgress(progress, requests.Count), token);
            failures.AddRange(result.Failures.Select(f => new LocalRepairFailure(f.Folder, f.Message)));
            return new LocalRepairResult(result.Added, result.Continued, existing + result.Existing, failures) { Complete = complete };
        }, token);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var copy in checkedCopies) LocalCopyChecked?.Invoke(this, copy);
        });
        return summary;
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

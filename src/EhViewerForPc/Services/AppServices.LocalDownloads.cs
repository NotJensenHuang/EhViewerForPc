using EhViewerForPc.Core.Download;
using EhViewerForPc.Core.Imaging;
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
    public event EventHandler<(GalleryInfo Copy, PageScanResult Pages)>? LocalReaderPagesValidated;

    /// <summary>阅读器提交校验结果，资料库只更新仍使用该快照的已登记副本。</summary>
    public void NotifyLocalReaderPagesValidated(GalleryInfo copy, PageScanResult pages) => LocalReaderPagesValidated?.Invoke(this, (copy, pages));
    // 损坏页与缺页同等对待：两者都只能靠补下载修复。
    public bool CanContinueLocalDownload(GalleryInfo info) => (info.IsIncomplete || info.CorruptPages > 0) &&
        info.Gid is > 0 && !info.HasGidConflict &&
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
            // 损坏页不改变页数，但同样需要进入补页流程。
            if ((!info.IsIncomplete && info.CorruptPages == 0) || !folders.Add(info.FolderPath)) continue;
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
                        candidate.Source.DisplayName, qualify, info.FolderPath, validatePages: true).Galleries.Single();
                    copy.StableId = info.StableId;
                    if (!Directory.Exists(copy.FolderPath) || copy.HeaderError is not null && copy.PageCount == 0) throw new IOException("原副本目录不可用。");
                    if (candidate.Expected is > 0 && candidate.Expected != copy.ExpectedPages)
                    {
                        var scan = PageFileParser.Scan(copy.FolderPath, new SpiderHeader { HasPages = true, Pages = candidate.Expected.Value }, out string? error, validateContainers: true);
                        if (error is not null) throw new IOException(error);
                        copy.ExpectedPages = candidate.Expected;
                        copy.IsIncomplete = scan.IsIncomplete;
                        copy.MissingRanges = scan.MissingRanges;
                        copy.HasHeaderPageCountOutdated = scan.HeaderPageCountOutdated;
                    }
                    if (!SameLocalPageState(info, copy)) checkedCopies.Add(copy);
                    if (!copy.IsIncomplete) { complete++; continue; }
                    if (candidate.Request is null) throw new IOException(candidate.Error);
                    requests.Add(candidate.Request);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { failures.Add(new(candidate.Info.FolderPath, error.Message)); }
                progress?.Report(new(i + 1, candidates.Count));
            }
            var result = requests.Count == 0 ? new DownloadRepairBatchResult(0, 0, 0, [])
                : Downloads.EnqueueRepairs(requests, new RepairBatchProgress(progress, requests.Count), token);
            failures.AddRange(result.Failures.Select(f => new LocalRepairFailure(f.Folder, f.Message)));
            return new LocalRepairResult(result.Added, result.Continued, existing + result.Existing, failures) { Complete = complete };
        }, token);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var copy in checkedCopies) LocalCopyChecked?.Invoke(this, copy);
        });
        return summary;
    }

    internal static bool SameLocalPageState(GalleryInfo before, GalleryInfo after) =>
        before.PageCount == after.PageCount && before.MaxPageNumber == after.MaxPageNumber &&
        before.ExpectedPages == after.ExpectedPages && before.IsIncomplete == after.IsIncomplete &&
        before.ZeroByteFiles == after.ZeroByteFiles && before.CorruptPages == after.CorruptPages &&
        before.HasHeaderPageCountOutdated == after.HasHeaderPageCountOutdated &&
        before.HasNaturalPageNames == after.HasNaturalPageNames && before.LastWriteUtc == after.LastWriteUtc &&
        before.HeaderError == after.HeaderError && before.MissingRanges.SequenceEqual(after.MissingRanges);

    internal async Task RetryLocalPageAsync(GalleryInfo info, GalleryPage page, CancellationToken token,
        IProgress<ImageLoadStatus>? progress = null)
    {
        var task = Downloads.Tasks.FirstOrDefault(t => PathIdentity.Comparer.Equals(t.FolderPath, info.FolderPath));
        string? root = Settings.Sources.FirstOrDefault(s => s.Id == info.SourceId && s.Enabled)?.RootPath ?? task?.DestinationRoot;
        string galleryToken = info.Token;
        if (galleryToken.Length == 0) galleryToken = Works.Get(info.WorkId)?.Token ?? string.Empty;
        if (galleryToken.Length == 0) galleryToken = task?.Token ?? string.Empty;
        if (info.Gid is not > 0 || info.HasGidConflict || info.HasNaturalPageNames || string.IsNullOrEmpty(root) || string.IsNullOrEmpty(galleryToken))
            throw new IOException("此副本缺少可用的原画廊链接或原始页号，只能重新载入本地文件。");
        var details = new EhGalleryDetails { Gid = info.Gid.Value, Token = galleryToken, Pages = info.ExpectedPages ?? 0, Title = info.Title };
        await Downloads.RedownloadPageAsync(details, page.Number, info.FolderPath, root, page.Primary?.FilePath, token, progress);
        var source = Settings.Sources.FirstOrDefault(s => s.Id == info.SourceId && s.Enabled)?.Clone();
        if (source is null) return;
        bool qualify = Settings.ActiveSources.Count > 1;
        var copy = await Task.Run(() => Scanner.Scan(source.RootPath, null, true, null, token, source.Id,
            source.DisplayName, qualify, info.FolderPath, validatePages: true).Galleries.Single(), token);
        copy.StableId = info.StableId;
        // 无头信息的旧下载也继续保留已知尾页，不能因只下载一页而丢失剩余缺页。
        if (info.ExpectedPages is > 0 && copy.ExpectedPages != info.ExpectedPages)
        {
            var pages = await Task.Run(() => PageFileParser.Scan(info.FolderPath,
                new SpiderHeader { HasPages = true, Pages = info.ExpectedPages.Value }, out _, validateContainers: true), token);
            copy.ExpectedPages = info.ExpectedPages;
            copy.IsIncomplete = pages.IsIncomplete;
            copy.MissingRanges = pages.MissingRanges;
        }
        await Dispatcher.UIThread.InvokeAsync(() => LocalCopyChecked?.Invoke(this, copy));
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

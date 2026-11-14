using EhViewerForPc.Core.Download;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Services;

public sealed partial class AppServices
{
    public bool CanContinueLocalDownload(GalleryInfo info) => info.IsIncomplete && info.Gid is > 0 && !info.HasGidConflict &&
        Settings.Sources.Any(source => source.Id == info.SourceId && source.Enabled);

    /// <summary>由明确的用户操作授权为该副本补页；捕获路径后交给既有下载服务，不修改全局下载根目录。</summary>
    public async Task<DownloadTask> ContinueLocalDownloadAsync(GalleryInfo info, CancellationToken token)
    {
        if (!CanContinueLocalDownload(info)) throw new InvalidOperationException("此副本无法继续下载，请检查数据源和画廊链接。");
        var source = Settings.Sources.First(s => s.Id == info.SourceId && s.Enabled);
        string root = source.RootPath, folder = info.FolderPath;
        long gid = info.Gid!.Value;
        string galleryToken = info.Token.Length > 0 ? info.Token : Works.Get(info.WorkId)?.Token ?? string.Empty;
        if (galleryToken.Length == 0) throw new InvalidOperationException("本地副本缺少画廊 token，请先补充原画廊链接。");
        if (!PathIdentity.IsDescendantOf(folder, root)) throw new IOException("副本路径已变化，请重新扫描本地库。");
        token.ThrowIfCancellationRequested();
        var details = new EhGalleryDetails { Gid = gid, Token = galleryToken, Title = info.Title, Pages = info.ExpectedPages ?? 0 };
        // 点击已授权入队；关闭详情页只停止页面观察，不撤销已提交给下载服务的用户操作。
        return await Task.Run(() =>
        {
            return Downloads.EnqueueRepair(details, folder, root);
        });
    }
}

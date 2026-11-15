using Avalonia.Threading;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Services;

public sealed partial class AppServices
{
    private readonly Action<string> _recycleDirectory;
    internal event Func<CancellationToken, Task>? LocalDeletionPreparing;
    public event EventHandler<string>? LocalCopyDeleted;

    public async Task DeleteLocalCopyAsync(GalleryInfo copy, CancellationToken token)
    {
        var source = Settings.Sources.FirstOrDefault(s => s.Id == copy.SourceId)?.Clone()
            ?? throw new IOException("副本数据源已移除，请重新打开本地库。");
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(copy.FolderPath));
        if (!PathIdentity.IsDescendantOf(folder, source.RootPath) ||
            Settings.Sources.Any(s => PathIdentity.Comparer.Equals(s.RootPath, folder) || PathIdentity.IsDescendantOf(s.RootPath, folder)) ||
            PathIdentity.Comparer.Equals(Paths.BaseDirectory, folder) || PathIdentity.IsDescendantOf(Paths.BaseDirectory, folder))
            throw new IOException("只能删除数据源中的单个漫画目录，不能删除数据源或应用数据目录。");
        var sources = Settings.Sources.Select(s => s.Clone()).ToArray();
        if (LocalDeletionPreparing is { } preparing)
            foreach (Func<CancellationToken, Task> handler in preparing.GetInvocationList()) await handler(token);
        await Downloads.DeleteLocalCopyAsync(folder, source.RootPath, path =>
        {
            _recycleDirectory(path);
            if (Directory.Exists(path)) throw new IOException("副本仍存在，回收未完成。");
            // 与文件操作一起收尾，退出等待包含缓存写入；保留作品级收藏与阅读进度。
            foreach (var entry in sources)
            {
                if (!PathIdentity.IsDescendantOf(path, entry.RootPath)) continue;
                try { CacheStore.RemoveFolder(entry.Id, entry.RootPath, path); }
                catch (Exception error) { Log.Warn("Deleted local copy cache cleanup failed", error); }
            }
        }, token).ConfigureAwait(false);
        if (!_disposed) await Dispatcher.UIThread.InvokeAsync(() => LocalCopyDeleted?.Invoke(this, folder));
    }
}

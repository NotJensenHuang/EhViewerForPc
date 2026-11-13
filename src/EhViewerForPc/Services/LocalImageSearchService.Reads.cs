namespace EhViewerForPc.Services;

public sealed partial class LocalImageSearchService
{
    /// <summary>只跟踪后台文件读取本身；释放时不等待依赖 UI 线程的调用方 continuation。</summary>
    private Task<T> RunReadAsync<T>(Func<CancellationToken, T> read, CancellationToken token)
    {
        lock (_readGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _pendingReads++;
        }
        // 不把取消令牌交给调度器，确保尚未开始的读取也进入 finally 归还计数。
        return Task.Run(() =>
        {
            try
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                return read(cancellation.Token);
            }
            finally
            {
                lock (_readGate)
                    if (--_pendingReads == 0) Monitor.PulseAll(_readGate);
            }
        });
    }
}

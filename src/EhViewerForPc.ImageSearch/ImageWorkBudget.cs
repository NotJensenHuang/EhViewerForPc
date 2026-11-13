namespace EhViewerForPc.ImageSearch;

/// <summary>有界像素工作区；等待者可取消，归还租约会唤醒所有可继续的工作。</summary>
public sealed class ImageWorkBudget
{
    private readonly object _gate = new();
    private TaskCompletionSource _changed = NewSignal();
    private long _used, _peak;
    public ImageWorkBudget(long limitBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limitBytes);
        LimitBytes = limitBytes;
    }
    public long LimitBytes { get; }
    public long UsedBytes { get { lock (_gate) return _used; } }
    public long PeakBytes { get { lock (_gate) return _peak; } }

    public async ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken token)
    {
        if (bytes < 0 || bytes > LimitBytes) throw new InvalidDataException("InputTooLarge: extraction exceeds the memory budget.");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task changed;
            lock (_gate)
            {
                if (bytes <= LimitBytes - _used)
                {
                    var lease = new Lease(this, bytes);
                    _used += bytes; _peak = Math.Max(_peak, _used);
                    return lease;
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(token).ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Release(long bytes)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            _used -= bytes;
            changed = _changed; _changed = NewSignal();
        }
        changed.TrySetResult();
    }
    private sealed class Lease(ImageWorkBudget owner, long bytes) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(bytes); }
    }
}

namespace EhViewerForPc.Core.Storage;

/// <summary>Coalesces disk writes on one worker. Dispose drains the final snapshots.</summary>
public sealed class DeferredSaveQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Action> _pending = new(StringComparer.Ordinal);
    private Task? _worker;
    private bool _disposed;

    public void Schedule(string key, Action write)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DeferredSaveQueue));
            _pending[key] = write;
            _worker ??= Task.Run(DrainAsync);
        }
    }

    private async Task DrainAsync()
    {
        await Task.Delay(120).ConfigureAwait(false);
        while (true)
        {
            Action[] batch;
            lock (_gate)
            {
                if (_pending.Count == 0) { _worker = null; return; }
                batch = _pending.Values.ToArray();
                _pending.Clear();
            }
            foreach (Action write in batch) write();
        }
    }

    public void Dispose()
    {
        Task? worker;
        lock (_gate) { _disposed = true; worker = _worker; }
        worker?.GetAwaiter().GetResult();
    }
}

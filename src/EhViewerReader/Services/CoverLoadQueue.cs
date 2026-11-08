using System.Collections.Concurrent;
using System.Threading.Channels;

namespace EhViewerReader.Services;

/// <summary>
/// Shared decoded cover pixels: raw BGRA (premultiplied) bytes. Each caller wraps
/// them into its own WriteableBitmap so a recycled container can dispose its
/// bitmap without pulling the rug from under another visible card.
/// </summary>
public sealed record CoverPixels(int Width, int Height, byte[] Pixels, string SourcePath);

/// <summary>
/// Bounded cover load queue modelled on WinUIMusicPlayer's CoverLoadQueue:
/// a bounded Channel with dedicated BelowNormal worker threads, request
/// de-duplication per cache key, and waiter-count-driven cancellation.
///
/// Why not "Task.Run per visible card": scrolling a 6,000-item grid used to
/// spawn one decode task per realized card at normal thread priority. Here the
/// backlog is capped at the channel capacity, decode threads yield to the UI
/// thread, identical requests share one decode, and a card scrolled out of view
/// cancels its decode as soon as no other waiter needs it.
/// </summary>
public sealed class CoverLoadQueue : IDisposable
{
    public const int DefaultWorkerCount = 3;
    public const int ChannelCapacity = 64;

    private readonly Channel<Job> _channel = Channel.CreateBounded<Job>(new BoundedChannelOptions(ChannelCapacity)
    {
        SingleReader = false,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
    });

    private readonly ConcurrentDictionary<string, Job> _pending = new(StringComparer.Ordinal);
    private readonly List<Thread> _workers = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _initLock = new();
    private readonly LogService _log;
    private int _initialized;

    public CoverLoadQueue(LogService log)
    {
        _log = log;
    }

    private sealed class Job(
        string cacheKey,
        Func<CancellationToken, Task<CoverPixels?>> work)
    {
        private readonly CancellationTokenSource _workCts = new();
        private int _waiters;
        private int _completed;
        private int _disposed;

        public string CacheKey { get; } = cacheKey;
        public Func<CancellationToken, Task<CoverPixels?>> Work { get; } = work;
        public TaskCompletionSource<CoverPixels?> Tcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token => _workCts.Token;
        public int WaiterCount => Volatile.Read(ref _waiters);

        public void AddWaiter() => Interlocked.Increment(ref _waiters);

        public void ReleaseWaiter()
        {
            // Every consumer left (e.g. the card scrolled out of view) before the
            // decode finished - nothing needs the result anymore, so stop the work.
            if (Interlocked.Decrement(ref _waiters) == 0 && Volatile.Read(ref _completed) == 0)
            {
                try { _workCts.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            DisposeIfComplete();
        }

        public void Complete()
        {
            Volatile.Write(ref _completed, 1);
            DisposeIfComplete();
        }

        public void Abandon()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _workCts.Cancel(); }
            finally { _workCts.Dispose(); }
        }

        private void DisposeIfComplete()
        {
            if (Volatile.Read(ref _completed) != 0 && Volatile.Read(ref _waiters) == 0 &&
                Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _workCts.Dispose();
            }
        }
    }

    /// <summary>
    /// Joins or starts the load for <paramref name="cacheKey"/>. Concurrent
    /// callers with the same key share one decode; each receives the shared
    /// <see cref="CoverPixels"/> once the work completes.
    /// </summary>
    public async Task<CoverPixels?> EnqueueAsync(
        string cacheKey,
        Func<CancellationToken, Task<CoverPixels?>> work,
        CancellationToken token)
    {
        EnsureInitialized();
        Job job;
        bool isNew = false;
        while (true)
        {
            if (_pending.TryGetValue(cacheKey, out Job? existing) &&
                (!existing.Tcs.Task.IsCompleted || existing.Tcs.Task.Status == TaskStatus.RanToCompletion))
            {
                job = existing;
                break;
            }

            if (existing is not null)
            {
                _pending.TryRemove(cacheKey, out _);
            }

            var candidate = new Job(cacheKey, work);
            if (!_pending.TryAdd(cacheKey, candidate))
            {
                candidate.Abandon();
                continue;
            }

            job = candidate;
            isNew = true;
            break;
        }

        job.AddWaiter();
        bool releasedHere = false;
        try
        {
            if (isNew)
            {
                try
                {
                    await _channel.Writer.WriteAsync(job, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // This caller gave up while waiting for queue space. If other
                    // consumers still need the result, keep the shared request
                    // alive and finish enqueueing it without the caller's token.
                    if (job.WaiterCount > 1)
                    {
                        job.ReleaseWaiter();
                        releasedHere = true;
                        try
                        {
                            await _channel.Writer.WriteAsync(job, job.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
                        {
                            Remove(job);
                            job.Tcs.TrySetCanceled();
                            job.Complete();
                        }

                        throw;
                    }

                    Remove(job);
                    job.Tcs.TrySetCanceled(token);
                    job.Complete();
                    throw;
                }
                catch (ChannelClosedException)
                {
                    Remove(job);
                    job.Tcs.TrySetCanceled();
                    job.Complete();
                    return null;
                }
            }

            CoverPixels? pixels = await job.Tcs.Task.WaitAsync(token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return null;
            return pixels;
        }
        finally
        {
            if (!releasedHere)
            {
                job.ReleaseWaiter();
            }
        }
    }

    private void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized) != 0) return;
        lock (_initLock)
        {
            if (_initialized != 0) return;
            for (int i = 0; i < DefaultWorkerCount; i++)
            {
                var thread = new Thread(WorkerLoop)
                {
                    Name = $"CoverLoader#{i}",
                    IsBackground = true,
                    // Below-normal keeps decode work from competing with the UI
                    // thread for CPU - the single most effective anti-jank measure
                    // borrowed from the reference implementation.
                    Priority = ThreadPriority.BelowNormal,
                };
                _workers.Add(thread);
                thread.Start();
            }

            Volatile.Write(ref _initialized, 1);
        }
    }

    private void WorkerLoop()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            Job job;
            try
            {
                job = _channel.Reader.ReadAsync(_shutdown.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
            {
                return;
            }

            try
            {
                CoverPixels? pixels = job.Work(job.Token).GetAwaiter().GetResult();
                job.Tcs.TrySetResult(pixels);
            }
            catch (OperationCanceledException)
            {
                job.Tcs.TrySetCanceled();
            }
            catch (Exception ex)
            {
                // Soft failure: the card shows "无法加载" instead of crashing the queue.
                _log.Warn("Cover load failed (" + job.CacheKey + "): " + ex.Message, ex);
                job.Tcs.TrySetResult(null);
            }
            finally
            {
                Remove(job);
                job.Complete();
            }
        }
    }

    private void Remove(Job job) => _pending.TryRemove(job.CacheKey, out _);

    public void Dispose()
    {
        if (Volatile.Read(ref _initialized) == 0) return;
        _shutdown.Cancel();
        _channel.Writer.TryComplete();
        while (_channel.Reader.TryRead(out Job? job))
        {
            Remove(job);
            job.Tcs.TrySetCanceled();
            job.Complete();
        }

        foreach (Thread worker in _workers)
        {
            try { worker.Join(TimeSpan.FromSeconds(3)); }
            catch { }
        }

        _workers.Clear();
        _shutdown.Dispose();
    }
}

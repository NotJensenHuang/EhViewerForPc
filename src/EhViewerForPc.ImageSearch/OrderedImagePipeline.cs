using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace EhViewerForPc.ImageSearch;

/// <summary>以 admission 窗口约束乱序结果数量，单一消费方按输入顺序提交。
/// 输入与输出只持有托管数据；文件、连接和缓冲租约由 worker 在返回结果前收尾。</summary>
public static class OrderedImagePipeline
{
    public static Task RunAsync<TInput, TOutput, TWorker>(IEnumerable<TInput> source,
        Func<TWorker> createWorker, Func<TWorker, TInput, CancellationToken, ValueTask<TOutput>> process,
        Action<TOutput> commit, int workers, int capacity, CancellationToken token) where TWorker : IDisposable
        => RunAsync(EnumerateAsync(source, token), createWorker, process, commit, workers, capacity, token);

    private static async IAsyncEnumerable<T> EnumerateAsync<T>(IEnumerable<T> source, [EnumeratorCancellation] CancellationToken token)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var value in source) { token.ThrowIfCancellationRequested(); yield return value; }
    }

    public static async Task RunAsync<TInput, TOutput, TWorker>(IAsyncEnumerable<TInput> source,
        Func<TWorker> createWorker, Func<TWorker, TInput, CancellationToken, ValueTask<TOutput>> process,
        Action<TOutput> commit, int workers, int capacity, CancellationToken token) where TWorker : IDisposable
    {
        if (workers is < 1 or > IndexBuildOptions.MaximumWorkers || capacity < workers || capacity > IndexBuildOptions.MaximumWorkers * 4)
            throw new ArgumentOutOfRangeException(nameof(workers));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ct = cancellation.Token;
        using var admission = new SemaphoreSlim(capacity, capacity);
        var inputs = Channel.CreateBounded<(long Sequence, TInput Value)>(new BoundedChannelOptions(capacity) { SingleWriter = true });
        var outputs = Channel.CreateBounded<(long Sequence, TOutput Value)>(new BoundedChannelOptions(capacity) { SingleReader = true });
        Exception? failure = null;
        void Fail(Exception error)
        {
            if (error is not OperationCanceledException) Interlocked.CompareExchange(ref failure, error, null);
            cancellation.Cancel();
        }
        Task producer = Task.Run(async () =>
        {
            try
            {
                long sequence = 0;
                await foreach (var value in source.WithCancellation(ct).ConfigureAwait(false))
                {
                    await admission.WaitAsync(ct).ConfigureAwait(false);
                    await inputs.Writer.WriteAsync((sequence++, value), ct).ConfigureAwait(false);
                }
                inputs.Writer.TryComplete();
            }
            catch (Exception error) { inputs.Writer.TryComplete(error); Fail(error); throw; }
        });
        Task[] tasks = new Task[workers];
        for (int i = 0; i < tasks.Length; i++)
            tasks[i] = Task.Run(async () =>
            {
                try
                {
                    using var worker = createWorker();
                    await foreach (var item in inputs.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        var result = await process(worker, item.Value, ct).ConfigureAwait(false);
                        await outputs.Writer.WriteAsync((item.Sequence, result), ct).ConfigureAwait(false);
                    }
                }
                catch (Exception error) { Fail(error); throw; }
            });
        Task completion = CompleteWorkersAsync();
        async Task CompleteWorkersAsync()
        {
            try { await Task.WhenAll(tasks).ConfigureAwait(false); outputs.Writer.TryComplete(); }
            catch (Exception error) { outputs.Writer.TryComplete(error); throw; }
        }
        Exception? primary = null;
        var pending = new Dictionary<long, TOutput>();
        try
        {
            long expected = 0;
            await foreach (var item in outputs.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                pending.Add(item.Sequence, item.Value);
                while (pending.Remove(expected, out var result))
                {
                    ct.ThrowIfCancellationRequested();
                    commit(result);
                    expected++;
                    admission.Release();
                }
            }
            await producer.ConfigureAwait(false);
            await completion.ConfigureAwait(false);
        }
        catch (Exception error) { primary = error; }
        finally
        {
            cancellation.Cancel(); inputs.Writer.TryComplete(); outputs.Writer.TryComplete();
            try { await Task.WhenAll(producer, completion).ConfigureAwait(false); }
            catch { /* 观察所有后台任务，随后重抛原始失败。 */ }
            pending.Clear();
            while (outputs.Reader.TryRead(out _)) { }
        }
        if (primary is OperationCanceledException && !token.IsCancellationRequested && failure is not null) primary = failure;
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }
}

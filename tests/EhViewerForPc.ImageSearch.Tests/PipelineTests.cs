namespace EhViewerForPc.ImageSearch.Tests;

public sealed class PipelineTests
{
    [Fact]
    public async Task SlowFirstItemKeepsAdmissionBoundedAndCommitsInStableOrder()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saturated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int produced = 0, finished = 0, created = 0, disposed = 0;
        var committed = new List<int>();
        IEnumerable<int> Source()
        {
            for (int i = 0; i < 50; i++) { Interlocked.Increment(ref produced); yield return i; }
        }
        async ValueTask<int> Process(Worker worker, int value, CancellationToken token)
        {
            if (value == 0) await release.Task.WaitAsync(token);
            if (Interlocked.Increment(ref finished) == 5) saturated.TrySetResult();
            return value;
        }
        Task pipeline = OrderedImagePipeline.RunAsync(Source(), () => { Interlocked.Increment(ref created); return new Worker(() => Interlocked.Increment(ref disposed)); },
            Process, value => committed.Add(value), 3, 6, CancellationToken.None);
        await saturated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(committed);
        Assert.InRange(Volatile.Read(ref produced), 6, 7);
        release.TrySetResult();
        await pipeline.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Enumerable.Range(0, 50), committed);
        Assert.Equal(3, created); Assert.Equal(created, disposed);
    }

    [Fact]
    public async Task WorkerFailureCancelsAllSiblingsAndDisposesContexts()
    {
        int created = 0, disposed = 0;
        await Assert.ThrowsAsync<IOException>(() => OrderedImagePipeline.RunAsync(Enumerable.Range(0, 30),
            () => { Interlocked.Increment(ref created); return new Worker(() => Interlocked.Increment(ref disposed)); },
            (_, value, _) => value == 1 ? ValueTask.FromException<int>(new IOException("fixture-failure")) : ValueTask.FromResult(value),
            _ => { }, 3, 6, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(created, disposed);
    }

    [Fact]
    public async Task CancellationDrainsWorkersAndWeightedReservations()
    {
        var budget = new ImageWorkBudget(10);
        using var cancellation = new CancellationTokenSource();
        using var first = await budget.ReserveAsync(8, CancellationToken.None);
        var waiting = budget.ReserveAsync(6, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(8, budget.UsedBytes);
        first.Dispose(); first.Dispose();
        Assert.Equal(0, budget.UsedBytes);
        Assert.InRange(budget.PeakBytes, 0, budget.LimitBytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => budget.ReserveAsync(11, CancellationToken.None).AsTask());
    }

    private sealed class Worker(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}

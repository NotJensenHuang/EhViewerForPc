namespace EhViewerForPc.Core.Download;

public sealed partial class DownloadManager
{
    private async Task SampleSpeedAsync(DownloadTask task, DownloadTransferMeter meter, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                task.BytesPerSecond = meter.Sample();
                RaiseTask(task);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}

using EhViewerForPc.Core.Download;
using EhViewerForPc.Core.Imaging;

namespace EhViewerForPc.Core.Tests;

public sealed class DownloadTransferMeterTests
{
    [Fact]
    public void SpeedAggregatesParallelPagesIgnoresCacheAndDecaysAfterTwoSeconds()
    {
        var clock = new ManualClock(); var meter = new DownloadTransferMeter(clock);
        var first = meter.ObservePage(); var second = meter.ObservePage();
        first.Report(new(ImageLoadPhase.Downloading, 1024));
        first.Report(new(ImageLoadPhase.Downloading, 2048));
        first.Report(new(ImageLoadPhase.Downloading, 2048));
        second.Report(new(ImageLoadPhase.Downloading, 1024));
        meter.ObservePage().Report(ImageLoadStatus.Decoding(999999));
        clock.Advance(); Assert.Equal(6144, meter.Sample());
        for (int i = 0; i < 4; i++) { clock.Advance(); meter.Sample(); }
        Assert.Equal(0, meter.Sample());
    }

    [Fact]
    public void ConcurrentPageCallbacksCountEveryReportedNetworkByteOnce()
    {
        var clock = new ManualClock(); var meter = new DownloadTransferMeter(clock);
        Parallel.For(0, 32, _ =>
        {
            var page = meter.ObservePage();
            for (int i = 1; i <= 10; i++) page.Report(new(ImageLoadPhase.Downloading, i * 1024));
        });
        clock.Advance(); Assert.Equal(32 * 10 * 1024 * 2, meter.Sample());
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public void Advance() => _timestamp += 500;
    }
}

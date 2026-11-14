using EhViewerForPc.Core.Imaging;

namespace EhViewerForPc.Core.Download;

/// <summary>任务独占的两秒滑动窗口；并行页只累加实际网络字节，缓存命中不计入。</summary>
internal sealed class DownloadTransferMeter
{
    private readonly TimeProvider _clock;
    private readonly (long Timestamp, long Bytes)[] _samples = new (long, long)[5];
    private int _next = 1, _count = 1;
    private long _received;

    internal DownloadTransferMeter(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _samples[0] = (_clock.GetTimestamp(), 0);
    }

    internal IProgress<ImageLoadStatus> ObservePage() => new PageProgress(this);

    internal double Sample()
    {
        long now = _clock.GetTimestamp(), bytes = Interlocked.Read(ref _received);
        _samples[_next] = (now, bytes);
        _next = (_next + 1) % _samples.Length;
        _count = Math.Min(_samples.Length, _count + 1);
        var first = _samples[_count == _samples.Length ? _next : 0];
        double elapsed = _clock.GetElapsedTime(first.Timestamp, now).TotalSeconds;
        return elapsed <= 0 ? 0 : Math.Max(0, (bytes - first.Bytes) / elapsed);
    }

    private sealed class PageProgress(DownloadTransferMeter owner) : IProgress<ImageLoadStatus>
    {
        private long _previous;
        public void Report(ImageLoadStatus value)
        {
            if (value.Phase != ImageLoadPhase.Downloading || value.ReceivedBytes <= _previous) return;
            Interlocked.Add(ref owner._received, value.ReceivedBytes - _previous);
            _previous = value.ReceivedBytes;
        }
    }
}

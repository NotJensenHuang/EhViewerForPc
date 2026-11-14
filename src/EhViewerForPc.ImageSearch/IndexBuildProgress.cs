using System.Diagnostics;

namespace EhViewerForPc.ImageSearch;

public enum IndexBuildStage { Assigning, WritingSegment, CheckingSegment, CheckingExistingIndex, ReadingSegments, Merging, HashingIndex, ValidatingIndex, Publishing, Complete }

/// <summary>运行时阶段进度；各阶段的特征、分区和字节分别计数，不虚构整个建库的百分比。</summary>
public readonly record struct IndexBuildProgress(IndexBuildStage Stage, long Completed, long Total, int Part = 0, int Parts = 0)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Completed / Total, 0, 1) : 0;
}

internal sealed class IndexBuildProgressReporter(IProgress<IndexBuildProgress>? observer)
{
    private readonly object _gate = new();
    private long _lastReport, _assigned, _total;
    private IndexBuildProgress? _last;
    internal bool Enabled => observer is not null;

    internal void StartAssignment(long completed, long total)
    {
        _assigned = completed;
        _total = total;
        ReportAssignment();
    }

    internal void AdvanceAssignment(int count)
    {
        if (observer is null) return;
        Interlocked.Add(ref _assigned, count);
        lock (_gate) ReportLocked(new(IndexBuildStage.Assigning, Interlocked.Read(ref _assigned), _total), false);
    }

    internal void ReportAssignment() => Report(new(IndexBuildStage.Assigning, _assigned, _total), true);

    internal void Report(IndexBuildProgress value, bool force = false)
    {
        if (observer is null) return;
        lock (_gate) ReportLocked(value, force);
    }

    private void ReportLocked(IndexBuildProgress value, bool force)
    {
        long now = Stopwatch.GetTimestamp();
        force |= _last?.Stage != value.Stage || _last?.Part != value.Part || value.Completed == value.Total;
        if (!force && now - _lastReport < Stopwatch.Frequency / 10) return;
        _last = value;
        _lastReport = now;
        observer!.Report(value);
    }
}

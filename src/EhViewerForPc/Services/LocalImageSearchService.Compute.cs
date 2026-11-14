using EhViewerForPc.ImageSearch;

namespace EhViewerForPc.Services;

public sealed partial class LocalImageSearchService
{
    private const long GpuHostReservation = 32L * 1024 * 1024;
    private readonly IImageComputeAccelerator? _gpu = ImageComputeAccelerators.CreateDefault();
    private IImageComputeAccelerator? _jobAccelerator;
    private IDisposable? _gpuReservation;
    private long _gpuScoreStart, _gpuAssignmentStart;
    private readonly SemaphoreSlim _sourceIo = new(1, 1);

    private void PrepareComputeJob(IndexBuildOptions options, CancellationToken token)
    {
        if (!options.UseGpu || _gpu is null || !ImageMemoryBudget.TryReserveIndex(GpuHostReservation, out _gpuReservation)) return;
        token.ThrowIfCancellationRequested();
        if (!_gpu.TryInitialize())
        {
            _services.Log.Warn("GPU compute unavailable; using CPU.");
            _gpuReservation?.Dispose();
            _gpuReservation = null;
            return;
        }
        _jobAccelerator = _gpu;
        _gpuScoreStart = _gpu.ScoreDispatches;
        _gpuAssignmentStart = _gpu.AssignmentDispatches;
    }

    private void FinishComputeJob()
    {
        try
        {
            if (_jobAccelerator is { } accelerator && BuildStatistics is { } statistics)
                Volatile.Write(ref _statistics, statistics with { Accelerator = accelerator.DeviceName,
                    GpuScoreDispatches = accelerator.ScoreDispatches - _gpuScoreStart,
                    GpuAssignmentDispatches = accelerator.AssignmentDispatches - _gpuAssignmentStart });
        }
        finally
        {
            try { _gpu?.ReleaseWorkingSet(); }
            finally
            {
                _jobAccelerator = null;
                _gpuReservation?.Dispose();
                _gpuReservation = null;
            }
        }
    }
}

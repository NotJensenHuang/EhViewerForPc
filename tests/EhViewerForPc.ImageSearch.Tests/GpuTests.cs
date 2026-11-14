using System.Runtime.Versioning;

namespace EhViewerForPc.ImageSearch.Tests;

[SupportedOSPlatform("windows")]
public sealed class GpuTests
{
    [GpuFact]
    public void HardwareFastPreservesEveryOrbFeatureAcrossCellBoundariesAndThresholds()
    {
        using var gpu = new ComputeSharpAccelerator();
        Assert.True(gpu.TryInitialize(), gpu.FailureReason ?? "No hardware compute adapter");
        foreach (var size in new[] { (64, 96), (127, 181), (320, 448), (256, 96), (96, 640) })
        foreach (int seed in new[] { 42, 75, 106 })
        {
            var image = SyntheticImages.Create(seed, size.Item1, size.Item2);
            Assert.Equal(new OrbExtractor().Extract(image), new OrbExtractor(accelerator: gpu).Extract(image));
        }
        foreach (int seed in new[] { 1, 2, 3 })
        {
            byte[] pixels = new byte[129 * 177]; new Random(seed).NextBytes(pixels);
            var image = new GrayImage(129, 177, pixels);
            var profile = new FeatureProfile { FastThreshold = 40, MinFastThreshold = 3 };
            Assert.Equal(new OrbExtractor(profile).Extract(image), new OrbExtractor(profile, accelerator: gpu).Extract(image));
        }
        var blank = new GrayImage(127, 181, new byte[127 * 181]);
        Assert.Empty(new OrbExtractor(accelerator: gpu).Extract(blank));
        Assert.True(gpu.ScoreDispatches > 0, gpu.FailureReason);
        Assert.True(gpu.IsAvailable, gpu.FailureReason);
        gpu.ReleaseWorkingSet();
        Assert.Equal(new OrbExtractor().Extract(SyntheticImages.Create(42)), new OrbExtractor(accelerator: gpu).Extract(SyntheticImages.Create(42)));
    }

    [GpuFact]
    public void HardwareAssignmentsAndTrainingRetainTieBreakAndDeterministicCenters()
    {
        using var gpu = new ComputeSharpAccelerator();
        Assert.True(gpu.TryInitialize(), gpu.FailureReason ?? "No hardware compute adapter");
        var data = AlgorithmTests.Descriptors(8193, 42);
        var centers = AlgorithmTests.Descriptors(33, 52); centers[1] = centers[0];
        data[0] = centers[0];
        int[] ids = new int[data.Length];
        Assert.True(gpu.TryAssign(data, centers, ids, out long distance, CancellationToken.None), gpu.FailureReason);
        long expectedDistance = 0;
        for (int i = 0; i < data.Length; i++)
        {
            int best = int.MaxValue, index = 0;
            for (int c = 0; c < centers.Length; c++)
            {
                int d = data[i].Distance(centers[c]);
                if (d < best) { best = d; index = c; }
            }
            expectedDistance += best; Assert.Equal(index, ids[i]);
        }
        Assert.Equal(expectedDistance, distance);
        foreach (bool twoLevel in new[] { false, true })
        {
            var cpu = BinaryKModes.Train(data, 256, iterations: 2, twoLevel: twoLevel);
            var accelerated = BinaryKModes.Train(data, 256, iterations: 2, twoLevel: twoLevel, accelerator: gpu);
            Assert.Equal(cpu.Centers, accelerated.Centers); Assert.Equal(cpu.Frequencies, accelerated.Frequencies); Assert.Equal(cpu.Distance, accelerated.Distance);
        }
        Assert.True(gpu.AssignmentDispatches > 0, gpu.FailureReason);
        Assert.True(gpu.IsAvailable, gpu.FailureReason);
    }

    [Fact]
    public void FailedAcceleratorResultsAreDiscardedBeforeCpuFallback()
    {
        using var failed = new FailedAccelerator();
        var image = SyntheticImages.Create(42);
        Assert.Equal(new OrbExtractor().Extract(image), new OrbExtractor(accelerator: failed).Extract(image));
        var data = AlgorithmTests.Descriptors(8193, 42);
        var cpu = BinaryKModes.Train(data, 128, iterations: 2, twoLevel: false);
        var fallback = BinaryKModes.Train(data, 128, iterations: 2, twoLevel: false, accelerator: failed);
        Assert.True(failed.AssignmentCalls > 0);
        Assert.Equal(cpu.Centers, fallback.Centers); Assert.Equal(cpu.Frequencies, fallback.Frequencies); Assert.Equal(cpu.Distance, fallback.Distance);
    }

    [GpuFact]
    public void HardwareDeviceSurvivesDisposalOfAnotherOwner()
    {
        using var first = new ComputeSharpAccelerator();
        using var second = new ComputeSharpAccelerator();
        Assert.True(first.TryInitialize(), first.FailureReason);
        Assert.True(second.TryInitialize(), second.FailureReason);
        first.Dispose(); first.Dispose();
        var image = SyntheticImages.Create(42, 127, 181);
        Assert.True(second.TryGetFastScores(image, 7, new int[image.Pixels.Length], CancellationToken.None), second.FailureReason);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => second.TryGetFastScores(image, 7, new int[image.Pixels.Length], canceled.Token));
        Assert.True(second.TryGetFastScores(image, 7, new int[image.Pixels.Length], CancellationToken.None), second.FailureReason);
    }

    private sealed class GpuFactAttribute : FactAttribute
    {
        public GpuFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("EHVIEWER_GPU_TESTS") != "1") Skip = "Set EHVIEWER_GPU_TESTS=1 for hardware GPU integration checks.";
        }
    }

    private sealed class FailedAccelerator : IImageComputeAccelerator
    {
        public string DeviceName => "failed";
        public bool IsAvailable => false;
        public long ScoreDispatches => 0;
        public long AssignmentDispatches => 0;
        public bool TryInitialize() => false;
        public bool TryGetFastScores(GrayImage image, int threshold, int[] scores, CancellationToken token) { Array.Fill(scores, int.MaxValue); return false; }
        public int AssignmentCalls { get; private set; }
        public bool TryAssign(BinaryDescriptor[] data, BinaryDescriptor[] centers, int[] ids, out long distance, CancellationToken token) { AssignmentCalls++; Array.Fill(ids, int.MaxValue); distance = 0; return false; }
        public void ReleaseWorkingSet() { }
        public void Dispose() { }
    }
}

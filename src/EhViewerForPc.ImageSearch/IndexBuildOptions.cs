namespace EhViewerForPc.ImageSearch;

/// <summary>一次建库的执行配置，不改变提取配置身份或持久化格式。</summary>
public sealed record IndexBuildOptions
{
    public const int MaximumWorkers = 32;
    public const long MaximumWorkingSetBytes = 1024L * 1024 * 1024;
    public static int DefaultWorkers => Math.Clamp(Environment.ProcessorCount - 2, 1, MaximumWorkers);
    public static long MemoryCapacityBytes => GetMemoryCapacity(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    public static long DefaultWorkingSetBytes => Math.Min(MemoryCapacityBytes, Math.Max(128L * 1024 * 1024, DefaultWorkers * 64L * 1024 * 1024));

    public int Workers { get; init; } = DefaultWorkers;
    public int BatchSize { get; init; } = 10;
    public long WorkingSetBytes { get; init; } = DefaultWorkingSetBytes;
    /// <summary>自动尝试兼容 GPU；设备不可用或计算失败时保留原 CPU 路径。</summary>
    public bool UseGpu { get; init; } = true;
    public bool UseSimd { get; init; } = true;

    /// <summary>依据 GC 可用内存上限缩小工作区；未取得快照时保守回退，不把它当作实时空闲内存。</summary>
    public static long GetMemoryCapacity(long availableMemoryBytes) => availableMemoryBytes <= 0
        ? 128L * 1024 * 1024
        : Math.Clamp(availableMemoryBytes / 16, 128L * 1024 * 1024, MaximumWorkingSetBytes);

    public void Validate()
    {
        if (Workers is < 1 or > MaximumWorkers || BatchSize is < 1 or > 10 || WorkingSetBytes is < 16L * 1024 * 1024 or > MaximumWorkingSetBytes)
            throw new ArgumentOutOfRangeException(nameof(IndexBuildOptions));
    }
}

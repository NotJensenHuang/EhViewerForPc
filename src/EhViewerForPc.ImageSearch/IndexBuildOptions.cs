namespace EhViewerForPc.ImageSearch;

/// <summary>一次建库的执行配置，不改变提取配置身份或持久化格式。</summary>
public sealed record IndexBuildOptions
{
    public int Workers { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    public int BatchSize { get; init; } = 10;
    public long WorkingSetBytes { get; init; } = 128L * 1024 * 1024;
    public bool UseSimd { get; init; } = true;
    public void Validate()
    {
        if (Workers is < 1 or > 6 || BatchSize is < 1 or > 10 || WorkingSetBytes is < 16L * 1024 * 1024 or > 256L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(IndexBuildOptions));
    }
}

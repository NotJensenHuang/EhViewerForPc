namespace EhViewerForPc.ImageSearch;

/// <summary>可选的整数计算后端；失败返回 false，调用方完整重做当前计算，不改变提取配置身份。</summary>
public interface IImageComputeAccelerator : IDisposable
{
    string DeviceName { get; }
    bool IsAvailable { get; }
    long ScoreDispatches { get; }
    long AssignmentDispatches { get; }
    bool TryInitialize();
    bool TryGetFastScores(GrayImage image, int threshold, int[] scores, CancellationToken token);
    bool TryAssign(BinaryDescriptor[] data, BinaryDescriptor[] centers, int[] ids, out long distance, CancellationToken token);
    void ReleaseWorkingSet();
}

public static class ImageComputeAccelerators
{
    public static IImageComputeAccelerator? CreateDefault() => OperatingSystem.IsWindows() ? new ComputeSharpAccelerator() : null;
}

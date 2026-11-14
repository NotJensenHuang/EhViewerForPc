using EhViewerForPc.ImageSearch;

namespace EhViewerForPc.Services;

public sealed partial class LocalImageSearchService
{
    private sealed class IndexProgressObserver(Action<IndexBuildProgress> report) : IProgress<IndexBuildProgress>
    {
        public void Report(IndexBuildProgress value) => report(value);
    }

    private static string DescribeIndexProgress(IndexBuildProgress value)
    {
        string phase = value.Stage switch
        {
            IndexBuildStage.Assigning => "正在分配图片特征",
            IndexBuildStage.WritingSegment => $"正在保存索引分段 {value.Part:N0}",
            IndexBuildStage.CheckingSegment => $"正在校验索引分段 {value.Part:N0}",
            IndexBuildStage.CheckingExistingIndex => "正在校验已有图片索引",
            IndexBuildStage.ReadingSegments => $"正在读取索引分段 {value.Part:N0} / {value.Parts:N0}",
            IndexBuildStage.Merging => "正在合并图片索引",
            IndexBuildStage.HashingIndex => "正在计算图片索引校验值",
            IndexBuildStage.ValidatingIndex => "正在校验图片索引",
            IndexBuildStage.Publishing => "正在发布图片索引",
            _ => "图片索引构建完成",
        };
        if (value.Total <= 0 || value.Stage == IndexBuildStage.Complete) return phase;
        string units = value.Stage switch
        {
            IndexBuildStage.Assigning => $"{value.Completed:N0} / {value.Total:N0} 个特征",
            IndexBuildStage.WritingSegment or IndexBuildStage.Merging => $"{value.Completed:N0} / {value.Total:N0} 个分区",
            _ => $"{value.Completed / 1048576d:N1} / {value.Total / 1048576d:N1} MiB",
        };
        return $"{phase} · {units} · {value.Fraction:P0}";
    }
}

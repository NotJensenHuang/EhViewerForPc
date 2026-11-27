using EhViewerForPc.Services;

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public sealed class JMangaDialogueRecoveryTests
{
    private static readonly MangaTranslationModelOption Model = MangaTranslationModelOption.Find(MangaTranslationModelOption.JMangaId)!;

    [Fact]
    public void DistinctPausedColumnsWithRepeatedTranslationRetrySeparatelyInReadingOrder()
    {
        var requests = new List<string>();
        MangaTranslationResponse response = MangaTranslationWorker.BuildResponse(Page("待って・", "だめ…"),
            JMangaTranslator.Provider, source =>
            {
                requests.Add(source);
                return source switch
                {
                    "待って・" => "等等……",
                    "だめ…" => "不行……",
                    _ => "停下……停下……",
                };
            }, Model);

        MangaTranslationRegion region = Assert.Single(response.Regions);
        Assert.Equal(["待って・だめ…", "待って・", "だめ…"], requests);
        Assert.Equal("等等……\n不行……", region.TranslatedText);
        Assert.Equal("停下……停下……", region.RejectedOutput);
        Assert.Equal(MangaTranslationRegionReview.None, region.Review);
        Assert.Equal([2, 1], region.SourceRegionIds);
        Assert.Equal(new MangaOcrLayoutBounds(12, 15, 70, 100), region.BubbleInterior);
    }

    [Fact]
    public void IntentionalRepetitionKeepsOneModelCall()
    {
        int calls = 0;
        MangaTranslationResponse response = MangaTranslationWorker.BuildResponse(Page("待って…", "待って‥"),
            JMangaTranslator.Provider, _ =>
            {
                calls++;
                return "等等……等等……";
            }, Model);

        Assert.Equal(1, calls);
        MangaTranslationRegion region = Assert.Single(response.Regions);
        Assert.Equal("等等……等等……", region.TranslatedText);
        Assert.Equal(MangaTranslationRegionReview.None, region.Review);
    }

    [Fact]
    public void FailedRetryLeavesAmbiguousWholeCandidateForReview()
    {
        MangaTranslationResponse response = MangaTranslationWorker.BuildResponse(Page("待って・", "だめ…"),
            JMangaTranslator.Provider, source => source switch
            {
                "待って・" => "等等……",
                "だめ…" => "だめ…",
                _ => "停下……停下……",
            }, Model);

        MangaTranslationRegion region = Assert.Single(response.Regions);
        Assert.Equal("停下……停下……", region.TranslatedText);
        Assert.Equal(MangaTranslationRegionReview.Duplicate, region.Review);
    }

    [Fact]
    public void AContinuedColumnDoesNotTriggerIndependentTranslation()
    {
        int calls = 0;
        MangaTranslationResponse response = MangaTranslationWorker.BuildResponse(Page("買い物に", "行こう…"),
            JMangaTranslator.Provider, _ =>
            {
                calls++;
                return "去吧……去吧……";
            }, Model);

        Assert.Equal(1, calls);
        Assert.Equal(MangaTranslationRegionReview.None, Assert.Single(response.Regions).Review);
    }

    private static MangaOcrResult Page(string first, string second) =>
        new(100, 120,
            [new(20, 30, 50, 60, first + second)
            {
                SourceRegionIds = [2, 1],
                BubbleInterior = new(12, 15, 70, 100),
            }], MangaOcrOrt.CpuProvider)
        {
            Observations =
            [
                Observation(1, second),
                Observation(2, first),
            ],
        };

    private static MangaOcrObservation Observation(int id, string text) =>
        new(id, [], 1, text, 1, null, text, 1, MangaOcrDirection.Vertical,
            MangaOcrRegionType.Unclassified, false);
}

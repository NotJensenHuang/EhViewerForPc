using System.Text;
using EhViewerForPc.Services;

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public sealed class MangaTranslationSceneProtocolTests
{
    private const string Reader = "183ef2cfa5f343c5b4ed241a75294f41";

    [Fact]
    public void AcceptsLegacyAndExplicitSceneWithoutChangingInputPath()
    {
        Assert.True(MangaTranslationWorker.TryParsePage($"page.png\t{Reader}\t8\t1",
            out string page, out MangaTranslationPageContext context, out bool prior));
        Assert.Equal("page.png", page);
        Assert.Equal(8, context.Page);
        Assert.True(prior);
        Assert.Null(context.Scene);

        string note = Convert.ToBase64String(Encoding.UTF8.GetBytes("  人物在站台等车  "));
        Assert.True(MangaTranslationWorker.TryParsePage($"page.png\t{Reader}\t8\t1\t{note}",
            out page, out context, out prior));
        Assert.Equal("page.png", page);
        Assert.Equal("人物在站台等车", context.Scene);
        Assert.True(prior);
    }

    [Fact]
    public void RejectsInvalidUtf8ControlsAndOversizedScene()
    {
        foreach (string note in new[]
        {
            "not valid base64",
            Convert.ToBase64String([0xff]),
            Convert.ToBase64String(Encoding.UTF8.GetBytes("第一行\n第二行")),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('字', 161))),
        })
        {
            Assert.False(MangaTranslationWorker.TryParsePage($"page.png\t{Reader}\t8\t1\t{note}",
                out _, out _, out _));
        }
    }

    [Fact]
    public void UsesOnlySceneWhenThereIsOneBubbleAndNoPreviousPage()
    {
        var regions = new MangaOcrRegion[] { new(1, 2, 3, 4, "対象"), new(5, 6, 7, 8, "二") };
        (string prompt, MangaTranslationContextSources sources) = MangaTranslationWorker.GetPageContext(
            regions, 0, [], "", true, "罐头没有打开");
        Assert.Equal(MangaTranslationContextSources.Scene, sources);
        Assert.Contains("罐头没有打开", prompt);
        Assert.DoesNotContain("対象", prompt);

        (prompt, sources) = MangaTranslationWorker.GetPageContext(regions, 0, [], "", false, "罐头没有打开");
        Assert.Equal(MangaTranslationContextSources.NearbyDialogue, sources);
        Assert.Contains("二", prompt);
    }

    [Fact]
    public void KeepsNeighboringSourceWhileAvoidingMachineDraftInSceneMode()
    {
        var regions = new MangaOcrRegion[]
        {
            new(1, 2, 3, 4, "次の電車は？"),
            new(5, 6, 7, 8, "対象"),
            new(9, 10, 11, 12, "切符が必要だよ"),
        };
        var reviewed = new MangaTranslationRegion[] { new("次の電車は？", "机器前句草稿", 1, 2, 3, 4) };
        (string prompt, MangaTranslationContextSources sources) = MangaTranslationWorker.GetPageContext(
            regions, 1, reviewed, "駅に向かう", true, " 站内 ");
        Assert.Equal(MangaTranslationContextSources.Scene | MangaTranslationContextSources.NearbyDialogue |
            MangaTranslationContextSources.PreviousPage, sources);
        Assert.Contains("切符が必要だよ", prompt);
        Assert.Contains("次の電車は？", prompt);
        Assert.Contains("駅に向かう", prompt);
        Assert.DoesNotContain("机器前句草稿", prompt);
        Assert.DoesNotContain("対象", prompt);
    }
}

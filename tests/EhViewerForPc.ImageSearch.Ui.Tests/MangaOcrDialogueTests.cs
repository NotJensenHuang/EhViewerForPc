using EhViewerForPc.Services;

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public sealed class MangaOcrDialogueTests
{
    [Theory]
    [InlineData(true, -1)]
    [InlineData(true, 67)]
    [InlineData(true, 71)]
    [InlineData(false, -1)]
    [InlineData(false, 67)]
    [InlineData(false, 71)]
    public void DialogueUsesOneTranslationPerConnectedBubble(bool vertical, int borderAt)
    {
        const int size = 140;
        byte[] pixels = new byte[size * size * 4];
        pixels.AsSpan().Fill(vertical ? (byte)175 : (byte)240);
        for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var first = vertical ? new MangaOcrRegion(68, 20, 20, 65, "おはよう") : new MangaOcrRegion(20, 46, 95, 21, "おはよう");
        var second = vertical ? new MangaOcrRegion(46, 22, 21, 95, "ございます") : new MangaOcrRegion(24, 68, 87, 20, "ございます");
        if (borderAt >= 0)
        {
            for (int offset = 0; offset < size; offset++)
            {
                int index = vertical ? (offset * size + borderAt) * 4 : (borderAt * size + offset) * 4;
                pixels.AsSpan(index, 3).Clear();
            }
        }

        var token = TestContext.Current.CancellationToken;
        IReadOnlyList<MangaOcrRegion> grouped = MangaOcr.ArrangeDialogue([second, first], pixels, size, size, size * 4, token);
        var phrases = new List<string>();
        var response = MangaTranslationWorker.BuildResponse(new MangaOcrResult(size, size, grouped, MangaOcrOrt.Provider), null, text =>
        {
            phrases.Add(text);
            return "早上好";
        });
        if (borderAt >= 0)
        {
            Assert.Equal(2, response.Regions.Length);
            Assert.Equal(new[] { first.Text, second.Text }, phrases);
            return;
        }

        string phrase = vertical ? first.Text + second.Text : first.Text + "\n" + second.Text;
        Assert.Equal(phrase, Assert.Single(phrases));
        var region = Assert.Single(response.Regions);
        Assert.Equal(vertical ? (46, 20, 42, 97) : (20, 46, 95, 42),
            (region.X, region.Y, region.Width, region.Height));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VerticalDialogueDistinguishesRepeatedGlyphStrokesFromABubbleBorder(bool continuousBorder)
    {
        const int size = 180;
        byte[] pixels = new byte[size * size * 4];
        pixels.AsSpan().Fill(255);
        for (int y = 20; y < 130; y++)
        {
            if (continuousBorder || (y - 20) % 33 < 19)
                pixels.AsSpan((y * size + 138) * 4, 3).Clear();
        }

        var columns = new List<MangaOcrRegion>
        {
            new(28, 20, 40, 110, "ならない？"),
            new(60, 20, 40, 130, "聞き入れねば"),
            new(92, 20, 40, 130, "お前の懇願を"),
            new(124, 20, 40, 110, "なぜ俺が")
        };
        var token = TestContext.Current.CancellationToken;
        IReadOnlyList<MangaOcrRegion> grouped = MangaOcr.ArrangeDialogue(columns, pixels, size, size, size * 4, token);
        if (continuousBorder)
        {
            Assert.Equal(new[] { "なぜ俺が", "お前の懇願を聞き入れねばならない？" },
                grouped.Select(static region => region.Text));
            return;
        }

        MangaOcrRegion bubble = Assert.Single(grouped);
        Assert.Equal("なぜ俺がお前の懇願を聞き入れねばならない？", bubble.Text);
        Assert.Equal((28, 20, 136, 130), (bubble.X, bubble.Y, bubble.Width, bubble.Height));
    }
}

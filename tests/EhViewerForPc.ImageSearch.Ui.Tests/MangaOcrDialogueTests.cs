using EhViewerForPc.Services;

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public sealed class MangaOcrDialogueTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleSmallRecognitionMeasuresTheSurroundingBubble(bool open)
    {
        const int size = 180;
        byte[] pixels = new byte[size * size * 4];
        pixels.AsSpan().Fill(224);
        for (int y = 22; y <= 152; y++)
        for (int x = 42; x <= 135; x++)
        {
            if (x == 42 || x == 135 || y == 152 || (y == 22 && !(open && x == 84)))
                pixels.AsSpan((y * size + x) * 4, 3).Clear();
        }

        var glyph = new MangaOcrRegion(80, 72, 7, 15, "待つ") { SourceRegionIds = [1] };
        var result = Assert.Single(MangaOcr.ArrangeDialogue([glyph], pixels, size, size, size * 4,
            TestContext.Current.CancellationToken));
        Assert.Equal((glyph.X, glyph.Y, glyph.Width, glyph.Height), (result.X, result.Y, result.Width, result.Height));
        if (open) Assert.Null(result.BubbleInterior);
        else
        {
            var interior = Assert.IsType<MangaOcrLayoutBounds>(result.BubbleInterior);
            Assert.True(interior.Width >= 80 && interior.Height >= 110);
        }
    }

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
    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(false, false, 3)]
    [InlineData(true, false, 1)]
    [InlineData(true, false, 3)]
    [InlineData(false, true, 1)]
    public void StaggeredVerticalColumnsOnlyJoinWithClosedBubbleEvidence(bool separatingBorder, bool openOutline, int scale)
    {
        int size = 180 * scale;
        byte[] pixels = new byte[size * size * 4];
        pixels.AsSpan().Fill(225);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        for (int y = 18 * scale; y <= 155 * scale; y++)
        for (int x = 45 * scale; x <= 130 * scale; x++)
        {
            if (x == 45 * scale || (x == 130 * scale && !(openOutline && y == 80 * scale)) ||
                y == 18 * scale || y == 155 * scale || (separatingBorder && x == 87 * scale))
                pixels.AsSpan((y * size + x) * 4, 3).Clear();
        }

        var columns = new List<MangaOcrRegion>
        {
            new(58, 35, 25, 104, "三列目") { SourceRegionIds = [1] },
            new(85, 58, 20, 72, "二列目") { SourceRegionIds = [2] },
            new(105, 62, 20, 67, "一列目") { SourceRegionIds = [3] },
            new(132, 55, 20, 58, "ガタン") { SourceRegionIds = [4] },
        };

        if (scale > 1)
        {
            for (int i = 0; i < columns.Count; i++)
            {
                MangaOcrRegion region = columns[i];
                columns[i] = region with
                {
                    X = region.X * scale,
                    Y = region.Y * scale,
                    Width = region.Width * scale,
                    Height = region.Height * scale,
                };
            }
        }

        var grouped = MangaOcr.ArrangeDialogue(columns, pixels, size, size, size * 4, TestContext.Current.CancellationToken);
        Assert.Contains(grouped, item => item.SourceRegionIds.SequenceEqual([4]));
        if (separatingBorder || openOutline)
        {
            Assert.Contains(grouped, item => item.SourceRegionIds.SequenceEqual([1]));
            return;
        }

        MangaOcrRegion bubble = Assert.Single(grouped, item => item.SourceRegionIds.Length == 3);
        Assert.Equal(new[] { 3, 2, 1 }, bubble.SourceRegionIds);
        Assert.Equal("一列目二列目三列目", bubble.Text);
        var interior = Assert.IsType<MangaOcrLayoutBounds>(bubble.BubbleInterior);
        Assert.True(interior.X > 45 * scale && interior.X + interior.Width < 130 * scale);
        Assert.True(interior.Y > 18 * scale && interior.Y + interior.Height < 155 * scale);
    }
    [Theory]
    [InlineData(1, false, 1)]
    [InlineData(2, false, 2)]
    [InlineData(1, true, 2)]
    public void BubbleInstancesPreventCrossBubbleChainsWithoutOverridingInkSeparators(
        int leftBubble, bool separatingInk, int expectedGroups)
    {
        const int size = 160;
        byte[] pixels = new byte[size * size * 4];
        pixels.AsSpan().Fill(255);
        if (separatingInk)
        {
            for (int y = 18; y < 95; y++)
                pixels.AsSpan((y * size + 81) * 4, 3).Clear();
        }

        var columns = new List<MangaOcrRegion>
        {
            new(30, 18, 24, 75, "左列目") { SourceRegionIds = [1], BubbleInstanceId = leftBubble },
            new(56, 18, 24, 75, "中央列") { SourceRegionIds = [2] },
            new(82, 18, 24, 75, "右列目") { SourceRegionIds = [3], BubbleInstanceId = 1 },
        };
        var grouped = MangaOcr.ArrangeDialogue(columns, pixels, size, size, size * 4, TestContext.Current.CancellationToken);
        Assert.Equal(expectedGroups, grouped.Count);
        Assert.Equal(new[] { 1, 2, 3 }, grouped.SelectMany(static region => region.SourceRegionIds).Order().ToArray());
        if (leftBubble == 2)
        {
            Assert.DoesNotContain(grouped, item => item.SourceRegionIds.Contains(1) && item.SourceRegionIds.Contains(3));
            Assert.Contains(grouped, item => item.SourceRegionIds.SequenceEqual([3, 2]));
        }
    }
}

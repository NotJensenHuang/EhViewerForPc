using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EhViewerForPc.Controls;
using EhViewerForPc.Services;
using EhViewerForPc.ViewModels;
using SkiaSharp;

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public sealed class MangaTranslationOverlayTests
{
    [Fact]
    public void TranslationCanOnlyPaintTheSameStaticFileVersion()
    {
        var image = new ReaderTranslationImage("01.webp", 150, DateTime.UnixEpoch);
        var translation = new MangaTranslationOverlay(400, 300, []) { ImageSource = image };

        Assert.True(translation.Matches(image));
        Assert.False(translation.Matches(null));
        Assert.False(translation.Matches(image with { Path = "02.webp" }));
        Assert.False(translation.Matches(image with { Length = 160 }));
        Assert.False(translation.Matches(image with { ModifiedUtc = DateTime.UnixEpoch.AddSeconds(1) }));
        Assert.False(translation.Matches(image with { IsAnimated = true }));
    }

    [AvaloniaFact]
    public void TranslatedChineseInkFollowsZoomAndCoversOnlyTheClippedRegions()
    {
        // 图片相对视口放大并向上、向左移动，第一处译文仅剩左下部可见。
        var destination = new Rect(-160, -70, 800, 600);
        var viewport = new Rect(30, 20, 620, 490);
        var overlay = new MangaTranslationOverlay(400, 300,
        [
            new(new Rect(30, 20, 200, 70), "欢迎来到这里。今天也请多关照。"),
            new(new Rect(260, 45, 31, 180), "欢迎来到这里。今天也请多关照。"),
        ]);

        using var bitmap = new RenderTargetBitmap(new PixelSize(680, 530), new Vector(96, 96));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(Brushes.Navy, new Rect(new Size(680, 530)));
            using var clip = context.PushClip(viewport);
            context.FillRectangle(Brushes.Red, destination);
            overlay.Draw(context, destination, viewport);
        }

        using var encoded = new MemoryStream();
        bitmap.Save(encoded, PngBitmapEncoderOptions.Default);
        encoded.Position = 0;
        using SKBitmap actual = SKBitmap.Decode(encoded);
        Assert.Equal(SKColors.Navy, actual.GetPixel(320, 10));
        Assert.Equal(SKColors.Red, actual.GetPixel(320, 40));
        Assert.Equal(SKColors.White, actual.GetPixel(35, 104));
        Assert.Equal(SKColors.White, actual.GetPixel(415, 372));
        Assert.Equal(SKColors.Red, actual.GetPixel(423, 190));
        Assert.True(CountInk(actual, new PixelRect(45, 25, 248, 80)) > 200);
        Assert.True(CountInk(actual, new PixelRect(363, 130, 56, 140)) > 200);
        Assert.Equal(0, CountInk(actual, new PixelRect(422, 25, 15, 350)));
        Assert.Equal(0, CountInk(actual, new PixelRect(363, 382, 56, 30)));
    }

    [AvaloniaFact]
    public void NarrowLongTranslationExpandsAtFitPageScaleAndReturnsInsideWhenZoomed()
    {
        var source = new Rect(90, 42, 26, 110);
        var image = new Rect(0, 0, 200, 150);
        var overlay = new MangaTranslationOverlay(400, 300,
            [new(source, "这是为了复现长译文被挤进窄竖列后看不清的情况。")]);

        using var bitmap = new RenderTargetBitmap(new PixelSize(200, 150), new Vector(96, 96));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(Brushes.Navy, image);
            context.FillRectangle(Brushes.Red, new Rect(45, 21, 13, 55));
            overlay.Draw(context, image, image);
        }

        using var encoded = new MemoryStream();
        bitmap.Save(encoded, PngBitmapEncoderOptions.Default);
        encoded.Position = 0;
        using SKBitmap actual = SKBitmap.Decode(encoded);
        Assert.True(CountInk(actual, new PixelRect(13, 25, 31, 46)) > 50);
        Assert.Equal(SKColors.Navy, actual.GetPixel(2, 2));

        // 复用同一排版缓存放大，当前显示字号恢复到可读范围时可以重新绘制。
        var enlarged = new Rect(0, 0, 800, 600);
        using SKBitmap zoomed = Render(overlay, enlarged, [new Rect(180, 84, 52, 220)], default);
        Assert.Equal(SKColors.White, zoomed.GetPixel(182, 86));
        Assert.True(CountInk(zoomed, new PixelRect(181, 86, 49, 215)) > 200);
    }

    [AvaloniaTheory]
    [InlineData("zh-CN", "这句话不必挤进原文的一列。")]
    [InlineData("zh-Hant", "這句話不用擠進原文的一列……！")]
    public void ClosedBubbleMakesCaptionReadableWithoutPaintingItsBackground(string language, string right)
    {
        var image = new Rect(0, 0, 330, 190);
        var rightSource = new Rect(261, 61, 22, 64);
        var rightInterior = new Rect(231, 46, 81, 95);
        var leftSource = new Rect(24, 66, 147, 58);
        var overlay = new MangaTranslationOverlay(330, 190,
        [
            new(leftSource, "你在这里等着。我马上回来。"),
            new MangaTranslationOverlayRegion(rightSource, right) { SafeTextBounds = rightInterior },
        ], language);

        using SKBitmap result = Render(overlay, image, [leftSource, rightSource], rightInterior);
        Assert.True(CountInk(result, new PixelRect(26, 68, 142, 53)) > 250);
        int outsideSourceInk = CountInk(result, new PixelRect(235, 50, 24, 85)) +
                               CountInk(result, new PixelRect(284, 50, 25, 85));
        Assert.True(outsideSourceInk > 70, $"源框外墨迹为 {outsideSourceInk} 像素。");
        Assert.True(CountInk(result, new PixelRect(235, 50, 74, 85)) > 300);
        Assert.Equal(SKColors.Pink, result.GetPixel(232, 48));
        Assert.Equal(SKColors.Navy, result.GetPixel(315, 95));
    }

    [AvaloniaFact]
    public void CandidateSpaceIsTrimmedAwayFromAnotherRegionsOriginal()
    {
        var image = new Rect(0, 0, 330, 190);
        var leftSource = new Rect(24, 66, 147, 58);
        var rightSource = new Rect(220, 61, 20, 64);
        var expanded = new Rect(150, 36, 141, 100);
        var overlay = new MangaTranslationOverlay(330, 190,
        [
            new(leftSource, "你在这里等着。我马上回来。"),
            new MangaTranslationOverlayRegion(rightSource, "这句话太长，需要在原文旁边找到余量。")
            {
                SafeTextBounds = expanded,
            },
        ]);

        using SKBitmap result = Render(overlay, image, [leftSource, rightSource], expanded);
        Assert.True(CountInk(result, new PixelRect(26, 68, 142, 53)) > 250);
        Assert.True(CountInk(result, new PixelRect(174, 2, 150, 186)) > 120);
    }

    [AvaloniaFact]
    public void TwoSmallRecognitionsCanShareTheSameBubble()
    {
        var image = new Rect(0, 0, 220, 150);
        var left = new Rect(62, 71, 7, 13);
        var right = new Rect(146, 71, 7, 13);
        var paper = new Rect(29, 33, 159, 88);
        var overlay = new MangaTranslationOverlay(220, 150,
        [
            new MangaTranslationOverlayRegion(left, "左边说话。") { SafeTextBounds = paper },
            new MangaTranslationOverlayRegion(right, "右边回答。") { SafeTextBounds = paper },
        ]);

        using SKBitmap result = Render(overlay, image, [left, right], paper);
        Assert.True(CountInk(result, new PixelRect(31, 34, 71, 87)) > 200);
        Assert.True(CountInk(result, new PixelRect(111, 34, 75, 87)) > 200);
        Assert.Equal(SKColors.Pink, result.GetPixel(30, 34));
        Assert.Equal(SKColors.Navy, result.GetPixel(22, 34));
    }

    [AvaloniaFact]
    public void RightEdgeCaptionsExpandLeftAndKeepNeighboringTranslationVisible()
    {
        var image = new Rect(0, 0, 240, 180);
        var viewport = new Rect(9, 5, 225, 172);
        var overlay = new MangaTranslationOverlay(400, 300,
        [
            new(new Rect(24, 39, 176, 61), "早上好，今天见。"),
            new(new Rect(367, 45, 24, 45), "右侧第一句话要往左排，避免被页缘截断。"),
            new(new Rect(373, 130, 22, 48), "右侧第二句话也需要完整显示。"),
        ]);
        using var bitmap = new RenderTargetBitmap(new PixelSize(240, 180), new Vector(96, 96));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(Brushes.Navy, image);
            overlay.Draw(context, image, viewport);
        }
        using var encoded = new MemoryStream();
        bitmap.Save(encoded, PngBitmapEncoderOptions.Default);
        encoded.Position = 0;
        using SKBitmap result = SKBitmap.Decode(encoded);

        Assert.True(CountInk(result, new PixelRect(16, 23, 100, 38)) > 100);
        Assert.True(CountInk(result, new PixelRect(165, 10, 53, 61)) > 100);
        Assert.True(CountInk(result, new PixelRect(165, 70, 53, 61)) > 100);
        Assert.Equal(SKColors.Navy, result.GetPixel(237, 25));
        Assert.Equal(SKColors.Navy, result.GetPixel(237, 90));
    }

    [AvaloniaFact]
    public void OutwardCaptionKeepsUntranslatedSourceVisible()
    {
        var image = new Rect(0, 0, 220, 150);
        var source = new Rect(175, 54, 11, 35);
        var obstacle = new Rect(147, 49, 22, 43);
        var overlay = new MangaTranslationOverlay(220, 150,
            [new(source, "这段文字需要完整显示。")])
        {
            ProtectedImageBounds = [obstacle],
        };
        using SKBitmap result = Render(overlay, image, [source, obstacle], default);

        Assert.Equal(SKColors.Red, result.GetPixel(157, 70));
        Assert.True(CountInk(result, new PixelRect(175, 35, 42, 96)) > 150);
    }

    [AvaloniaFact]
    public void BalancedBubbleInsetCanFitMoreReadableMultilineText()
    {
        var image = new Rect(0, 0, 300, 220);
        var speech = new MangaTranslationOverlayRegion(new Rect(140, 60, 87, 126), "等、等一下……\n喂……")
        {
            SafeTextBounds = new Rect(109, 71, 142, 112),
        };
        var balanced = speech with { BalancedTextBounds = new Rect(118, 57, 126, 127) };
        using SKBitmap wideLayout = Render(new MangaTranslationOverlay(300, 220, [speech]), image, [], default);
        using SKBitmap balancedLayout = Render(new MangaTranslationOverlay(300, 220, [balanced]), image, [], default);

        var translatedArea = new PixelRect(114, 55, 136, 132);
        Assert.True(CountInk(balancedLayout, translatedArea) > CountInk(wideLayout, translatedArea) * 1.15);
    }

    [AvaloniaFact]
    public void CompactCaptionDoesNotStackSpeechOneCharacterPerLine()
    {
        var image = new Rect(0, 0, 168, 236);
        var overlay = new MangaTranslationOverlay(1200, 1680,
        [
            new MangaTranslationOverlayRegion(new Rect(1040, 560, 75, 122), "等、等一下……\n喂……")
            {
                SafeTextBounds = new Rect(1011, 560, 110, 122),
            },
        ]);

        using SKBitmap result = Render(overlay, image, [], default);

        Assert.True(CountInk(result, new PixelRect(107, 67, 24, 48)) > 35);
        Assert.True(CountInk(result, new PixelRect(135, 67, 23, 48)) > 35);
        Assert.Equal(SKColors.Navy, result.GetPixel(150, 152));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyFullyCoveredReviewBoxesAllowUnrestrictedBubbleSpace(bool duplicate)
    {
        MangaOcrPoint[] owner = Box(140, 60, 32, 100);
        MangaOcrPoint[] reviewed = duplicate ? Box(147, 144, 12, 12) : Box(137, 144, 16, 12);
        var region = new MangaTranslationRegion("source", "output", 140, 60, 32, 100)
        {
            SourceRegionIds = [1],
            BubbleInterior = new(110, 54, 92, 108),
        };
        var response = new MangaTranslationResponse(1, 220, 180, "test", "test", [region])
        {
            RawDetections = [new(1, owner, 0.95f), new(2, reviewed, 0.64f)],
            OcrObservations =
            [
                new(1, owner, 0.95f, "source", 1, null, "source", 1,
                    MangaOcrDirection.Vertical, MangaOcrRegionType.Unclassified, false) { OriginalDetectionIds = [1] },
                new(2, reviewed, 0.64f, "0", 0.05f, null, "0", 0.05f,
                    MangaOcrDirection.Horizontal, MangaOcrRegionType.Unclassified, true) { OriginalDetectionIds = [2] },
            ],
        };
        var coveredObservations = new HashSet<int>();
        var coveredDetections = new HashSet<int>();

        Rect? space = ReaderViewModel.GetOverlaySpace(response, region, coveredObservations, coveredDetections, out _, out var masks);

        if (duplicate)
        {
            Assert.Equal(new Rect(110, 54, 92, 108), space);
            Assert.Equal(new Rect(140, 60, 32, 100), Assert.Single(masks!));
        }
        else Assert.Null(space);
        Assert.Equal(duplicate, coveredObservations.Contains(2));
        Assert.Equal(duplicate, coveredDetections.Contains(2));
    }

    private static MangaOcrPoint[] Box(int x, int y, int width, int height) =>
        [new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height)];

    private static SKBitmap Render(MangaTranslationOverlay overlay, Rect image, Rect[] original, Rect paper)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)image.Width, (int)image.Height), new Vector(96, 96));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(Brushes.Navy, image);
            context.FillRectangle(Brushes.Pink, paper);
            foreach (Rect region in original) context.FillRectangle(Brushes.Red, region);
            overlay.Draw(context, image, image);
        }
        using var encoded = new MemoryStream();
        bitmap.Save(encoded, PngBitmapEncoderOptions.Default);
        encoded.Position = 0;
        return SKBitmap.Decode(encoded);
    }

    [Theory]
    [InlineData("zh-CN", FlowDirection.LeftToRight)]
    [InlineData("zh-Hant", FlowDirection.LeftToRight)]
    [InlineData("en", FlowDirection.LeftToRight)]
    [InlineData("ar", FlowDirection.RightToLeft)]
    [InlineData("he", FlowDirection.RightToLeft)]
    public void ParagraphDirectionFollowsTargetLanguage(string targetLanguage, FlowDirection direction)
    {
        var overlay = new MangaTranslationOverlay(400, 300, [], targetLanguage);
        Assert.Equal(direction, overlay.TextFlowDirection);
    }

    private static int CountInk(SKBitmap image, PixelRect bounds)
    {
        int count = 0;
        for (int y = bounds.Y; y < bounds.Bottom; y++)
        {
            for (int x = bounds.X; x < bounds.Right; x++)
            {
                SKColor pixel = image.GetPixel(x, y);
                if (pixel.Red < 80 && pixel.Green < 80 && pixel.Blue < 80) count++;
            }
        }

        return count;
    }
}

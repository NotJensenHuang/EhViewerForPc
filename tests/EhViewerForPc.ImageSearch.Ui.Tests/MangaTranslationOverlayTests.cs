using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EhViewerForPc.Controls;
using EhViewerForPc.Services;
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
        Assert.Equal(0, CountInk(actual, new PixelRect(363, 25, 56, 80)));
        Assert.Equal(0, CountInk(actual, new PixelRect(363, 295, 56, 78)));
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

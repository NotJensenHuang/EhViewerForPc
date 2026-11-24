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
            new(new Rect(260, 45, 31, 180), "翻译沿着竖直文字从上到下排列。"),
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
        Assert.True(CountInk(actual, new PixelRect(45, 25, 248, 80)) > 200);
        Assert.True(CountInk(actual, new PixelRect(362, 24, 58, 350)) > 200);
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

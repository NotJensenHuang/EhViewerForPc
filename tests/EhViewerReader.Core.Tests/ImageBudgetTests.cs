using EhViewerReader.Core.Imaging;

namespace EhViewerReader.Core.Tests;

public class ImageBudgetTests
{
    [Fact]
    public void NeverUpscales()
    {
        var (w, h) = ImageBudget.Compute(400, 300, targetWidth: 2000, targetHeight: 2000);
        Assert.Equal((400, 300), (w, h));
    }

    [Theory]
    [InlineData(977, 14998, 1400, 0)]
    [InlineData(5304, 7952, 2000, 0)]
    public void KeepsLongStripWidthAndRespectsPixelBudget(int sourceWidth, int sourceHeight, int viewportWidth, int viewportHeight)
    {
        var (w, h) = ImageBudget.Compute(sourceWidth, sourceHeight, viewportWidth, viewportHeight, 24, 16000);
        Assert.InRange((long)w * h, 1, 24_000_000);
        Assert.InRange(w, 1, sourceWidth);
        Assert.InRange(h, 1, sourceHeight);
    }

    [Fact]
    public void LongStripUsesOriginalWidthWhenViewportIsWider()
    {
        var (w, h) = ImageBudget.Compute(977, 14998, targetWidth: 1400, targetHeight: 0, maxMegapixels: 24, maxDimension: 16000);
        Assert.Equal(977, w);
        Assert.Equal(14998, h);
    }

    [Fact]
    public void HugeImageIsLimitedByDimension()
    {
        // 512 MP budget is intentionally above the 400 MP source so the
        // dimension constraint is the binding one here.
        var (w, h) = ImageBudget.Compute(20000, 20000, maxMegapixels: 512, maxDimension: 16000);
        Assert.Equal(16000, w);
        Assert.Equal(16000, h);
    }

    [Fact]
    public void PixelBudgetWinsWhenItIsStricterThanDimension()
    {
        var (w, h) = ImageBudget.Compute(20000, 20000, maxMegapixels: 24, maxDimension: 16000);
        // Rounding can push the product a few pixels above the nominal budget.
        Assert.InRange((long)w * h, 1, 24_100_000);
        Assert.True(w < 16000);
        Assert.True(h < 16000);
    }

    [Fact]
    public void FitPageHonoursBothTargets()
    {
        var (w, h) = ImageBudget.Compute(2000, 3000, targetWidth: 1000, targetHeight: 900);
        Assert.Equal(600, w);
        Assert.Equal(900, h);
    }

    [Fact]
    public void InvalidSourceReturnsOnePixel()
    {
        Assert.Equal((1, 1), ImageBudget.Compute(0, 0));
        Assert.Equal((1, 1), ImageBudget.Compute(-5, 10));
    }
}

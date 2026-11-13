namespace EhViewerForPc.ImageSearch.Tests;

public sealed class ImageKernelTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 7)]
    [InlineData(3, 5)]
    [InlineData(4, 8)]
    [InlineData(41, 43)]
    [InlineData(128, 97)]
    [InlineData(257, 319)]
    public void BlurMatchesScalarAtEdgesAndVectorTails(int width, int height)
    {
        byte[] pixels = new byte[width * height]; new Random(width).NextBytes(pixels);
        var image = new GrayImage(width, height, pixels);
        Assert.Equal(ImageKernels.GaussianBlur(image, false).Pixels, ImageKernels.GaussianBlur(image).Pixels);
    }

    [Fact]
    public void GrayMatchesScalarForAlphaRoundingAndUnalignedRows()
    {
        byte[] pixels = new byte[1_000_003 * 4 + 1]; new Random(42).NextBytes(pixels);
        byte[] expected = new byte[1_000_003], actual = new byte[1_000_003];
        ImageKernels.RgbaToGray(pixels.AsSpan(1), expected, false);
        ImageKernels.RgbaToGray(pixels.AsSpan(1), actual);
        Assert.Equal(expected, actual);
        for (int alpha = 0; alpha < 256; alpha++)
        {
            for (int value = 0; value < 256; value++)
                pixels.AsSpan(1 + value * 4, 4).Fill((byte)value);
            for (int value = 0; value < 256; value++) pixels[1 + value * 4 + 3] = (byte)alpha;
            ImageKernels.RgbaToGray(pixels.AsSpan(1, 1024), expected.AsSpan(0, 256), false);
            ImageKernels.RgbaToGray(pixels.AsSpan(1, 1024), actual.AsSpan(0, 256));
            Assert.Equal(expected.AsSpan(0, 256).ToArray(), actual.AsSpan(0, 256).ToArray());
        }
    }

    [Theory]
    [InlineData(320, 448)]
    [InlineData(113, 1024)]
    [InlineData(701, 533)]
    public void FullExtractorRetainsScalarFeaturesAndNormalizerBytes(int width, int height)
    {
        var image = SyntheticImages.Create(42, width, height);
        byte[] bytes = image.EncodePng();
        Assert.Equal(GrayImage.Decode(bytes, useSimd: false).Pixels, GrayImage.Decode(bytes).Pixels);
        Assert.Equal(new OrbExtractor(useSimd: false).Extract(image), new OrbExtractor().Extract(image));
    }
}

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

    [Theory]
    [InlineData(257, 331, 20, 20, 7, "8cc8c048e89d641c1ab019ef516fe9a4c6201f8aceef11c602e32f3052c2aedb")]
    [InlineData(319, 513, 256, 255, 255, "374708fff7719dd5979ec875d56cd2286f6d3cf7ec317a3b25632aab28ec37bb")]
    [InlineData(177, 441, 256, 250, 1, "d6c8ebaab7eae3d9636bb5ba3c5ab0d33deaed7ff71079166327ad921403fab4")]
    public void FastRetainsFullFeaturesAcrossThresholds(int width, int height, int shades, int threshold,
        int minimum, string expectedHash)
    {
        // 旧提取器生成的固定结果覆盖低对比度回退、阈值相同及强弱角点混合。
        var extractor = new OrbExtractor(new FeatureProfile { FastThreshold = threshold, MinFastThreshold = minimum });
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        Span<byte> descriptor = stackalloc byte[32];
        for (int seed = 0; seed < 4; seed++)
        {
            var random = new Random(seed);
            var pixels = new byte[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)random.Next(shades);
            var features = extractor.Extract(new GrayImage(width, height, pixels));
            writer.Write(features.Length);
            foreach (var feature in features)
            {
                writer.Write(feature.X);
                writer.Write(feature.Y);
                writer.Write(feature.Level);
                writer.Write(feature.Angle);
                writer.Write(feature.Response);
                feature.Descriptor.Write(descriptor);
                writer.Write(descriptor);
            }
        }
        Assert.Equal(expectedHash, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(output.ToArray())));
    }
}

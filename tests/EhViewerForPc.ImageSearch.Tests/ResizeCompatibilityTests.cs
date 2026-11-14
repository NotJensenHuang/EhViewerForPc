using System.Security.Cryptography;

namespace EhViewerForPc.ImageSearch.Tests;

public sealed class ResizeCompatibilityTests
{
    // 哈希由优化前 8c3aaff 的实际 GrayImage.Resize 生成，不在测试中复制缩放算法。
    [Theory]
    [InlineData(7, 5, 4, 3, "ec630fce033e055105f46c668db9ad7f2fdf7214737878dc2582abfb3835295b")]
    [InlineData(31, 23, 10, 7, "12bed3e314c34c322708730894366301123ff8aba905fd578d1abf4922d84159")]
    [InlineData(128, 91, 61, 37, "549bf4d2f09c9603ed00ef00a8654785f0884556fc96a5205e652d3dbfccda9a")]
    [InlineData(101, 173, 97, 169, "9d3043b5da87eb5e9a2f9d7e8e3ac7b4404dfe8062d3c00ce2dd18b21b6e3245")]
    [InlineData(2048, 32, 1, 31, "f542ee6e1f0f09511ac232412f0e82f91ac00a98d8faf7643277a141064e6628")]
    [InlineData(1009, 997, 768, 761, "442a47a4018b3a26472db4e618042985d7db48fbded7bd91e473dec336c244f6")]
    [InlineData(1280, 1810, 768, 1086, "422ec8ca24d50562d8b75c0d240697057554a4d889df852b045fe1425712bb52")]
    public void AreaPixelsRetainTheOriginalGolden(int width, int height, int targetWidth, int targetHeight, string expected)
    {
        byte[] pixels = new byte[width * height];
        new Random(42).NextBytes(pixels);
        var resized = new GrayImage(width, height, pixels).Resize(targetWidth, targetHeight);
        Assert.Equal(expected, Hash(resized.Pixels));
    }

    [Fact]
    public void SingleAxisTiesRetainEvenRounding()
    {
        byte[] pixels = [0, 1, 2, 3, 254, 255];
        Assert.Equal(new byte[] { 0, 2, 254 }, new GrayImage(6, 1, pixels).Resize(3, 1).Pixels);
        Assert.Equal(new byte[] { 0, 2, 254 }, new GrayImage(1, 6, pixels).Resize(1, 3).Pixels);
    }

    [Fact]
    public void NormalizationRetainsBothPixelsAndCanonicalIdentity()
    {
        byte[] pixels = new byte[2304 * 3072];
        new Random(42).NextBytes(pixels);
        var normalized = GrayImage.Decode(new GrayImage(2304, 3072, pixels).EncodePng());
        Assert.Equal(1536, normalized.Width);
        Assert.Equal(2048, normalized.Height);
        Assert.Equal("ccb615a35855f0bc785cf1b41b6bfad115dd0244f1b8b795dc50218f5ce1786f", Hash(normalized.Pixels));
        Assert.Equal("bbe2bf2e251b303fc5fce791d1bd141882e960fe6750dfade4d027386d157fb3", Hash(normalized.EncodePng()));
    }

    [Fact]
    public void ConcurrentResizesKeepIndependentResultsAfterOtherResizes()
    {
        byte[] pixels = new byte[128 * 91];
        new Random(42).NextBytes(pixels);
        var input = new GrayImage(128, 91, pixels);
        var results = new GrayImage[16];
        Parallel.For(0, results.Length, i => results[i] = input.Resize(61, 37));
        var white = new GrayImage(128, 91, Enumerable.Repeat((byte)255, pixels.Length).ToArray()).Resize(61, 37);
        Assert.All(white.Pixels, value => Assert.Equal(255, value));
        Assert.All(results, result => Assert.Equal("549bf4d2f09c9603ed00ef00a8654785f0884556fc96a5205e652d3dbfccda9a", Hash(result.Pixels)));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

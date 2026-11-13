using SkiaSharp;

namespace EhViewerReader.ImageSearch.Tests;

public sealed class ImageInputTests
{
    [Fact]
    public void AnimatedGifUsesFirstFrameAndOversizedHeadersAreRejectedBeforePixels()
    {
        byte[] gif = [71,73,70,56,57,97, 1,0,1,0,128,0,0, 0,0,0,255,255,255,
            33,249,4,0,1,0,0,0, 44,0,0,0,0,1,0,1,0,0, 2,2,68,1,0,
            33,249,4,0,1,0,0,0, 44,0,0,0,0,1,0,1,0,0, 2,2,76,1,0,59];
        Assert.Equal(new byte[] { 0 }, GrayImage.Decode(gif).Pixels);
        byte[] oversized = new GrayImage(16001, 1, new byte[16001]).EncodePng();
        var error = Assert.Throws<InvalidDataException>(() => GrayImage.Decode(oversized));
        Assert.StartsWith("InputTooLarge", error.Message);
    }

    [Fact]
    public void OrbProducesStableOrientedFeaturesAndEmptyBlankResult()
    {
        var source = SyntheticImages.Create(42);
        var extractor = new OrbExtractor();
        var first = extractor.Extract(source); var second = extractor.Extract(GrayImage.Decode(source.EncodePng()));
        Assert.InRange(first.Length, 450, 530); Assert.Equal(first, second);
        Assert.Contains(first, f => f.Level > 0); Assert.Contains(first, f => f.Angle > .1f);
        Assert.Empty(extractor.Extract(new GrayImage(100, 100, new byte[10000])));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => extractor.Extract(source, cancel.Token));
    }

    [Fact]
    public void CroppingAndTransparentWhiteNormalizationAreStrict()
    {
        using var bitmap = new SKBitmap(4, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.Erase(SKColors.Transparent); bitmap.SetPixel(1, 1, SKColors.Black);
        using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var crop = GrayImage.Decode(png.ToArray(), new(1, 1, 2, 2));
        Assert.Equal(new byte[] { 0, 255, 255, 255 }, crop.Pixels);
        Assert.Throws<ArgumentOutOfRangeException>(() => GrayImage.Decode(png.ToArray(), new(-1, 0, 2, 2)));
        Assert.Throws<InvalidDataException>(() => GrayImage.Decode([1, 2, 3]));
    }

    [Theory]
    [InlineData(1, new[] { 0, 1, 2, 3, 4, 5 })]
    [InlineData(2, new[] { 1, 0, 3, 2, 5, 4 })]
    [InlineData(3, new[] { 5, 4, 3, 2, 1, 0 })]
    [InlineData(4, new[] { 4, 5, 2, 3, 0, 1 })]
    [InlineData(5, new[] { 0, 2, 4, 1, 3, 5 })]
    [InlineData(6, new[] { 4, 2, 0, 5, 3, 1 })]
    [InlineData(7, new[] { 5, 3, 1, 4, 2, 0 })]
    [InlineData(8, new[] { 1, 3, 5, 0, 2, 4 })]
    public void AllExifOrientationsReorderTheDecodedPixels(int orientation, int[] expectedOrder)
    {
        using var bitmap = new SKBitmap(2, 3);
        for (int i = 0; i < 6; i++) bitmap.SetPixel(i % 2, i / 2, new SKColor((byte)(i * 40), (byte)(i * 40), (byte)(i * 40)));
        using var image = SKImage.FromBitmap(bitmap); using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        byte[] original = jpeg.ToArray(); byte[] decoded = GrayImage.Decode(original).Pixels;
        using var stream = new MemoryStream(); stream.Write(original.AsSpan(0, 2));
        stream.Write([0xff, 0xe1, 0, 34, 69, 120, 105, 102, 0, 0, 73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0]);
        stream.Write(original.AsSpan(2)); var oriented = GrayImage.Decode(stream.ToArray());
        Assert.Equal(orientation >= 5 ? 3 : 2, oriented.Width); Assert.Equal(orientation >= 5 ? 2 : 3, oriented.Height);
        Assert.Equal(expectedOrder.Select(i => decoded[i]), oriented.Pixels);
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public void SupportedCodecsYieldRealDescriptors(SKEncodedImageFormat format)
    {
        var source = SyntheticImages.Create(42, 160, 200);
        using var bitmap = SKBitmap.Decode(source.EncodePng()); using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(format, 95);
        Assert.NotEmpty(new OrbExtractor().Extract(GrayImage.Decode(encoded.ToArray())));
    }
}

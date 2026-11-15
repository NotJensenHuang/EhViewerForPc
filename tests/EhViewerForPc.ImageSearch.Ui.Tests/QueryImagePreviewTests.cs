using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using EhViewerForPc.ViewModels;
using SkiaSharp;

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public sealed class QueryImagePreviewTests
{
    [AvaloniaTheory]
    [InlineData(1, new[] { 0, 1, 2, 3, 4, 5 })]
    [InlineData(2, new[] { 1, 0, 3, 2, 5, 4 })]
    [InlineData(3, new[] { 5, 4, 3, 2, 1, 0 })]
    [InlineData(4, new[] { 4, 5, 2, 3, 0, 1 })]
    [InlineData(5, new[] { 0, 2, 4, 1, 3, 5 })]
    [InlineData(6, new[] { 4, 2, 0, 5, 3, 1 })]
    [InlineData(7, new[] { 5, 3, 1, 4, 2, 0 })]
    [InlineData(8, new[] { 1, 3, 5, 0, 2, 4 })]
    public async Task PreviewKeepsColorAndAllExifOrientationsInOriginalCropCoordinates(int orientation, int[] order)
    {
        using var fixture = new ReaderRetryIntegrationTests.Fixture();
        using var vm = new ImageSearchViewModel(fixture.Services, () => []);
        using var source = new SKBitmap(2, 3);
        for (int i = 0; i < 6; i++) source.SetPixel(i % 2, i / 2, new SKColor((byte)(30 + i * 30), (byte)(200 - i * 20), (byte)(25 + i * 10)));
        using var jpeg = source.Encode(SKEncodedImageFormat.Jpeg, 100);
        byte[] original = jpeg.ToArray();
        using var decoded = SKBitmap.Decode(original);
        var expected = order.Select(i => decoded.GetPixel(i % 2, i / 2)).ToArray();
        using var stream = new MemoryStream(); stream.Write(original.AsSpan(0, 2));
        stream.Write([0xff, 0xe1, 0, 34, 69, 120, 105, 102, 0, 0, 73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0]);
        stream.Write(original.AsSpan(2));
        await vm.LoadBytesAsync(stream.ToArray(), "彩色 EXIF");
        Assert.True(vm.HasInput, vm.StatusText);
        Assert.Equal(orientation >= 5 ? 3 : 2, vm.ImageWidth);
        Assert.Equal(orientation >= 5 ? 2 : 3, vm.ImageHeight);
        using var previewStream = new MemoryStream();
        vm.QueryPreview!.Save(previewStream, PngBitmapEncoderOptions.Default);
        using var preview = SKBitmap.Decode(previewStream.ToArray());
        Assert.Equal(expected, Enumerable.Range(0, 6).Select(i => preview.GetPixel(i % preview.Width, i / preview.Width)));
        Assert.Contains(expected, p => p.Red != p.Green);
        Assert.Equal(vm.ImageWidth, vm.CropWidth);
        Assert.Equal(vm.ImageHeight, vm.CropHeight);
    }

    [AvaloniaFact]
    public async Task LargeColorPreviewIsBoundedButCropUsesTheOriginalDimensions()
    {
        using var fixture = new ReaderRetryIntegrationTests.Fixture();
        using var vm = new ImageSearchViewModel(fixture.Services, () => []);
        using var source = new SKBitmap(3000, 1000);
        source.Erase(new SKColor(235, 40, 80));
        using var png = source.Encode(SKEncodedImageFormat.Png, 100);
        await vm.LoadBytesAsync(png.ToArray(), "彩色大图");
        Assert.True(vm.HasInput, vm.StatusText);
        Assert.Equal(2048, vm.QueryPreview!.PixelSize.Width);
        Assert.Equal(3000, vm.ImageWidth);
        Assert.Equal(1000, vm.ImageHeight);
        Assert.Equal(3000, vm.CropWidth);
        Assert.Equal(1000, vm.CropHeight);
    }
}

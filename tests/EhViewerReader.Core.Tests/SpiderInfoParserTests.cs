using System.Text;
using EhViewerReader.Core.Format;
using EhViewerReader.Core.Models;

namespace EhViewerReader.Core.Tests;

public class SpiderInfoParserTests
{
    [Fact]
    public void ParsesVersion2Header()
    {
        string text = """
            VERSION2
            0000000e
            1002892
            5b438f2fab
            1
            20
            20
            20
            0 2b1ebca51c
            1 16819fa194
            """;

        SpiderHeader? header = SpiderInfoParser.ParseText(Encoding.ASCII.GetBytes(text), out string? error);

        Assert.NotNull(header);
        Assert.True(header.IsValid);
        Assert.Null(error);
        Assert.Equal(2, header.Version);
        Assert.Equal(14, header.StartPage);
        Assert.Equal(1002892, header.Gid);
        Assert.Equal("5b438f2fab", header.Token);
        Assert.Equal(20, header.Pages);
        Assert.Equal(20, header.PreviewPages);
        Assert.Equal(20, header.PreviewPerPage);
    }

    [Fact]
    public void ParsesVersion1HeaderWithConsumedIgnoredLine()
    {
        string text = """
            00000000
            123456
            abcdef01
            1
            2
            999
            20
            0 token0
            1 token1
            """;

        SpiderHeader? header = SpiderInfoParser.ParseText(Encoding.ASCII.GetBytes(text), out string? error);

        Assert.NotNull(header);
        Assert.True(header.IsValid);
        Assert.Null(error);
        Assert.Equal(1, header.Version);
        Assert.Equal(123456, header.Gid);
        Assert.Equal(20, header.Pages);
        Assert.Equal(2, header.PreviewPages);
        Assert.Equal(0, header.PreviewPerPage);
    }

    [Fact]
    public void HandlesBomAndCrlf()
    {
        string text = "\uFEFFVERSION2\r\n00000000\r\n42\r\ntoken\r\n1\r\n1\r\n1\r\n3\r\n";
        SpiderHeader? header = SpiderInfoParser.ParseText(Encoding.UTF8.GetBytes(text), out _);

        Assert.NotNull(header);
        Assert.True(header.IsValid);
        Assert.Equal(42, header.Gid);
        Assert.Equal(3, header.Pages);
    }

    [Fact]
    public void RejectsInvalidPageCountButKeepsGid()
    {
        string text = "VERSION2\n00000000\n42\ntoken\n1\n1\n1\n0\n";
        SpiderHeader? header = SpiderInfoParser.ParseText(Encoding.ASCII.GetBytes(text), out string? error);

        Assert.NotNull(header);
        Assert.False(header.IsValid);
        Assert.Equal(42, header.Gid);
        Assert.NotNull(error);
    }

    [Fact]
    public void ParsesIndefiniteCborHeaderFromEhViewerTest()
    {
        byte[] bytes =
        [
            0xBF, 0x63, 0x67, 0x69, 0x64, 0x1A, 0x35, 0xA4, 0xE9, 0x02,
            0x65, 0x74, 0x6F, 0x6B, 0x65, 0x6E, 0x6A, 0x30, 0x30, 0x30,
            0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x65, 0x70, 0x61,
            0x67, 0x65, 0x73, 0x18, 0x4D, 0x69, 0x70, 0x54, 0x6F, 0x6B,
            0x65, 0x6E, 0x4D, 0x61, 0x70, 0xBF, 0x00, 0x6A, 0x30, 0x30,
            0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x31, 0xFF, 0x6C,
            0x70, 0x72, 0x65, 0x76, 0x69, 0x65, 0x77, 0x50, 0x61, 0x67,
            0x65, 0x73, 0x02, 0x6E, 0x70, 0x72, 0x65, 0x76, 0x69, 0x65,
            0x77, 0x50, 0x65, 0x72, 0x50, 0x61, 0x67, 0x65, 0x18, 0x28,
            0xFF,
        ];

        SpiderHeader? header = CborHeaderReader.TryRead(bytes, out string? error);

        Assert.NotNull(header);
        Assert.True(header.HasGid);
        Assert.Equal(900000002L, header.Gid);
        Assert.Equal("0000000000", header.Token);
        Assert.Equal(77, header.Pages);
        Assert.True(header.IsCbor);
    }

    [Fact]
    public void CborRejectsNonMapAndTruncated()
    {
        Assert.Null(CborHeaderReader.TryRead([0x00, 0x01], out _));
        Assert.Null(CborHeaderReader.TryRead([0xBF, 0x63, 0x67], out _));
    }
}

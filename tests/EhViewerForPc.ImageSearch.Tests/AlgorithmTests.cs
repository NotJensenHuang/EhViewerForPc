using System.Text;

namespace EhViewerForPc.ImageSearch.Tests;

public sealed class AlgorithmTests
{
    [Fact]
    public void Blake3MatchesAllOfficialChunkAndTreeBoundaryVectors()
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "blake3.json")));
        foreach (var vector in document.RootElement.EnumerateArray())
        {
            int length = vector.GetProperty("inputLength").GetInt32();
            byte[] input = Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();
            Assert.Equal(vector.GetProperty("hash").GetString(), Convert.ToHexStringLower(Blake3.Hash(input)));
        }
    }
    [Theory]
    [InlineData("", "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262")]
    [InlineData("abc", "6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85")]
    public void Blake3MatchesPublishedVectors(string text, string expected)
        => Assert.Equal(expected, Convert.ToHexStringLower(Blake3.Hash(Encoding.UTF8.GetBytes(text))));

    [Fact]
    public void HammingAndResidualCoverAll256Bits()
    {
        var zero = default(BinaryDescriptor); var ones = new BinaryDescriptor(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue);
        Assert.Equal(256, zero.Distance(ones)); Assert.Equal(0, ones.Distance(ones));
        Assert.Equal(ones, ones.Xor(new(1, 2, 3, 4)).Xor(new(1, 2, 3, 4)));
        Span<byte> bytes = stackalloc byte[32]; ones.Write(bytes); Assert.Equal(ones, BinaryDescriptor.Read(bytes));
    }

    [Fact]
    public void WilsonUsesUpstreamContinuousVarianceFormula()
    {
        Assert.Equal(0, MatchScoring.Wilson([]));
        Assert.InRange(MatchScoring.Wilson([1]), .155f, .157f);
        Assert.True(MatchScoring.Wilson([1, 1, 1]) > MatchScoring.Wilson([1]));
        Assert.True(MatchScoring.Wilson([.75f, .75f]) > MatchScoring.Wilson([.5f, 1f]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KModesHandlesUniformInputAndWeightedTwoLevelAllocation(bool plusPlus)
    {
        var all = Enumerable.Repeat(new BinaryDescriptor(1, 2, 3, 4), 300).ToArray();
        var uniform = BinaryKModes.Train(all, 8, plusPlus: plusPlus);
        Assert.Equal(8, uniform.Centers.Length); Assert.Equal(300, uniform.Frequencies.Sum()); Assert.Equal(0, uniform.Distance);
        var random = Descriptors(600, 42);
        var trained = BinaryKModes.Train(random, 16, iterations: 3, plusPlus: plusPlus);
        Assert.Equal(16, trained.Centers.Length); Assert.Equal(600, trained.Frequencies.Sum()); Assert.True(trained.Distance > 0);
        Assert.Throws<InvalidOperationException>(() => BinaryKModes.Train(random, 32));
    }

    [Fact]
    public void HnswFindsExactAndNearVectorsAfterCheckedReload()
    {
        using var scope = new TestDirectory();
        var data = Descriptors(500, 42); var graph = new BinaryHnsw();
        foreach (var descriptor in data) graph.Add(descriptor);
        graph.Save(scope.File("quantizer.bin"), "profile");
        graph = BinaryHnsw.Load(scope.File("quantizer.bin"), "profile");
        for (int i = 0; i < data.Length; i += 11)
        {
            Assert.Equal(i, graph.Search(data[i], 1, 64)[0]);
            Assert.Equal(i, graph.Search(data[i].Xor(new(1, 2, 0, 0)), 1, 64)[0]);
        }
        Assert.Throws<InvalidDataException>(() => BinaryHnsw.Load(scope.File("quantizer.bin"), "wrong"));
        byte[] bytes = File.ReadAllBytes(scope.File("quantizer.bin")); bytes[80] ^= 1;
        File.WriteAllBytes(scope.File("quantizer.bin"), bytes);
        Assert.Throws<InvalidDataException>(() => BinaryHnsw.Load(scope.File("quantizer.bin"), "profile"));
    }

    internal static BinaryDescriptor[] Descriptors(int count, int seed)
    {
        var random = new Random(seed); var result = new BinaryDescriptor[count]; var bytes = new byte[32];
        for (int i = 0; i < count; i++) { random.NextBytes(bytes); result[i] = BinaryDescriptor.Read(bytes); }
        return result;
    }
}

internal sealed class TestDirectory : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EhImageSearch-" + Guid.NewGuid().ToString("N"));
    internal TestDirectory() => Directory.CreateDirectory(Path);
    internal string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() => Directory.Delete(Path, true);
}

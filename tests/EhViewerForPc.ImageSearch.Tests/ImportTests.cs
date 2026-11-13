using System.Formats.Tar;

namespace EhViewerForPc.ImageSearch.Tests;

public sealed class ImportTests
{
    [Fact]
    public void TarReportCannotOverwriteInputAndBackendCannotContainInput()
    {
        using var scope = new TestDirectory();
        string archive = scope.File("input.tar");
        WriteTar(archive, [("page.png", SyntheticImages.Create(42, 160, 200).EncodePng())]);
        byte[] before = File.ReadAllBytes(archive);
        Assert.Throws<InvalidOperationException>(() => ImageImporter.ValidateImportPaths(scope.File("backend"), archive, archive));
        Assert.Throws<InvalidOperationException>(() => ImageImporter.ValidateImportPaths(scope.Path, archive, scope.File("report.jsonl")));
        Assert.Equal(before, File.ReadAllBytes(archive));
    }

    [Fact]
    public async Task DirectoryImportUsesBoundedParallelBatchesAndKeepsInputOrder()
    {
        using var scope = new TestDirectory();
        string input = scope.File("input"); Directory.CreateDirectory(input);
        for (int i = 0; i < 12; i++)
            File.WriteAllBytes(Path.Combine(input, $"{i:D2}.png"), i == 7 ? [1, 2, 3] : SyntheticImages.Create(42 + i, 160, 200).EncodePng());
        string[] before = Directory.GetFiles(input).Select(p => p + "|" + new FileInfo(p).Length + "|" + File.GetLastWriteTimeUtc(p).Ticks).ToArray();
        using var database = new ImageDatabase(scope.File("backend"), writable: true);
        var summary = await ImageImporter.ImportAsync(database, input, scope.File("report.jsonl"), new() { Workers = 4, BatchSize = 5 }).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(11, summary.Added); Assert.Equal(1, summary.Failed);
        string[] aliases = File.ReadAllLines(scope.File("report.jsonl")).Take(12)
            .Select(line => { using var document = System.Text.Json.JsonDocument.Parse(line); return document.RootElement.GetProperty("alias").GetString(); }).ToArray()!;
        Assert.Equal(Directory.GetFiles(input).Select(Path.GetFileName), aliases);
        Assert.Equal(before, Directory.GetFiles(input).Select(p => p + "|" + new FileInfo(p).Length + "|" + File.GetLastWriteTimeUtc(p).Ticks));
        Assert.Equal(11, database.ReadCached().Count());
    }

    [Fact]
    public async Task TarImportReportsEveryTerminalStateAndReplaysWithoutDuplicates()
    {
        using var scope = new TestDirectory();
        string tarPath = scope.File("input.tar");
        WriteTar(tarPath, [("pages/图 1.png", SyntheticImages.Create(42, 160, 200).EncodePng()), ("pages/copy.png", SyntheticImages.Create(42, 160, 200).EncodePng()), ("pages/bad.png", new byte[] { 1, 2, 3 })]);
        using var database = new ImageDatabase(scope.File("backend"), writable: true);
        var summary = await ImageImporter.ImportAsync(database, tarPath, scope.File("report.jsonl")).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(summary.Complete); Assert.Equal(3, summary.Total); Assert.Equal(1, summary.Added); Assert.Equal(1, summary.AlreadyPresent); Assert.Equal(1, summary.Failed);
        Assert.Equal(4, File.ReadAllLines(scope.File("report.jsonl")).Length); Assert.Single(database.ReadCached());
        var replay = await ImageImporter.ImportAsync(database, tarPath, scope.File("replay.jsonl"));
        Assert.Equal(0, replay.Added); Assert.Equal(2, replay.AlreadyPresent); Assert.Equal(1, replay.Failed);
    }

    [Theory]
    [InlineData("../escape.png")]
    [InlineData("/absolute.png")]
    public async Task UnsafeTarStopsAllWorkersWithoutPublishingACompleteReport(string alias)
    {
        using var scope = new TestDirectory();
        WriteTar(scope.File("input.tar"), [(alias, new byte[] { 1, 2, 3 })]);
        using var database = new ImageDatabase(scope.File("backend"), writable: true);
        await Assert.ThrowsAnyAsync<Exception>(() => ImageImporter.ImportAsync(database, scope.File("input.tar"), scope.File("report.jsonl")).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(File.Exists(scope.File("report.jsonl"))); Assert.Empty(database.ReadCached());
    }

    [Fact]
    public async Task CancellationStopsBoundedPipelineAndNeverPublishesSuccess()
    {
        using var scope = new TestDirectory(); Directory.CreateDirectory(scope.File("input"));
        using var database = new ImageDatabase(scope.File("backend"), writable: true);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageImporter.ImportAsync(database, scope.File("input"), scope.File("report.jsonl"), cancellationToken: cancellation.Token));
        Assert.False(File.Exists(scope.File("report.jsonl")));
    }

    [Fact]
    public void DHashApproximateDeduplicationAndAliasOverwriteSurviveReopen()
    {
        using var scope = new TestDirectory(); var descriptors = AlgorithmTests.Descriptors(90, 42);
        using (var database = new ImageDatabase(scope.Path, writable: true, hashMode: ImageHashMode.DHash))
        {
            database.Add("first.png", new byte[8], descriptors);
            Assert.Equal("already_present", database.Add("second.png", [1, 0, 0, 0, 0, 0, 0, 0], descriptors, duplicateDistance: 1).Status);
            Assert.Single(database.ReadCached());
        }
        using (var database = new ImageDatabase(scope.Path, writable: true))
        {
            Assert.Equal("already_present", database.Add("replacement.png", new byte[8], descriptors, overwrite: true).Status);
            ImageSearchEngine.Train(database, 1, 1); ImageSearchEngine.Build(database);
            var result = new ImageSearchEngine(database).SearchDescriptors(descriptors, new() { NProbe = 1 });
            Assert.Equal(new[] { "replacement.png" }, Assert.Single(result).Aliases);
            Assert.Throws<InvalidDataException>(() => database.Add("wrong-threshold.png", new byte[8], descriptors, minFeatures: 20));
        }
    }

    internal static void WriteTar(string path, (string Alias, byte[] Bytes)[] entries)
    {
        using var stream = File.Create(path); using var writer = new TarWriter(stream);
        foreach (var (alias, bytes) in entries)
        {
            using var data = new MemoryStream(bytes);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, alias) { DataStream = data });
        }
    }
}

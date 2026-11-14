namespace EhViewerForPc.ImageSearch.Tests;

public sealed class IndexBuildProgressTests
{
    [Fact]
    public void ProgressUsesActualFeatureAndPartitionCountsWithoutChangingPublishedBytes()
    {
        using var scope = new TestDirectory();
        var values = new List<IndexBuildProgress>();
        foreach (bool report in new[] { false, true })
        {
            using var database = Create(scope.File(report ? "progress" : "plain"));
            var result = report ? ImageSearchEngine.Build(database, new Observer(values.Add), batchSize: 2, workers: 2)
                : ImageSearchEngine.Build(database, batchSize: 2, workers: 2);
            Assert.True(result.Complete);
            Assert.Equal(2000, result.IndexedVectors);
        }
        Assert.Contains(values, value => value.Stage == IndexBuildStage.Assigning && value.Completed == 0 && value.Total == 2000);
        Assert.Contains(values, value => value.Stage == IndexBuildStage.Assigning && value.Completed == 2000 && value.Total == 2000);
        Assert.Contains(values, value => value.Stage == IndexBuildStage.Merging && value.Completed == 32 && value.Total == 32);
        Assert.Contains(values, value => value.Stage == IndexBuildStage.ValidatingIndex && value.Completed == value.Total && value.Total > 0);
        Assert.Equal(IndexBuildStage.Complete, values[^1].Stage);
        Assert.All(values, value => Assert.InRange(value.Completed, 0, value.Total));
        var assigned = values.Where(v => v.Stage == IndexBuildStage.Assigning).Select(v => v.Completed).ToArray();
        Assert.Equal(assigned.Order(), assigned);
        foreach (string file in new[] { "quantizer.bin", "invlists.bin" })
            Assert.Equal(File.ReadAllBytes(scope.File("plain/" + file)), File.ReadAllBytes(scope.File("progress/" + file)));
    }

    [Fact]
    public void ResumedSegmentsCountAsCompletedAssignmentInsteadOfStartingAtZero()
    {
        using var scope = new TestDirectory(); using var database = Create(scope.File("backend"));
        Assert.False(ImageSearchEngine.Build(database, batchSize: 2, stopAfterSegments: 1).Complete);
        var values = new List<IndexBuildProgress>();
        Assert.True(ImageSearchEngine.Build(database, new Observer(values.Add), batchSize: 2).Complete);
        var initial = values.First(v => v.Stage == IndexBuildStage.Assigning);
        Assert.Equal(1000, initial.Completed);
        Assert.Equal(2000, initial.Total);
    }

    [Fact]
    public void CancellationDuringChecksumPreservesThePreviousIndexAndCanResume()
    {
        using var scope = new TestDirectory(); using var database = Create(scope.File("backend"));
        ImageSearchEngine.Build(database);
        byte[] old = File.ReadAllBytes(Path.Combine(database.DirectoryPath, "invlists.bin"));
        database.Add("extra.png", Blake3.Hash([99]), AlgorithmTests.Descriptors(500, 99));
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => ImageSearchEngine.Build(database, new Observer(value =>
        {
            if (value.Stage == IndexBuildStage.HashingIndex && value.Completed > 0) cancellation.Cancel();
        }), cancellationToken: cancellation.Token));
        Assert.Equal(old, File.ReadAllBytes(Path.Combine(database.DirectoryPath, "invlists.bin")));
        Assert.Empty(Directory.GetFiles(database.DirectoryPath, "*.part"));
        Assert.Equal(5, ImageSearchEngine.Build(database).IndexedImages);
    }

    private static ImageDatabase Create(string path)
    {
        var database = new ImageDatabase(path, writable: true);
        using (var writer = database.CreateImportWriter())
        {
            for (int i = 0; i < 4; i++) writer.Add($"{i}.png", Blake3.Hash([(byte)i]), AlgorithmTests.Descriptors(500, i));
            writer.Commit();
        }
        ImageSearchEngine.Train(database, 32, 4, iterations: 3);
        return database;
    }
    private sealed class Observer(Action<IndexBuildProgress> action) : IProgress<IndexBuildProgress>
    {
        public void Report(IndexBuildProgress value) => action(value);
    }
}

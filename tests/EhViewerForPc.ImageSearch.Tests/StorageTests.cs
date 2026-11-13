namespace EhViewerForPc.ImageSearch.Tests;

public sealed class StorageTests
{
    [Fact]
    public void BatchedWriterCommitsSharedFeaturesBeforeMetadataAndRollsBackUnfinishedBatch()
    {
        using var scope = new TestDirectory();
        using var database = new ImageDatabase(scope.File("backend"), writable: true);
        database.UseSharedFeatureCache(scope.File("shared.db"));
        using (var writer = database.CreateImportWriter())
        {
            for (int i = 0; i < 3; i++) writer.Add($"{i}.png", Blake3.Hash([(byte)i]), AlgorithmTests.Descriptors(90, i));
            Assert.Empty(database.ReadCached());
            Assert.Throws<InvalidOperationException>(() => database.CreateImportWriter());
            writer.Commit();
            Assert.Equal(3, database.ReadCached().Count());
            writer.Add("uncommitted.png", Blake3.Hash([3]), AlgorithmTests.Descriptors(90, 3));
        }
        Assert.Equal(3, database.ReadCached().Count());
        using (var failed = database.CreateImportWriter())
        {
            failed.Add("valid.png", Blake3.Hash([4]), AlgorithmTests.Descriptors(90, 4));
            Assert.Throws<InvalidDataException>(() => failed.Add("../unsafe.png", Blake3.Hash([5]), AlgorithmTests.Descriptors(90, 5)));
            Assert.True(failed.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => failed.Commit());
        }
        Assert.Equal(3, database.ReadCached().Count());
        database.CheckIntegrity();
    }

    [Fact]
    public void LinkedSnapshotPublishesIndependentlyWhileOriginalBucketIsBeingRead()
    {
        using var scope = new TestDirectory();
        string original = scope.File("old"), next = scope.File("next");
        using (var database = new ImageDatabase(original, writable: true))
        {
            database.Add("old.png", Blake3.Hash([1]), AlgorithmTests.Descriptors(90, 1));
            ImageSearchEngine.Train(database, 1, 1);
            ImageSearchEngine.Build(database);
        }
        using (var source = new ImageDatabase(original)) source.CopySnapshotTo(next, shareImmutableFiles: true);
        var index = DiskInvertedIndex.Open(Path.Combine(original, "invlists.bin"), new FeatureProfile().Id,
            DiskInvertedIndex.HashQuantizer(Path.Combine(original, "quantizer.bin")), allowLinkedPublication: true);
        using var reading = index.ReadBucket(0).GetEnumerator();
        Assert.True(reading.MoveNext());
        using (var database = new ImageDatabase(next, writable: true))
        {
            database.Add("new.png", Blake3.Hash([2]), AlgorithmTests.Descriptors(90, 2));
            Assert.Equal(2, ImageSearchEngine.Build(database).IndexedImages);
        }
        int oldRecords = 1;
        while (reading.MoveNext()) oldRecords++;
        Assert.Equal(90, oldRecords);
        using var unchanged = new ImageDatabase(original);
        Assert.Equal(1, new ImageSearchEngine(unchanged).IndexedImages);
    }

    [Fact]
    public void RemovedContentKeepsStableIdsAndCanBeReintroducedAfterSharedCachePruning()
    {
        using var scope = new TestDirectory();
        string shared = scope.File("shared.db");
        using var database = new ImageDatabase(scope.File("backend"), writable: true);
        database.UseSharedFeatureCache(shared);
        var original = AlgorithmTests.Descriptors(90, 1);
        var removed = AlgorithmTests.Descriptors(90, 2);
        database.Add("first.png", Blake3.Hash([1]), original);
        database.Add("removed.png", Blake3.Hash([2]), removed);
        ImageSearchEngine.Train(database, 2, 2);
        ImageSearchEngine.Build(database);
        long removedId = database.ReadCached().Last().ImageId;
        Assert.Equal(1, database.RetainAliases(new HashSet<string> { "first.png" }));
        Assert.Equal(1, ImageSearchEngine.Build(database, allowedImageIds: database.AliasedImageIds()).IndexedImages);
        using (var cache = new SharedFeatureCache(shared))
            Assert.Equal(1, cache.Prune(database.ReferencedFeatureHashes().Select(hash => (database.Profile.Id, hash))));
        database.Add("new.png", Blake3.Hash([3]), AlgorithmTests.Descriptors(90, 3));
        Assert.True(database.ReadCached().Last().ImageId > removedId);
        database.Add("restored.png", Blake3.Hash([2]), removed);
        Assert.True(database.HasPendingImages);
        Assert.Equal(3, ImageSearchEngine.Build(database, allowedImageIds: database.AliasedImageIds()).IndexedImages);
        Assert.Equal("restored.png", new ImageSearchEngine(database).SearchDescriptors(removed, new() { NProbe = 2 })[0].Aliases[0]);
        Assert.False(database.HasPendingImages);
    }

    [Fact]
    public void IncrementalSnapshotPreservesOriginalAndAddsOnlyNewVectors()
    {
        using var scope = new TestDirectory(); string original = scope.File("old"), next = scope.File("next");
        using (var database = new ImageDatabase(original, writable: true))
        {
            for (int i = 0; i < 3; i++) database.Add($"page-{i}.png", Blake3.Hash([(byte)i]), AlgorithmTests.Descriptors(90, i));
            ImageSearchEngine.Train(database, 4, 3); ImageSearchEngine.Build(database);
        }
        using (var source = new ImageDatabase(original)) source.CopySnapshotTo(next);
        using (var database = new ImageDatabase(next, writable: true))
        {
            var descriptors = AlgorithmTests.Descriptors(90, 999);
            database.Add("new.png", Blake3.Hash([99]), descriptors); ImageSearchEngine.Build(database);
            Assert.Equal(4, new ImageSearchEngine(database).IndexedImages);
            Assert.Equal("new.png", new ImageSearchEngine(database).SearchDescriptors(descriptors, new() { NProbe = 4 })[0].Aliases[0]);
        }
        using var unchanged = new ImageDatabase(original); Assert.Equal(3, new ImageSearchEngine(unchanged).IndexedImages);
    }
    [Fact]
    public void ImportTransactionsDeduplicateAliasesAndRejectConfigurationChanges()
    {
        using var scope = new TestDirectory();
        using (var db = new ImageDatabase(scope.Path, writable: true))
        {
            var descriptors = AlgorithmTests.Descriptors(90, 42); byte[] hash = Blake3.Hash([1, 2, 3]);
            Assert.Equal("added", db.Add("pages/a.png", hash, descriptors).Status);
            Assert.Equal("already_present", db.Add("pages/a.png", hash, descriptors).Status);
            Assert.Equal("already_present", db.Add("pages/b.png", hash, descriptors).Status);
            Assert.Throws<InvalidDataException>(() => db.Add("pages/a.png", Blake3.Hash([4]), descriptors));
            Assert.Single(db.ReadCached()); db.CheckIntegrity();
            Assert.Throws<IOException>(() => new ImageDatabase(scope.Path, writable: true));
        }
        using var read = new ImageDatabase(scope.Path);
        Assert.Single(read.ReadCached());
        Assert.Throws<InvalidDataException>(() => new ImageDatabase(scope.Path, hashMode: ImageHashMode.DHash));
        Assert.Throws<InvalidDataException>(() => new ImageDatabase(scope.Path, profile: new() { Features = 600 }));
    }

    [Fact]
    public void BuildPauseReplayMergeAndCleanRetainSearchableVectors()
    {
        using var scope = new TestDirectory();
        var all = Enumerable.Range(0, 6).Select(i => AlgorithmTests.Descriptors(90, 42 + i)).ToArray();
        using (var db = new ImageDatabase(scope.Path, writable: true))
        {
            for (int i = 0; i < all.Length; i++) db.Add($"pages/{i}.png", Blake3.Hash([(byte)i]), all[i]);
            ImageSearchEngine.Train(db, 8, 6, iterations: 3);
            Assert.False(ImageSearchEngine.Build(db, batchSize: 2, stopAfterSegments: 1).Complete);
        }
        using (var db = new ImageDatabase(scope.Path, writable: true))
        {
            var built = ImageSearchEngine.Build(db, batchSize: 2);
            Assert.True(built.Complete); Assert.Equal(540, built.IndexedVectors); Assert.Equal(6, built.IndexedImages);
            var engine = new ImageSearchEngine(db);
            var hits = engine.SearchDescriptors(all[3], new() { NProbe = 8, K = 3 });
            Assert.Equal("pages/3.png", hits[0].Aliases[0]);
            var oneQuery = engine.SearchDescriptors([all[3][0]], new() { NProbe = 8, K = 3, MaxDistance = 255, CountScore = true });
            Assert.Equal(3, oneQuery.Sum(hit => hit.Score)); // K is global across all probed buckets.
            Assert.Empty(engine.SearchDescriptors([], new() { NProbe = 8 }));
            Assert.Equal(540, ImageSearchEngine.Build(db).IndexedVectors);
            db.Clean(); Assert.Empty(db.ReadCached());
        }
        using (var db = new ImageDatabase(scope.Path))
        {
            var engine = new ImageSearchEngine(db);
            Assert.Equal("pages/3.png", engine.SearchDescriptors(all[3], new() { NProbe = 8 })[0].Aliases[0]);
            Assert.Throws<ArgumentOutOfRangeException>(() => engine.SearchDescriptors(all[0], new() { NProbe = 9 }));
        }
    }

    [Fact]
    public void DiskIndexRejectsTruncationAndQuantizerMismatch()
    {
        using var scope = new TestDirectory();
        DiskInvertedIndex.Write(scope.File("index.bin"), "profile", "quantizer", 2,
            i => i == 0 ? [new IndexVector(1, 1, default)] : []);
        Assert.Throws<InvalidDataException>(() => DiskInvertedIndex.Open(scope.File("index.bin"), "profile", "wrong"));
        byte[] bytes = File.ReadAllBytes(scope.File("index.bin")); File.WriteAllBytes(scope.File("index.bin"), bytes[..^8]);
        Assert.Throws<InvalidDataException>(() => DiskInvertedIndex.Open(scope.File("index.bin"), "profile", "quantizer"));
    }
}

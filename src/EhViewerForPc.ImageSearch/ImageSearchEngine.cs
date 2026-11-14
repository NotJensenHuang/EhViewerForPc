// Port of imsearch's train/build/search orchestration; managed C# format only.
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EhViewerForPc.ImageSearch;

/// <summary>Reports completion or an intentional safe build pause.</summary>
public sealed record BuildResult(bool Complete, int SegmentsWritten, long IndexedImages, long IndexedVectors);

/// <summary>Runs training, bounded index construction, and on-disk descriptor search.</summary>
public sealed class ImageSearchEngine
{
    private readonly ImageDatabase _database;
    private readonly BinaryHnsw _quantizer;
    private readonly DiskInvertedIndex _index;

    /// <summary>Opens a validated quantizer and published IVF index for querying.</summary>
    public ImageSearchEngine(ImageDatabase database, bool allowLinkedPublication = false)
    {
        _database = database;
        string quantizer = Path.Combine(database.DirectoryPath, "quantizer.bin");
        _quantizer = BinaryHnsw.Load(quantizer, database.Profile.Id);
        _index = DiskInvertedIndex.Open(Path.Combine(database.DirectoryPath, "invlists.bin"), database.Profile.Id, DiskInvertedIndex.HashQuantizer(quantizer), allowLinkedPublication);
        if (_index.BucketCount != _quantizer.Count) throw new InvalidDataException("Index and quantizer bucket counts differ.");
    }

    /// <summary>Gets the number of searchable unique images from the actual index.</summary>
    public long IndexedImages => _index.ImageCount;
    /// <summary>Gets the actual index descriptor count.</summary>
    public long IndexedVectors => _index.VectorCount;
    /// <summary>Gets the available center count.</summary>
    public int Centers => _quantizer.Count;
    /// <summary>Gets the most recent query timing breakdown.</summary>
    public SearchTiming? LastTiming { get; private set; }

    /// <summary>Trains and saves a new quantizer, refusing to invalidate existing index segments.</summary>
    public static KModesResult Train(ImageDatabase database, int centers, int images, int iterations = 20, bool plusPlus = false,
        bool twoLevel = true, int seed = 20261112, CancellationToken cancellationToken = default, int trainingVectorLimit = 4_000_000, int workers = 1)
    {
        database.RequireWriter();
        if (images < 1 || trainingVectorLimit is < 1 or > 64_000_000 || workers is < 1 or > IndexBuildOptions.MaximumWorkers)
            throw new ArgumentOutOfRangeException(nameof(images));
        if (File.Exists(Path.Combine(database.DirectoryPath, "invlists.bin")) || Directory.EnumerateFiles(database.DirectoryPath, "segment-*.ivf").Any())
            throw new InvalidOperationException("Train a new generation instead of replacing an indexed quantizer.");
        // Explicit cap prevents a mistakenly chosen image limit from filling memory during training.
        var data = new List<BinaryDescriptor>();
        foreach (var image in database.ReadCached(limit: images))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((long)data.Count + image.Descriptors.Length > trainingVectorLimit)
                throw new InvalidOperationException("TrainingBudgetExceeded: reduce training images or train in a separately budgeted job.");
            data.AddRange(image.Descriptors);
        }
        var trained = BinaryKModes.Train(data.ToArray(), centers, iterations, plusPlus, twoLevel, seed, cancellationToken, workers);
        var graph = new BinaryHnsw(seed: seed);
        foreach (var center in trained.Centers) graph.Add(center, cancellationToken);
        graph.Save(Path.Combine(database.DirectoryPath, "quantizer.bin"), database.Profile.Id);
        return trained;
    }

    /// <summary>Builds bounded segments and publishes a streaming merge; a paused build remains resumable.</summary>
    public static BuildResult Build(ImageDatabase database, int batchSize = 2000, bool stopBeforeMerge = false,
        bool cleanBeforeMerge = false, int? stopAfterSegments = null, CancellationToken cancellationToken = default, IReadOnlySet<long>? allowedImageIds = null, int workers = 1)
    {
        database.RequireWriter();
        if (batchSize is < 1 or > 2000 || stopAfterSegments is < 1 || workers is < 1 or > IndexBuildOptions.MaximumWorkers)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        string directory = database.DirectoryPath, profileId = database.Profile.Id;
        string quantizerPath = Path.Combine(directory, "quantizer.bin");
        var quantizer = BinaryHnsw.Load(quantizerPath, profileId);
        string quantizerHash = DiskInvertedIndex.HashQuantizer(quantizerPath);
        // 文件落盘与 SQLite 检查点之间崩溃会留下孤立段；它们没有任何已提交引用。
        var pendingSegments = new HashSet<string>(StringComparer.Ordinal);
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT segment FROM vector_stats WHERE indexed=0 AND segment IS NOT NULL";
            using var reader = command.ExecuteReader();
            while (reader.Read()) pendingSegments.Add(reader.GetString(0));
        }
        if (File.Exists(Path.Combine(directory, "invlists.bin")))
            _ = DiskInvertedIndex.Open(Path.Combine(directory, "invlists.bin"), profileId, quantizerHash);
        foreach (string file in Directory.EnumerateFiles(directory, "segment-*.ivf"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(file);
            if (IsSegmentName(name) && !pendingSegments.Contains(name)) File.Delete(file);
        }
        int written = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = database.ReadCached(unsegmentedOnly: true, limit: batchSize).ToArray();
            if (batch.Length == 0) break;
            var buckets = Enumerable.Range(0, quantizer.Count).Select(_ => new List<IndexVector>()).ToArray();
            var assignments = new int[batch.Length][];
            void AssignImage(int ordinal)
            {
                var image = batch[ordinal];
                cancellationToken.ThrowIfCancellationRequested();
                int[] assigned = new int[image.Descriptors.Length];
                for (int i = 0; i < image.Descriptors.Length; i++)
                {
                    if ((i & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
                    assigned[i] = quantizer.Search(image.Descriptors[i], 1)[0];
                }
                assignments[ordinal] = assigned;
            }
            // 量化器在此阶段只读；按页并行查桶，随后依旧按原向量 ID 顺序落盘。
            if (workers == 1) for (int ordinal = 0; ordinal < batch.Length; ordinal++) AssignImage(ordinal);
            else Parallel.For(0, batch.Length, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken }, AssignImage);
            for (int ordinal = 0; ordinal < batch.Length; ordinal++)
            {
                var image = batch[ordinal];
                for (int i = 0; i < image.Descriptors.Length; i++)
                {
                    int bucket = assignments[ordinal][i];
                    buckets[bucket].Add(new(image.FirstVectorId + i, image.ImageId, image.Descriptors[i].Xor(quantizer[bucket])));
                }
            }
            string segment = "segment-" + Guid.NewGuid().ToString("N") + ".ivf";
            DiskInvertedIndex.Write(Path.Combine(directory, segment), profileId, quantizerHash, buckets.Length, b => buckets[b], cancellationToken);
            // The durable file precedes its checkpoint. An orphan file is never read by a subsequent build.
            using (var connection = database.Open())
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var image in batch) ImageDatabase.Execute(connection, "UPDATE vector_stats SET segment=$segment WHERE image_id=$id AND segment IS NULL AND indexed=0",
                    transaction, ("$segment", segment), ("$id", image.ImageId));
                transaction.Commit();
            }
            written++;
            if (stopAfterSegments is not null && written >= stopAfterSegments) return new(false, written, 0, 0);
        }
        using (var connection = database.Open())
        {
            if ((long)ImageDatabase.Scalar(connection, "SELECT count(*) FROM vector_stats s WHERE indexed=0 AND segment IS NULL AND EXISTS(SELECT 1 FROM alias a WHERE a.image_id=s.image_id)")! != 0)
                throw new InvalidOperationException("MissingDescriptorCache: reimport the affected source images before building.");
        }
        if (stopBeforeMerge) return new(false, written, 0, 0);
        var sources = new List<DiskInvertedIndex>();
        string published = Path.Combine(directory, "invlists.bin");
        if (File.Exists(published)) sources.Add(DiskInvertedIndex.Open(published, profileId, quantizerHash));
        var segmentNames = new List<string>();
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT segment FROM vector_stats WHERE indexed=0 AND segment IS NOT NULL ORDER BY segment";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string name = reader.GetString(0);
                if (!IsSegmentName(name)) throw new InvalidDataException("Invalid segment checkpoint.");
                segmentNames.Add(name); sources.Add(DiskInvertedIndex.Open(Path.Combine(directory, name), profileId, quantizerHash));
            }
        }
        if (cleanBeforeMerge)
        {
            using var connection = database.Open();
            ImageDatabase.Execute(connection, "DELETE FROM vector WHERE image_id IN (SELECT image_id FROM vector_stats WHERE segment IS NOT NULL OR indexed=1)");
            ImageDatabase.Execute(connection, "VACUUM");
        }
        // A separate destination preserves the old index until every new bucket and checksum is durable.
        string candidate = Path.Combine(directory, "invlists.new.bin");
        DiskInvertedIndex.Write(candidate, profileId, quantizerHash, quantizer.Count,
            b => allowedImageIds is null ? DiskInvertedIndex.MergeBucket(sources, b) : DiskInvertedIndex.MergeBucket(sources, b).Where(v => allowedImageIds.Contains(v.ImageId)), cancellationToken);
        var validated = DiskInvertedIndex.Open(candidate, profileId, quantizerHash);
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(candidate, published, overwrite: true);
        using (var connection = database.Open()) ImageDatabase.Execute(connection, "UPDATE vector_stats SET indexed=1 WHERE segment IS NOT NULL");
        foreach (string name in segmentNames) File.Delete(Path.Combine(directory, name));
        return new(true, written, validated.ImageCount, validated.VectorCount);
    }

    private static bool IsSegmentName(string name) => name.Length == 44 && name.StartsWith("segment-", StringComparison.Ordinal)
        && name.EndsWith(".ivf", StringComparison.Ordinal) && name.AsSpan(8, 32).IndexOfAnyExcept("0123456789abcdef") < 0;

    /// <summary>Extracts a query with the stored profile and searches the published disk index.</summary>
    public SearchHit[] Search(GrayImage query, SearchOptions? options = null, CancellationToken cancellationToken = default)
        => SearchDescriptors(new OrbExtractor(_database.Profile).Extract(query, cancellationToken).Select(f => f.Descriptor).ToArray(), options, cancellationToken);

    /// <summary>Searches descriptors using nprobe IVF lists and a global per-descriptor Top-K.</summary>
    public SearchHit[] SearchDescriptors(BinaryDescriptor[] descriptors, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new SearchOptions { NProbe = Math.Min(16, Centers) }; options.Validate(Centers);
        if (descriptors.Length > 10064) throw new ArgumentOutOfRangeException(nameof(descriptors));
        var clock = Stopwatch.StartNew();
        var queriesByBucket = new Dictionary<int, List<int>>();
        for (int i = 0; i < descriptors.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (int bucket in _quantizer.Search(descriptors[i], options.NProbe, Math.Max(16, options.NProbe)))
            {
                if (!queriesByBucket.TryGetValue(bucket, out var queries)) queriesByBucket.Add(bucket, queries = []);
                queries.Add(i);
            }
        }
        long quantization = clock.ElapsedTicks;
        var neighbors = Enumerable.Range(0, descriptors.Length).Select(_ => new PriorityQueue<(long VectorId, long ImageId, int Distance), (int, long)>()).ToArray();
        long ioTicks = 0, computeTicks = 0;
        Parallel.ForEach(queriesByBucket, new ParallelOptions { MaxDegreeOfParallelism = options.ReadThreads, CancellationToken = cancellationToken }, pair =>
        {
            var (bucket, queries) = pair;
            cancellationToken.ThrowIfCancellationRequested();
            var local = queries.Select(_ => new PriorityQueue<(long VectorId, long ImageId, int Distance), (int, long)>()).ToArray();
            long records = 0;
            using var iterator = _index.ReadBucket(bucket).GetEnumerator();
            while (true)
            {
                long ioStart = Stopwatch.GetTimestamp(); bool available = iterator.MoveNext();
                Interlocked.Add(ref ioTicks, Stopwatch.GetTimestamp() - ioStart);
                if (!available) break;
                if ((records++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                long computeStart = Stopwatch.GetTimestamp();
                var vector = iterator.Current;
                BinaryDescriptor original = vector.Residual.Xor(_quantizer[bucket]);
                for (int j = 0; j < queries.Count; j++)
                {
                    int distance = descriptors[queries[j]].Distance(original);
                    var heap = local[j];
                    if (heap.Count < options.K) heap.Enqueue((vector.VectorId, vector.ImageId, distance), (-distance, -vector.VectorId));
                    else
                    {
                        var worst = heap.Peek();
                        if ((distance, vector.VectorId).CompareTo((worst.Distance, worst.VectorId)) < 0)
                        { heap.Dequeue(); heap.Enqueue((vector.VectorId, vector.ImageId, distance), (-distance, -vector.VectorId)); }
                    }
                }
                Interlocked.Add(ref computeTicks, Stopwatch.GetTimestamp() - computeStart);
            }
            for (int j = 0; j < queries.Count; j++)
            {
                var heap = neighbors[queries[j]];
                lock (heap)
                foreach (var item in local[j].UnorderedItems)
                {
                    var candidate = item.Element;
                    if (heap.Count < options.K) heap.Enqueue(candidate, item.Priority);
                    else if ((candidate.Distance, candidate.VectorId).CompareTo((heap.Peek().Distance, heap.Peek().VectorId)) < 0)
                    { heap.Dequeue(); heap.Enqueue(candidate, item.Priority); }
                }
            }
        });
        var scores = new Dictionary<long, List<float>>();
        foreach (var heap in neighbors) foreach (var item in heap.UnorderedItems)
        {
            var match = item.Element; if (match.Distance > options.MaxDistance) continue;
            if (!scores.TryGetValue(match.ImageId, out var list)) scores.Add(match.ImageId, list = []);
            list.Add(1 - match.Distance / 256f);
        }
        var ranked = scores.Select(p => (Id: p.Key, Score: options.CountScore ? p.Value.Count : 100 * MatchScoring.Wilson(CollectionsMarshal.AsSpan(p.Value))))
            .OrderByDescending(p => p.Score).ThenBy(p => p.Id).Take(options.Count).ToArray();
        var hits = new List<SearchHit>();
        using var connection = _database.Open();
        foreach (var (id, score) in ranked)
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT path FROM alias WHERE image_id=$id ORDER BY path";
            command.Parameters.AddWithValue("$id", id); using var reader = command.ExecuteReader(); var aliases = new List<string>();
            while (reader.Read()) aliases.Add(reader.GetString(0));
            hits.Add(new(id, score, aliases.ToArray()));
        }
        LastTiming = new(TimeSpan.FromSeconds((double)quantization / Stopwatch.Frequency), clock.Elapsed, descriptors.Length, queriesByBucket.Count,
            TimeSpan.FromSeconds((double)ioTicks / Stopwatch.Frequency), TimeSpan.FromSeconds((double)computeTicks / Stopwatch.Frequency));
        return hits.ToArray();
    }
}

/// <summary>Contains measured quantization and complete backend search duration.</summary>
public sealed record SearchTiming(TimeSpan Quantization, TimeSpan Total, int QueryDescriptors, int BucketsRead, TimeSpan Io, TimeSpan Compute);

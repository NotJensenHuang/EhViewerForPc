// Ported from imsearch/src/kmodes.rs, fixed at 2bf2546822ca (GPL-3.0).
namespace EhViewerForPc.ImageSearch;

/// <summary>Contains the binary cluster centers and training measurements.</summary>
public sealed record KModesResult(BinaryDescriptor[] Centers, int[] Frequencies, long Distance)
{
    /// <summary>Gets the population imbalance factor, or zero for an empty population.</summary>
    public double Imbalance => Frequencies.Sum() == 0 ? 0 : Frequencies.Sum(f => (double)f * f) * Frequencies.Length / Math.Pow(Frequencies.Sum(), 2);
}

/// <summary>Trains bitwise-majority binary centers using the upstream one- or two-level method.</summary>
public static class BinaryKModes
{
    /// <summary>Trains a deterministic binary KModes model.</summary>
    public static KModesResult Train(BinaryDescriptor[] data, int centers, int iterations = 20, bool plusPlus = false,
        bool twoLevel = true, int seed = 20261112, CancellationToken cancellationToken = default, int workers = 1)
    {
        if (centers < 1 || centers > data.Length || iterations is < 1 or > 1000 || workers is < 1 or > IndexBuildOptions.MaximumWorkers)
            throw new ArgumentOutOfRangeException(nameof(centers));
        if (!twoLevel) return Single(data, centers, iterations, plusPlus, new Random(seed), cancellationToken, workers);
        if (data.Length < 30L * centers) throw new InvalidOperationException("InsufficientFeatures: two-level training requires D >= 30*K.");
        int firstCount = (int)Math.Sqrt(centers);
        var rng = new Random(seed);
        var first = Single(data.Take(Math.Min(data.Length, firstCount * 1024)).ToArray(), firstCount, iterations, plusPlus, rng, cancellationToken, workers);
        var assignment = Assign(data, first.Centers, cancellationToken, workers);
        var groups = Enumerable.Range(0, firstCount).Select(_ => new List<BinaryDescriptor>()).ToArray();
        for (int i = 0; i < data.Length; i++) groups[assignment.Ids[i]].Add(data[i]);
        var output = new List<BinaryDescriptor>(centers);
        var frequencies = new List<int>(centers);
        long distance = 0, cumulative = 0;
        int allocated = 0;
        foreach (var group in groups)
        {
            cumulative += group.Count;
            int target = (int)(cumulative * centers / data.Length);
            int k = target - allocated; allocated = target;
            if (k == 0) continue;
            var trained = Single(group.ToArray(), k, iterations, plusPlus, rng, cancellationToken, workers);
            output.AddRange(trained.Centers); frequencies.AddRange(trained.Frequencies); distance += trained.Distance;
        }
        if (output.Count != centers) throw new InvalidOperationException("Invalid two-level allocation.");
        return new(output.ToArray(), frequencies.ToArray(), distance);
    }

    private static KModesResult Single(BinaryDescriptor[] data, int k, int iterations, bool plusPlus, Random random, CancellationToken ct, int workers)
    {
        var centers = new BinaryDescriptor[k];
        if (plusPlus)
        {
            centers[0] = data[random.Next(data.Length)];
            var nearest = Enumerable.Repeat(257, data.Length).ToArray();
            for (int c = 1; c < k; c++)
            {
                ct.ThrowIfCancellationRequested();
                long total = 0;
                for (int i = 0; i < data.Length; i++) { nearest[i] = Math.Min(nearest[i], data[i].Distance(centers[c - 1])); total += nearest[i]; }
                // All-equal data has zero total weight. Keep training well-defined.
                if (total == 0) { centers[c] = data[random.Next(data.Length)]; continue; }
                long pick = random.NextInt64(total);
                for (int i = 0; i < data.Length; i++) { pick -= nearest[i]; if (pick < 0) { centers[c] = data[i]; break; } }
            }
        }
        else
        {
            int[] permutation = Enumerable.Range(0, data.Length).ToArray();
            for (int c = 0; c < k; c++) { int j = random.Next(c, data.Length); (permutation[c], permutation[j]) = (permutation[j], permutation[c]); centers[c] = data[permutation[c]]; }
        }
        long previous = long.MaxValue;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            var assigned = Assign(data, centers, ct, workers);
            if (assigned.Distance >= previous) break;
            previous = assigned.Distance;
            int[] counts = new int[k];
            foreach (int id in assigned.Ids) counts[id]++;
            int[] starts = new int[k + 1];
            for (int c = 0; c < k; c++) starts[c + 1] = starts[c] + counts[c];
            int[] offsets = (int[])starts.Clone(), ordered = new int[data.Length];
            for (int i = 0; i < data.Length; i++) ordered[offsets[assigned.Ids[i]]++] = i;
            // 每个并行上下文独占直方图；中心序号、平票规则和随机初始化顺序保持不变。
            if (workers == 1 || k < 8)
            {
                int[] bits = new int[256];
                for (int c = 0; c < k; c++) UpdateCenter(c, bits, data, ordered, starts, counts, centers, ct);
            }
            else
                Parallel.For(0, k, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
                    () => new int[256], (c, _, bits) => { UpdateCenter(c, bits, data, ordered, starts, counts, centers, ct); return bits; }, _ => { });
        }
        var final = Assign(data, centers, ct, workers);
        var histogram = new int[k]; foreach (int id in final.Ids) histogram[id]++;
        return new(centers, histogram, final.Distance);
    }

    private static void UpdateCenter(int c, int[] bits, BinaryDescriptor[] data, int[] ordered, int[] starts, int[] counts,
        BinaryDescriptor[] centers, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Array.Clear(bits);
        for (int p = starts[c]; p < starts[c + 1]; p++)
        {
            if ((p & 4095) == 0) ct.ThrowIfCancellationRequested();
            for (int w = 0; w < 4; w++)
            {
                ulong value = data[ordered[p]].Word(w);
                while (value != 0)
                {
                    int bit = System.Numerics.BitOperations.TrailingZeroCount(value);
                    bits[w * 64 + bit]++; value &= value - 1;
                }
            }
        }
        Span<ulong> words = stackalloc ulong[4];
        words.Clear();
        for (int bit = 0; bit < 256; bit++) if (bits[bit] > counts[c] / 2) words[bit / 64] |= 1UL << (bit % 64);
        centers[c] = new(words[0], words[1], words[2], words[3]);
    }

    private static (int[] Ids, long Distance) Assign(BinaryDescriptor[] data, BinaryDescriptor[] centers, CancellationToken ct, int workers)
    {
        var ids = new int[data.Length];
        int partitions = Math.Min(workers, Math.Max(1, data.Length / 1024));
        var distances = new long[partitions];
        void AssignRange(int partition)
        {
            int start = (int)((long)data.Length * partition / partitions);
            int end = (int)((long)data.Length * (partition + 1) / partitions);
            long total = 0;
            for (int i = start; i < end; i++)
            {
                if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
                int distance = int.MaxValue;
                for (int c = 0; c < centers.Length; c++)
                {
                    int d = data[i].Distance(centers[c]);
                    if (d < distance) { ids[i] = c; distance = d; }
                }
                total += distance;
            }
            distances[partition] = total;
        }
        ct.ThrowIfCancellationRequested();
        if (partitions == 1) AssignRange(0);
        else Parallel.For(0, partitions, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct }, AssignRange);
        long total = 0;
        foreach (long distance in distances) total += distance;
        return (ids, total);
    }
}

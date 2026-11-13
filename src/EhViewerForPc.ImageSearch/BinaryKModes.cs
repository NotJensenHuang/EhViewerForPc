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
        bool twoLevel = true, int seed = 20261112, CancellationToken cancellationToken = default)
    {
        if (centers < 1 || centers > data.Length || iterations is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(centers));
        if (!twoLevel) return Single(data, centers, iterations, plusPlus, new Random(seed), cancellationToken);
        if (data.Length < 30L * centers) throw new InvalidOperationException("InsufficientFeatures: two-level training requires D >= 30*K.");
        int firstCount = (int)Math.Sqrt(centers);
        var rng = new Random(seed);
        var first = Single(data.Take(Math.Min(data.Length, firstCount * 1024)).ToArray(), firstCount, iterations, plusPlus, rng, cancellationToken);
        var assignment = Assign(data, first.Centers, cancellationToken);
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
            var trained = Single(group.ToArray(), k, iterations, plusPlus, rng, cancellationToken);
            output.AddRange(trained.Centers); frequencies.AddRange(trained.Frequencies); distance += trained.Distance;
        }
        if (output.Count != centers) throw new InvalidOperationException("Invalid two-level allocation.");
        return new(output.ToArray(), frequencies.ToArray(), distance);
    }

    private static KModesResult Single(BinaryDescriptor[] data, int k, int iterations, bool plusPlus, Random random, CancellationToken ct)
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
        Span<ulong> words = stackalloc ulong[4];
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            var assigned = Assign(data, centers, ct);
            if (assigned.Distance >= previous) break;
            previous = assigned.Distance;
            int[] counts = new int[k];
            foreach (int id in assigned.Ids) counts[id]++;
            int[] starts = new int[k + 1];
            for (int c = 0; c < k; c++) starts[c + 1] = starts[c] + counts[c];
            int[] offsets = (int[])starts.Clone(), ordered = new int[data.Length];
            for (int i = 0; i < data.Length; i++) ordered[offsets[assigned.Ids[i]]++] = i;
            // One reusable 256-bit histogram avoids a K*256 allocation at large K.
            int[] bits = new int[256];
            for (int c = 0; c < k; c++)
            {
                ct.ThrowIfCancellationRequested(); Array.Clear(bits);
                for (int p = starts[c]; p < starts[c + 1]; p++)
                {
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
                words.Clear();
                for (int bit = 0; bit < 256; bit++) if (bits[bit] > counts[c] / 2) words[bit / 64] |= 1UL << (bit % 64);
                centers[c] = new(words[0], words[1], words[2], words[3]);
            }
        }
        var final = Assign(data, centers, ct);
        var histogram = new int[k]; foreach (int id in final.Ids) histogram[id]++;
        return new(centers, histogram, final.Distance);
    }

    private static (int[] Ids, long Distance) Assign(BinaryDescriptor[] data, BinaryDescriptor[] centers, CancellationToken ct)
    {
        var ids = new int[data.Length]; long total = 0;
        for (int i = 0; i < data.Length; i++)
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
        return (ids, total);
    }
}

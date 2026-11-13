// Ported from lolishinshi/imsearch 2bf2546822ca (GPL-3.0). See tools/imsearch/NOTICE.md.
using System.Buffers.Binary;
using System.Numerics;

namespace EhViewerForPc.ImageSearch;

/// <summary>Stores an ORB descriptor as four little-endian words.</summary>
public readonly record struct BinaryDescriptor(ulong A, ulong B, ulong C, ulong D)
{
    /// <summary>Reads exactly 32 descriptor bytes.</summary>
    public static BinaryDescriptor Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32) throw new ArgumentException("A descriptor needs 32 bytes.", nameof(bytes));
        return new(BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]));
    }

    /// <summary>Writes the 32 descriptor bytes.</summary>
    public void Write(Span<byte> bytes)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, A);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], B);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], C);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], D);
    }

    /// <summary>Counts differing bits.</summary>
    public int Distance(BinaryDescriptor other) => BitOperations.PopCount(A ^ other.A) + BitOperations.PopCount(B ^ other.B)
        + BitOperations.PopCount(C ^ other.C) + BitOperations.PopCount(D ^ other.D);

    /// <summary>Computes a reversible centroid residual.</summary>
    public BinaryDescriptor Xor(BinaryDescriptor other) => new(A ^ other.A, B ^ other.B, C ^ other.C, D ^ other.D);

    internal ulong Word(int i) => i switch { 0 => A, 1 => B, 2 => C, _ => D };
}

/// <summary>Provides the upstream continuous-score Wilson lower bound.</summary>
public static class MatchScoring
{
    /// <summary>Scores matches using their mean and population variance, with z=2.326.</summary>
    public static float Wilson(ReadOnlySpan<float> scores)
    {
        if (scores.IsEmpty) return 0;
        float n = scores.Length, sum = 0;
        foreach (float value in scores) sum += value;
        float mean = sum / n, variance = 0;
        foreach (float value in scores) variance += (mean - value) * (mean - value);
        variance /= n;
        const float z = 2.326f;
        return (mean + z * z / (2 * n) - z / (2 * n) * MathF.Sqrt(4 * n * variance + z * z)) / (1 + z * z / n);
    }
}

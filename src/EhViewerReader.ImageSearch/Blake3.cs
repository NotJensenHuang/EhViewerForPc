// BLAKE3 unkeyed 256-bit mode, following the BLAKE3 specification.
using System.Buffers.Binary;
using System.Numerics;

namespace EhViewerReader.ImageSearch;

/// <summary>Computes BLAKE3 without a Rust or native cryptography dependency.</summary>
public static class Blake3
{
    private static readonly uint[] Iv = [0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19];
    private static readonly int[] Permutation = [2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8];
    private sealed record Output(uint[] Cv, uint[] Block, ulong Counter, uint Length, uint Flags)
    {
        internal uint[] Chaining() => Compress(Cv, Block, Counter, Length, Flags)[..8];
    }

    /// <summary>Computes the 32-byte unkeyed digest.</summary>
    public static byte[] Hash(ReadOnlySpan<byte> input)
    {
        var stack = new Stack<uint[]>();
        ulong chunks = 0;
        while (input.Length > 1024)
        {
            uint[] cv = Chunk(input[..1024], chunks).Chaining();
            ulong total = ++chunks;
            while ((total & 1) == 0) { cv = Parent(stack.Pop(), cv).Chaining(); total >>= 1; }
            stack.Push(cv);
            input = input[1024..];
        }
        Output output = Chunk(input, chunks);
        while (stack.Count > 0) output = Parent(stack.Pop(), output.Chaining());
        uint[] root = Compress(output.Cv, output.Block, 0, output.Length, output.Flags | 8);
        byte[] digest = new byte[32];
        for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(i * 4), root[i]);
        return digest;
    }

    private static Output Parent(uint[] left, uint[] right) => new(Iv, [..left, ..right], 0, 64, 4);
    private static Output Chunk(ReadOnlySpan<byte> bytes, ulong counter)
    {
        uint[] cv = Iv;
        int blockNumber = 0;
        Span<byte> buffer = stackalloc byte[64];
        while (true)
        {
            int length = Math.Min(64, bytes.Length);
            buffer.Clear();
            bytes[..length].CopyTo(buffer);
            uint[] words = new uint[16];
            for (int i = 0; i < 16; i++) words[i] = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(i * 4)..]);
            uint flags = (blockNumber == 0 ? 1u : 0u) | (bytes.Length <= 64 ? 2u : 0u);
            var output = new Output(cv, words, counter, (uint)length, flags);
            if (bytes.Length <= 64) return output;
            cv = output.Chaining();
            bytes = bytes[64..];
            blockNumber++;
        }
    }

    private static uint[] Compress(uint[] cv, uint[] message, ulong counter, uint length, uint flags)
    {
        uint[] v = [..cv, Iv[0], Iv[1], Iv[2], Iv[3], (uint)counter, (uint)(counter >> 32), length, flags];
        uint[] m = (uint[])message.Clone();
        for (int round = 0; round < 7; round++)
        {
            G(v, 0, 4, 8, 12, m[0], m[1]); G(v, 1, 5, 9, 13, m[2], m[3]);
            G(v, 2, 6, 10, 14, m[4], m[5]); G(v, 3, 7, 11, 15, m[6], m[7]);
            G(v, 0, 5, 10, 15, m[8], m[9]); G(v, 1, 6, 11, 12, m[10], m[11]);
            G(v, 2, 7, 8, 13, m[12], m[13]); G(v, 3, 4, 9, 14, m[14], m[15]);
            if (round < 6) { uint[] next = new uint[16]; for (int i = 0; i < 16; i++) next[i] = m[Permutation[i]]; m = next; }
        }
        for (int i = 0; i < 8; i++) { v[i] ^= v[i + 8]; v[i + 8] ^= cv[i]; }
        return v;
    }

    private static void G(uint[] v, int a, int b, int c, int d, uint x, uint y)
    {
        unchecked
        {
            v[a] += v[b] + x; v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
            v[c] += v[d]; v[b] = BitOperations.RotateRight(v[b] ^ v[c], 12);
            v[a] += v[b] + y; v[d] = BitOperations.RotateRight(v[d] ^ v[a], 8);
            v[c] += v[d]; v[b] = BitOperations.RotateRight(v[b] ^ v[c], 7);
        }
    }
}

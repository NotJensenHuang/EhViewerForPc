using System.IO.Compression;
using System.Security.Cryptography;
using System.Buffers;
using System.Buffers.Binary;

namespace EhViewerForPc.ImageSearch;

/// <summary>Stores a vector's stable identity and centroid-XOR descriptor.</summary>
public readonly record struct IndexVector(long VectorId, long ImageId, BinaryDescriptor Residual);

/// <summary>Reads and writes checksummed, compressed inverted lists without loading the whole index.</summary>
public sealed class DiskInvertedIndex
{
    private readonly record struct Bucket(long Offset, long Count, long Length);
    private readonly Bucket[] _buckets;
    private readonly string _path;
    private readonly bool _allowLinkedPublication;

    private DiskInvertedIndex(string path, string profileId, string quantizerHash, bool allowLinkedPublication,
        Action<long, long>? progress = null, CancellationToken token = default)
    {
        _path = path;
        _allowLinkedPublication = allowLinkedPublication;
        using var reader = CheckedFile.Open(path, allowLinkedPublication, progress, token);
        if (reader.ReadUInt64() != 0x3146564943524d49UL || CheckedFile.ReadShortString(reader) != profileId || CheckedFile.ReadShortString(reader) != quantizerHash)
            throw new InvalidDataException("IVF format, profile or quantizer mismatch; rebuild required.");
        int count = reader.ReadInt32();
        ImageCount = reader.ReadInt64(); VectorCount = reader.ReadInt64();
        if (count is < 1 or > 1048576 || ImageCount < 0 || VectorCount < 0 || ImageCount > VectorCount)
            throw new InvalidDataException("Invalid IVF dimensions.");
        long start = checked(reader.BaseStream.Position + 24L * count), total = 0, end = start;
        _buckets = new Bucket[count];
        for (int i = 0; i < count; i++)
        {
            long offset = reader.ReadInt64(), records = reader.ReadInt64(), length = reader.ReadInt64();
            if (offset != end || records < 0 || records > 1_000_000_000 || length < 0 || length > reader.BaseStream.Length - 32 - offset
                || (records == 0) != (length == 0)) throw new InvalidDataException("Invalid IVF bucket bounds.");
            _buckets[i] = new(offset, records, length); total = checked(total + records); end = checked(offset + length);
        }
        if (total != VectorCount || end != reader.BaseStream.Length - 32) throw new InvalidDataException("Invalid IVF record total.");
    }

    /// <summary>Gets the number of inverted lists.</summary>
    public int BucketCount => _buckets.Length;
    /// <summary>Gets the unique image count recorded in this index.</summary>
    public long ImageCount { get; }
    /// <summary>Gets the stored descriptor count.</summary>
    public long VectorCount { get; }

    /// <summary>Validates and opens a read-only index.</summary>
    public static DiskInvertedIndex Open(string path, string profileId, string quantizerHash, bool allowLinkedPublication = false) => new(path, profileId, quantizerHash, allowLinkedPublication);

    internal static DiskInvertedIndex OpenWithProgress(string path, string profileId, string quantizerHash,
        Action<long, long>? progress, CancellationToken token, bool allowLinkedPublication = false) => new(path, profileId, quantizerHash, allowLinkedPublication, progress, token);

    /// <summary>Computes a quantizer file's identity.</summary>
    public static string HashQuantizer(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }

    /// <summary>Writes an index atomically, accepting a streaming source for each bucket.</summary>
    public static void Write(string path, string profileId, string quantizerHash, int buckets,
        Func<int, IEnumerable<IndexVector>> readBucket, CancellationToken cancellationToken = default)
        => WriteWithProgress(path, profileId, quantizerHash, buckets, readBucket, null, null, cancellationToken);

    internal static void WriteWithProgress(string path, string profileId, string quantizerHash, int buckets,
        Func<int, IEnumerable<IndexVector>> readBucket, Action<long, long>? bucketsProgress,
        Action<long, long>? checksumProgress, CancellationToken cancellationToken)
    {
        if (buckets is < 1 or > 1048576) throw new ArgumentOutOfRangeException(nameof(buckets));
        bucketsProgress?.Invoke(0, buckets);
        CheckedFile.Write(path, writer =>
        {
            writer.Write(0x3146564943524d49UL); writer.Write(profileId); writer.Write(quantizerHash); writer.Write(buckets);
            long countsPosition = writer.BaseStream.Position;
            writer.Write(0L); writer.Write(0L);
            long tablePosition = writer.BaseStream.Position;
            for (int i = 0; i < buckets; i++) { writer.Write(0L); writer.Write(0L); writer.Write(0L); }
            var table = new Bucket[buckets]; var images = new HashSet<long>(); long total = 0;
            for (int i = 0; i < buckets; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long offset = writer.BaseStream.Position, count = 0, previous = 0;
                using (var iterator = readBucket(i).GetEnumerator())
                {
                    if (iterator.MoveNext())
                    {
                        using var deflate = new DeflateStream(writer.BaseStream, CompressionLevel.Fastest, leaveOpen: true);
                        using var records = new BinaryWriter(deflate, System.Text.Encoding.UTF8, leaveOpen: true);
                        do
                        {
                            if ((count & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                            IndexVector vector = iterator.Current;
                            if (vector.VectorId <= previous || vector.ImageId < 1) throw new InvalidDataException("IVF vectors must be unique and ordered.");
                            previous = vector.VectorId; records.Write(vector.VectorId); records.Write(vector.ImageId); CheckedFile.WriteDescriptor(records, vector.Residual);
                            images.Add(vector.ImageId); count++;
                        } while (iterator.MoveNext());
                    }
                }
                table[i] = new(offset, count, writer.BaseStream.Position - offset); total += count;
                bucketsProgress?.Invoke(i + 1, buckets);
            }
            long end = writer.BaseStream.Position;
            writer.BaseStream.Position = countsPosition; writer.Write((long)images.Count); writer.Write(total);
            writer.BaseStream.Position = tablePosition;
            foreach (var bucket in table) { writer.Write(bucket.Offset); writer.Write(bucket.Count); writer.Write(bucket.Length); }
            writer.BaseStream.Position = end;
        }, checksumProgress, cancellationToken);
    }

    /// <summary>Streams a bucket's records and verifies decompressed size and ordering.</summary>
    public IEnumerable<IndexVector> ReadBucket(int bucket)
    {
        var info = _buckets[bucket]; if (info.Count == 0) yield break;
        using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | (_allowLinkedPublication ? FileShare.Delete : 0), 4096, FileOptions.RandomAccess);
        file.Position = info.Offset;
        using var limited = new LimitedStream(file, info.Length);
        using var deflate = new DeflateStream(limited, CompressionMode.Decompress);
        long previous = 0;
        // 每条记录固定 48 字节；成批解压，避免对每个 UInt64 单独进入解压器。
        byte[] buffer = ArrayPool<byte>.Shared.Rent(48 * 1024);
        try
        {
            for (long i = 0; i < info.Count;)
            {
                int count = (int)Math.Min(1024, info.Count - i);
                deflate.ReadExactly(buffer.AsSpan(0, count * 48));
                for (int j = 0; j < count; j++)
                {
                    var vector = ReadVector(buffer, j * 48);
                    if (vector.VectorId <= previous || vector.ImageId < 1) throw new InvalidDataException("Invalid IVF vector identity.");
                    previous = vector.VectorId;
                    yield return vector;
                }
                i += count;
            }
            if (deflate.ReadByte() != -1) throw new InvalidDataException("IVF decompression exceeds recorded size.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static IndexVector ReadVector(byte[] buffer, int offset)
    {
        ReadOnlySpan<byte> record = buffer.AsSpan(offset, 48);
        return new(BinaryPrimitives.ReadInt64LittleEndian(record), BinaryPrimitives.ReadInt64LittleEndian(record[8..]),
            new(BinaryPrimitives.ReadUInt64LittleEndian(record[16..]), BinaryPrimitives.ReadUInt64LittleEndian(record[24..]),
                BinaryPrimitives.ReadUInt64LittleEndian(record[32..]), BinaryPrimitives.ReadUInt64LittleEndian(record[40..])));
    }

    /// <summary>Merges sorted lists with idempotent vector replay, retaining only one copy of each vector.</summary>
    public static IEnumerable<IndexVector> MergeBucket(IReadOnlyList<DiskInvertedIndex> indexes, int bucket)
    {
        var iterators = indexes.Select(i => i.ReadBucket(bucket).GetEnumerator()).ToArray();
        try
        {
            var queue = new PriorityQueue<int, long>();
            for (int i = 0; i < iterators.Length; i++) if (iterators[i].MoveNext()) queue.Enqueue(i, iterators[i].Current.VectorId);
            IndexVector? previous = null;
            while (queue.TryDequeue(out int i, out _))
            {
                IndexVector current = iterators[i].Current;
                if (previous is null || current.VectorId != previous.Value.VectorId) { yield return current; previous = current; }
                else if (current != previous.Value) throw new InvalidDataException("Conflicting replayed IVF vector.");
                if (iterators[i].MoveNext()) queue.Enqueue(i, iterators[i].Current.VectorId);
            }
        }
        finally { foreach (var iterator in iterators) iterator.Dispose(); }
    }

    private sealed class LimitedStream(Stream source, long remaining) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) { if (remaining == 0) return 0; int count = source.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]); remaining -= count; return count; }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

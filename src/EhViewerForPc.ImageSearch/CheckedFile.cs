using System.Security.Cryptography;

namespace EhViewerForPc.ImageSearch;

internal static class CheckedFile
{
    internal static void Write(string path, Action<BinaryWriter> write)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true);
                write(writer);
                writer.Flush();
                long length = file.Position;
                file.Position = 0;
                byte[] hash = SHA256.HashData(file);
                file.Position = length;
                file.Write(hash);
                file.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static BinaryReader Open(string path, bool allowDelete = false)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | (allowDelete ? FileShare.Delete : 0));
        try
        {
            if (file.Length < 40) throw new InvalidDataException("Truncated search file.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[65536];
            long remaining = file.Length - 32;
            while (remaining > 0)
            {
                int count = file.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (count == 0) throw new EndOfStreamException();
                hash.AppendData(buffer, 0, count);
                remaining -= count;
            }
            Span<byte> expected = stackalloc byte[32];
            file.ReadExactly(expected);
            if (!CryptographicOperations.FixedTimeEquals(expected, hash.GetHashAndReset()))
                throw new InvalidDataException("Search file checksum mismatch.");
            file.Position = 0;
            return new BinaryReader(file);
        }
        catch { file.Dispose(); throw; }
    }

    internal static BinaryDescriptor ReadDescriptor(BinaryReader reader) => new(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64());
    internal static string ReadShortString(BinaryReader reader)
    {
        int length = reader.Read7BitEncodedInt();
        if (length is < 1 or > 128) throw new InvalidDataException("Invalid search metadata length.");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return new System.Text.UTF8Encoding(false, true).GetString(bytes);
    }
    internal static void WriteDescriptor(BinaryWriter writer, BinaryDescriptor descriptor)
    {
        writer.Write(descriptor.A); writer.Write(descriptor.B); writer.Write(descriptor.C); writer.Write(descriptor.D);
    }

    internal static void RequireEnd(BinaryReader reader)
    {
        if (reader.BaseStream.Position != reader.BaseStream.Length - 32) throw new InvalidDataException("Unexpected trailing data.");
    }
}

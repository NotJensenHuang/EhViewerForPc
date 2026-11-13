// Port of imsearch's db/imdb metadata and descriptor cache, with a versioned C# schema.
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EhViewerForPc.ImageSearch;

/// <summary>Chooses exact or perceptual image deduplication.</summary>
public enum ImageHashMode { Blake3, DHash }

/// <summary>Contains the terminal status for one imported input.</summary>
public sealed record ImportEntry(string Alias, string Status, int FeatureCount, string? ErrorCode = null);

/// <summary>Contains a cached image's descriptor range.</summary>
public sealed record CachedImage(long ImageId, long FirstVectorId, BinaryDescriptor[] Descriptors);

/// <summary>Stores backend metadata and descriptors with explicit transactions and schema checks.</summary>
public sealed class ImageDatabase : IDisposable
{
    private readonly string _connectionString;
    private readonly FileStream? _writerLease;
    private SharedFeatureCache? _sharedFeatures;
    private bool _disposed;
    private BinaryHnsw? _dHashGraph;
    private readonly List<long> _dHashIds = [];

    /// <summary>Initializes or opens a backend database, optionally acquiring its exclusive writer lease.</summary>
    public ImageDatabase(string directory, bool writable = false, FeatureProfile? profile = null, ImageHashMode? hashMode = null)
    {
        DirectoryPath = Path.GetFullPath(directory);
        if (writable) Directory.CreateDirectory(DirectoryPath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(DirectoryPath, "images.db"),
            Mode = writable ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        try
        {
            if (writable) _writerLease = new FileStream(Path.Combine(DirectoryPath, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var connection = Open();
            long version = (long)Scalar(connection, "PRAGMA user_version")!;
            if (version == 0 && writable)
            {
                var chosen = profile ?? new FeatureProfile(); chosen.Validate();
                using var tx = connection.BeginTransaction();
                Execute(connection, """
                    CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    CREATE TABLE image(id INTEGER PRIMARY KEY, hash BLOB NOT NULL UNIQUE, feature_count INTEGER NOT NULL, state TEXT NOT NULL);
                    CREATE TABLE alias(path TEXT PRIMARY KEY, image_id INTEGER NOT NULL REFERENCES image(id));
                    CREATE INDEX alias_image ON alias(image_id);
                    CREATE TABLE vector(image_id INTEGER PRIMARY KEY REFERENCES image(id), descriptors BLOB NOT NULL);
                    CREATE TABLE vector_stats(image_id INTEGER PRIMARY KEY REFERENCES image(id), first_id INTEGER NOT NULL UNIQUE,
                        vector_count INTEGER NOT NULL, segment TEXT, indexed INTEGER NOT NULL DEFAULT 0);
                    PRAGMA user_version=1;
                    """, tx);
                foreach (var pair in new Dictionary<string, string> { ["profile"] = JsonSerializer.Serialize(chosen, SearchJsonContext.Default.FeatureProfile),
                    ["hashMode"] = (hashMode ?? ImageHashMode.Blake3).ToString(), ["format"] = "imsearch-csharp/1", ["nextVectorId"] = "1" })
                    Execute(connection, "INSERT INTO meta(key,value) VALUES($key,$value)", tx, ("$key", pair.Key), ("$value", pair.Value));
                tx.Commit(); version = 1;
            }
            if (version != 1) throw new InvalidDataException("Unsupported database schema; rebuild required.");
            string format = (string)Scalar(connection, "SELECT value FROM meta WHERE key='format'")!;
            if (format != "imsearch-csharp/1") throw new InvalidDataException("Unsupported backend format.");
            Profile = JsonSerializer.Deserialize((string)Scalar(connection, "SELECT value FROM meta WHERE key='profile'")!, SearchJsonContext.Default.FeatureProfile)
                ?? throw new InvalidDataException("Missing feature profile.");
            Profile.Validate();
            HashMode = Enum.Parse<ImageHashMode>((string)Scalar(connection, "SELECT value FROM meta WHERE key='hashMode'")!);
            if (profile is not null && profile.Id != Profile.Id || hashMode is not null && hashMode != HashMode)
                throw new InvalidDataException("Backend profile or hash mode mismatch.");
            IsWritable = writable;
            if (writable && HashMode == ImageHashMode.DHash)
            {
                _dHashGraph = new BinaryHnsw();
                using var command = connection.CreateCommand(); command.CommandText = "SELECT id,hash FROM image ORDER BY id";
                using var reader = command.ExecuteReader();
                while (reader.Read()) { _dHashIds.Add(reader.GetInt64(0)); _dHashGraph.Add(new(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian((byte[])reader[1]), 0, 0, 0)); }
            }
        }
        catch { _writerLease?.Dispose(); throw; }
    }

    /// <summary>Gets the absolute backend directory.</summary>
    public string DirectoryPath { get; }
    /// <summary>Gets the stored extraction profile.</summary>
    public FeatureProfile Profile { get; }
    /// <summary>Gets the stored hash algorithm.</summary>
    public ImageHashMode HashMode { get; }
    /// <summary>Gets whether the writer lease is held.</summary>
    public bool IsWritable { get; }
    /// <summary>Gets the number of images in the published index.</summary>
    public long IndexedCount { get { using var c = Open(); return (long)Scalar(c, "SELECT count(*) FROM vector_stats WHERE indexed=1")!; } }

    /// <summary>Gets whether a currently referenced image still needs index publication.</summary>
    public bool HasPendingImages
    {
        get
        {
            using var connection = Open();
            return (long)Scalar(connection, "SELECT EXISTS(SELECT 1 FROM vector_stats s WHERE s.indexed=0 AND EXISTS(SELECT 1 FROM alias a WHERE a.image_id=s.image_id))")! != 0;
        }
    }

    internal SqliteConnection Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connection = new SqliteConnection(_connectionString); connection.Open();
        Execute(connection, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        return connection;
    }

    internal static object? Scalar(SqliteConnection c, string sql, params (string, object)[] values)
    { using var command = c.CreateCommand(); command.CommandText = sql; foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value); return command.ExecuteScalar(); }
    internal static void Execute(SqliteConnection c, string sql, SqliteTransaction? transaction = null, params (string, object)[] values)
    { using var command = c.CreateCommand(); command.CommandText = sql; command.Transaction = transaction; foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value); command.ExecuteNonQuery(); }
    internal void RequireWriter() { if (!IsWritable) throw new InvalidOperationException("A backend writer lease is required."); }

    /// <summary>Validates a relative logical input name without resolving it as an output path.</summary>
    public static void ValidateAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias) || alias.Length > 1024 || Path.IsPathRooted(alias) || alias.Contains('\\')
            || alias.Contains(':') || alias.Any(char.IsControl) || alias.Split('/').Any(p => p is "" or "." or ".."))
            throw new InvalidDataException("Invalid input alias.");
    }

    /// <summary>Commits one image, its aliases and descriptors atomically, with idempotent replay.</summary>
    public ImportEntry Add(string alias, byte[] hash, BinaryDescriptor[] descriptors, int minFeatures = 30, bool append = true, int duplicateDistance = 0, bool overwrite = false)
    {
        RequireWriter(); ValidateAlias(alias);
        if (minFeatures < 0 || hash.Length != (HashMode == ImageHashMode.Blake3 ? 32 : 8) || duplicateDistance is < 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(hash));
        using var c = Open(); using var tx = c.BeginTransaction();
        object? storedMinimum = Scalar(c, "SELECT value FROM meta WHERE key='minFeatures'");
        if (storedMinimum is string stored && int.Parse(stored, System.Globalization.CultureInfo.InvariantCulture) != minFeatures)
            throw new InvalidDataException("Import threshold mismatch; use a new generation.");
        if (storedMinimum is null) Execute(c, "INSERT INTO meta(key,value) VALUES('minFeatures',$value)", tx, ("$value", minFeatures.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        object? found = Scalar(c, "SELECT id FROM image WHERE hash=$hash", ("$hash", hash));
        bool exactDuplicate = found is not null;
        if (found is null && HashMode == ImageHashMode.DHash && duplicateDistance > 0)
        {
            if (_dHashGraph!.Count > 0)
            {
                var descriptor = new BinaryDescriptor(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash), 0, 0, 0);
                int closest = _dHashGraph.Search(descriptor, 1)[0];
                if (descriptor.Distance(_dHashGraph[closest]) <= duplicateDistance) found = _dHashIds[closest];
            }
        }
        if (found is long existing)
        {
            bool wasUnreferenced = (long)Scalar(c, "SELECT count(*) FROM alias WHERE image_id=$id", ("$id", existing))! == 0;
            long? aliasOwner = Scalar(c, "SELECT image_id FROM alias WHERE path=$path", ("$path", alias)) as long?;
            if (aliasOwner is not null && aliasOwner != existing) throw new InvalidDataException("Alias already belongs to different content.");
            if (overwrite) Execute(c, "DELETE FROM alias WHERE image_id=$id", tx, ("$id", existing));
            if (append || overwrite || aliasOwner == existing) Execute(c, "INSERT OR IGNORE INTO alias(path,image_id) VALUES($path,$id)", tx, ("$path", alias), ("$id", existing));
            int count = Convert.ToInt32(Scalar(c, "SELECT feature_count FROM image WHERE id=$id", ("$id", existing)));
            if (exactDuplicate && count > minFeatures && Scalar(c, "SELECT image_id FROM vector WHERE image_id=$id", ("$id", existing)) is null)
            {
                if (descriptors.Length != count) throw new InvalidDataException("Descriptor replay count differs from stored profile.");
                byte[] encoded = EncodeDescriptors(descriptors);
                if (_sharedFeatures is null)
                    Execute(c, "INSERT INTO vector(image_id,descriptors) VALUES($id,$bytes)", tx, ("$id", existing), ("$bytes", encoded));
                else
                    _sharedFeatures.Store(Profile.Id, hash, encoded);
            }
            if (wasUnreferenced && exactDuplicate && count > minFeatures)
                Execute(c, "UPDATE vector_stats SET indexed=0,segment=NULL WHERE image_id=$id", tx, ("$id", existing));
            tx.Commit();
            return new(alias, count <= minFeatures ? "skipped_low_features" : "already_present", count);
        }
        if (Scalar(c, "SELECT image_id FROM alias WHERE path=$path", ("$path", alias)) is not null) throw new InvalidDataException("Alias already belongs to different content.");
        bool skipped = descriptors.Length <= minFeatures;
        Execute(c, "INSERT INTO image(hash,feature_count,state) VALUES($hash,$count,$state)", tx,
            ("$hash", hash), ("$count", descriptors.Length), ("$state", skipped ? "skipped" : "imported"));
        long id = (long)Scalar(c, "SELECT last_insert_rowid()")!;
        Execute(c, "INSERT INTO alias(path,image_id) VALUES($path,$id)", tx, ("$path", alias), ("$id", id));
        if (!skipped)
        {
            long first = long.Parse((string)Scalar(c, "SELECT value FROM meta WHERE key='nextVectorId'")!, System.Globalization.CultureInfo.InvariantCulture);
            byte[] encoded = EncodeDescriptors(descriptors);
            if (_sharedFeatures is null)
                Execute(c, "INSERT INTO vector(image_id,descriptors) VALUES($id,$bytes)", tx, ("$id", id), ("$bytes", encoded));
            else
                _sharedFeatures.Store(Profile.Id, hash, encoded);
            Execute(c, "INSERT INTO vector_stats(image_id,first_id,vector_count) VALUES($id,$first,$count)", tx,
                ("$id", id), ("$first", first), ("$count", descriptors.Length));
            Execute(c, "UPDATE meta SET value=$next WHERE key='nextVectorId'", tx, ("$next", checked(first + descriptors.Length).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        tx.Commit();
        if (_dHashGraph is not null) { _dHashGraph.Add(new(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash), 0, 0, 0)); _dHashIds.Add(id); }
        return new(alias, skipped ? "skipped_low_features" : "added", descriptors.Length);
    }

    internal static byte[] EncodeDescriptors(BinaryDescriptor[] descriptors)
    { byte[] bytes = new byte[checked(descriptors.Length * 32)]; for (int i = 0; i < descriptors.Length; i++) descriptors[i].Write(bytes.AsSpan(i * 32, 32)); return bytes; }

    /// <summary>Enumerates descriptor batches in stable image order without loading the whole database.</summary>
    public IEnumerable<CachedImage> ReadCached(bool unsegmentedOnly = false, int limit = int.MaxValue)
    {
        using var c = Open(); using var command = c.CreateCommand();
        command.CommandText = "SELECT s.image_id,s.first_id,v.descriptors,i.hash FROM vector_stats s JOIN image i ON i.id=s.image_id "
            + (_sharedFeatures is null ? "JOIN" : "LEFT JOIN") + " vector v ON v.image_id=s.image_id "
            + "WHERE EXISTS(SELECT 1 FROM alias a WHERE a.image_id=s.image_id) "
            + (unsegmentedOnly ? "AND s.segment IS NULL " : "") + "ORDER BY s.image_id LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit); using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            byte[] bytes = reader.IsDBNull(2)
                ? _sharedFeatures?.Read(Profile.Id, (byte[])reader[3]) ?? throw new InvalidDataException("Missing shared descriptor cache.")
                : (byte[])reader[2];
            if (bytes.Length % 32 != 0 || bytes.Length > 10064 * 32) throw new InvalidDataException("Invalid cached descriptors.");
            var descriptors = new BinaryDescriptor[bytes.Length / 32];
            for (int i = 0; i < descriptors.Length; i++) descriptors[i] = BinaryDescriptor.Read(bytes.AsSpan(i * 32, 32));
            yield return new(reader.GetInt64(0), reader.GetInt64(1), descriptors);
        }
    }

    /// <summary>显式启用单份内容缓存，迁移已提交的描述子后再删除本地副本。</summary>
    public void UseSharedFeatureCache(string path, bool migrateExisting = false, CancellationToken cancellationToken = default)
    {
        RequireWriter();
        _sharedFeatures?.Dispose();
        _sharedFeatures = new SharedFeatureCache(path);
        if (!migrateExisting) return;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = new List<(long Id, byte[] Hash, byte[] Bytes)>();
            using (var c = Open())
            using (var command = c.CreateCommand())
            {
                command.CommandText = "SELECT v.image_id,i.hash,v.descriptors FROM vector v JOIN image i ON i.id=v.image_id LIMIT 32";
                using var reader = command.ExecuteReader();
                while (reader.Read()) batch.Add((reader.GetInt64(0), (byte[])reader[1], (byte[])reader[2]));
            }
            if (batch.Count == 0) break;
            _sharedFeatures.StoreBatch(Profile.Id, batch.Select(item => (item.Hash, item.Bytes)));
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var item in batch) Execute(connection, "DELETE FROM vector WHERE image_id=$id", transaction, ("$id", item.Id));
            transaction.Commit();
        }
        using var compact = Open();
        if ((long)Scalar(compact, "PRAGMA freelist_count")! > 0) Execute(compact, "VACUUM");
    }

    /// <summary>复用相同提取配置的内容特征，不共享可变图片缓冲。</summary>
    public BinaryDescriptor[]? ReadSharedFeatures(byte[] hash)
    {
        byte[]? bytes = _sharedFeatures?.Read(Profile.Id, hash);
        if (bytes is null) return null;
        if (bytes.Length % 32 != 0 || bytes.Length > 10064 * 32) throw new InvalidDataException("Invalid shared descriptors.");
        var descriptors = new BinaryDescriptor[bytes.Length / 32];
        for (int i = 0; i < descriptors.Length; i++) descriptors[i] = BinaryDescriptor.Read(bytes.AsSpan(i * 32, 32));
        return descriptors;
    }

    /// <summary>回收未引用的别名和本地缓存，保留图像 / 向量 ID 墓碑，避免旧索引身份被复用。</summary>
    public int RetainAliases(IReadOnlySet<string> live)
    {
        RequireWriter();
        var removed = new List<string>();
        using var connection = Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT path FROM alias";
            using var reader = command.ExecuteReader();
            while (reader.Read()) if (!live.Contains(reader.GetString(0))) removed.Add(reader.GetString(0));
        }
        using var transaction = connection.BeginTransaction();
        foreach (string alias in removed) Execute(connection, "DELETE FROM alias WHERE path=$path", transaction, ("$path", alias));
        Execute(connection, "DELETE FROM vector WHERE image_id NOT IN (SELECT image_id FROM alias)", transaction);
        transaction.Commit();
        return removed.Count;
    }

    public HashSet<long> AliasedImageIds()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT image_id FROM alias";
        using var reader = command.ExecuteReader(); var ids = new HashSet<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    public IEnumerable<byte[]> ReferencedFeatureHashes()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT i.hash FROM image i JOIN alias a ON a.image_id=i.id";
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return (byte[])reader[0];
    }

    /// <summary>Checks SQLite integrity without modifying the database.</summary>
    public void CheckIntegrity() { using var c = Open(); if ((string?)Scalar(c, "PRAGMA integrity_check") != "ok") throw new InvalidDataException("Database integrity check failed."); }

    /// <summary>Creates an independent SQLite snapshot and copies its immutable search files for a new generation.</summary>
    public void CopySnapshotTo(string destination, CancellationToken cancellationToken = default, bool shareImmutableFiles = false)
    {
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new InvalidOperationException("Snapshot destination must be empty.");
        if (destination.Equals(DirectoryPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Snapshot must use a new directory.");
        cancellationToken.ThrowIfCancellationRequested(); Directory.CreateDirectory(destination);
        using (var source = Open())
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(destination, "images.db"), Pooling = false }.ToString()))
        { target.Open(); source.BackupDatabase(target); }
        foreach (string name in new[] { "quantizer.bin", "invlists.bin" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (shareImmutableFiles && ImmutableFileLinks.TryCreate(Path.Combine(DirectoryPath, name), Path.Combine(destination, name))) continue;
            using var input = File.OpenRead(Path.Combine(DirectoryPath, name));
            using var output = new FileStream(Path.Combine(destination, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[1024 * 1024]; int count;
            while ((count = input.Read(buffer)) > 0) { cancellationToken.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
            output.Flush(true);
        }
    }

    /// <summary>Removes descriptor caches while retaining metadata and searchable indexes.</summary>
    public void Clean(bool all = false)
    {
        RequireWriter(); using var c = Open();
        Execute(c, all ? "DELETE FROM vector" : "DELETE FROM vector WHERE image_id IN (SELECT image_id FROM vector_stats WHERE indexed=1)");
        Execute(c, "VACUUM");
    }

    /// <summary>Releases only this database's writer lease.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _sharedFeatures?.Dispose(); }
        finally { _writerLease?.Dispose(); }
    }
}

using Microsoft.Data.Sqlite;

namespace EhViewerForPc.ImageSearch;

/// <summary>按提取配置与内容哈希共享描述子；先提交缓存，再提交引用它的任务元数据。</summary>
public sealed class SharedFeatureCache : IDisposable
{
    private readonly SqliteConnection _connection;

    public SharedFeatureCache(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bool fresh = !File.Exists(path);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = (fresh ? "PRAGMA auto_vacuum=INCREMENTAL;" : string.Empty)
                + "CREATE TABLE IF NOT EXISTS feature(profile TEXT NOT NULL,hash BLOB NOT NULL,descriptors BLOB NOT NULL,PRIMARY KEY(profile,hash)) WITHOUT ROWID";
            command.ExecuteNonQuery();
        }
        catch { _connection.Dispose(); throw; }
    }

    internal byte[]? Read(string profile, byte[] hash)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT descriptors FROM feature WHERE profile=$profile AND hash=$hash";
        command.Parameters.AddWithValue("$profile", profile); command.Parameters.AddWithValue("$hash", hash);
        return command.ExecuteScalar() as byte[];
    }

    internal void Store(string profile, byte[] hash, byte[] descriptors)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO feature(profile,hash,descriptors) VALUES($profile,$hash,$descriptors)";
        command.Parameters.AddWithValue("$profile", profile); command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$descriptors", descriptors); command.ExecuteNonQuery();
    }

    internal void StoreBatch(string profile, IEnumerable<(byte[] Hash, byte[] Bytes)> batch)
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO feature(profile,hash,descriptors) VALUES($profile,$hash,$descriptors)";
        command.Parameters.AddWithValue("$profile", profile);
        var hash = command.Parameters.Add("$hash", SqliteType.Blob);
        var bytes = command.Parameters.Add("$descriptors", SqliteType.Blob);
        foreach (var item in batch)
        {
            hash.Value = item.Hash; bytes.Value = item.Bytes; command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public int Prune(IEnumerable<(string Profile, byte[] Hash)> referenced)
    {
        using var transaction = _connection.BeginTransaction();
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "CREATE TEMP TABLE IF NOT EXISTS live_feature(profile TEXT,hash BLOB,PRIMARY KEY(profile,hash)) WITHOUT ROWID; DELETE FROM live_feature";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT OR IGNORE INTO live_feature VALUES($profile,$hash)";
            var profile = command.Parameters.Add("$profile", SqliteType.Text);
            var hash = command.Parameters.Add("$hash", SqliteType.Blob);
            foreach (var item in referenced)
            {
                profile.Value = item.Profile; hash.Value = item.Hash; command.ExecuteNonQuery();
            }
        }
        int removed;
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM feature WHERE NOT EXISTS(SELECT 1 FROM live_feature l WHERE l.profile=feature.profile AND l.hash=feature.hash)";
            removed = command.ExecuteNonQuery();
        }
        transaction.Commit();
        if (removed > 0) { using var command = _connection.CreateCommand(); command.CommandText = "PRAGMA incremental_vacuum"; command.ExecuteNonQuery(); }
        return removed;
    }

    public void Dispose() => _connection.Dispose();
}

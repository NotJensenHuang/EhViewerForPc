using Microsoft.Data.Sqlite;

namespace EhViewerForPc.ImageSearch;

public sealed partial class ImageDatabase
{
    private ImportWriter? _importWriter;

    /// <summary>单写入者复用连接和预编译语句；调用方按批提交。</summary>
    public ImportWriter CreateImportWriter()
    {
        RequireWriter();
        var writer = new ImportWriter(this);
        if (Interlocked.CompareExchange(ref _importWriter, writer, null) is not null)
        {
            writer.Dispose();
            throw new InvalidOperationException("An import writer is already active.");
        }
        return writer;
    }

    public sealed class ImportWriter : IDisposable
    {
        private readonly ImageDatabase _database;
        private readonly SqliteConnection _connection;
        private readonly string _profileId;
        private readonly Dictionary<string, SqliteCommand> _commands = new(StringComparer.Ordinal);
        private readonly List<(byte[] Hash, byte[] Bytes)> _features = [];
        private readonly List<(byte[] Hash, long Id)> _newHashes = [];
        private SqliteTransaction? _transaction;
        private bool _disposed;
        public bool IsFaulted { get; private set; }
        public int CommittedBatches { get; private set; }

        internal ImportWriter(ImageDatabase database)
        {
            _database = database;
            _profileId = database.Profile.Id;
            _connection = database.Open();
        }

        public ImportEntry Add(string alias, byte[] hash, BinaryDescriptor[] descriptors, int minFeatures = 30,
            bool append = true, int duplicateDistance = 0, bool overwrite = false)
        {
            CheckUsable();
            try
            {
                var result = _database.AddCore(this, alias, hash, descriptors, minFeatures, append, duplicateDistance, overwrite);
                // 近似哈希图必须看到上一项已提交的状态，因此该兼容模式仍逐项提交。
                if (_database.HashMode == ImageHashMode.DHash) Commit();
                return result;
            }
            catch { IsFaulted = true; throw; }
        }

        public void Commit()
        {
            CheckUsable();
            if (_transaction is null) return;
            try
            {
                // 先保存共享特征，再提交引用；中途退出最多留下可复用的孤立特征。
                if (_features.Count > 0) _database._sharedFeatures!.StoreBatch(_profileId, _features);
                _transaction.Commit();
                _transaction.Dispose();
                _transaction = null;
                foreach (var item in _newHashes)
                {
                    _database._dHashGraph!.Add(new(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(item.Hash), 0, 0, 0));
                    _database._dHashIds.Add(item.Id);
                }
                _features.Clear();
                _newHashes.Clear();
                CommittedBatches++;
            }
            catch { IsFaulted = true; throw; }
        }

        private void CheckUsable()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ObjectDisposedException.ThrowIf(_database._disposed, _database);
            if (IsFaulted) throw new InvalidOperationException("The failed import batch must be discarded.");
        }

        internal object? Scalar(string sql, params (string Name, object Value)[] values)
            => Command(sql, values).ExecuteScalar();
        internal void Execute(string sql, params (string Name, object Value)[] values)
            => Command(sql, values).ExecuteNonQuery();
        internal void StoreFeatures(byte[] hash, byte[] bytes) => _features.Add((hash, bytes));
        internal void RecordHash(byte[] hash, long id) => _newHashes.Add((hash, id));

        private SqliteCommand Command(string sql, (string Name, object Value)[] values)
        {
            _transaction ??= _connection.BeginTransaction();
            if (!_commands.TryGetValue(sql, out var command))
            {
                command = _connection.CreateCommand();
                command.CommandText = sql;
                foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value);
                command.Prepare();
                _commands.Add(sql, command);
            }
            command.Transaction = _transaction;
            foreach (var value in values) command.Parameters[value.Name].Value = value.Value;
            return command;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _transaction?.Dispose();
                foreach (var command in _commands.Values) command.Dispose();
                _commands.Clear();
                _features.Clear();
                _newHashes.Clear();
            }
            finally
            {
                _connection.Dispose();
                Interlocked.CompareExchange(ref _database._importWriter, null, this);
            }
        }
    }
}

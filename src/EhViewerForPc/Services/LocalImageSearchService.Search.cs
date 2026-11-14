using Avalonia.Threading;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.ImageSearch;

namespace EhViewerForPc.Services;

public sealed partial class LocalImageSearchService
{
    private readonly SemaphoreSlim _querySerial = new(1, 1);
    private QueryCache? _queryCache;

    // 冻结代只保留量化器和桶目录，不长期持有文件句柄或阻止旧代回收。
    private sealed record QueryCache(string Root, ImageSearchPointer Pointer, (long Length, long Mtime)[] Stamps,
        ImageDatabase Database, ImageSearchEngine Engine) : IDisposable
    {
        public bool Matches(string root, ImageSearchPointer pointer) => PathIdentity.Comparer.Equals(Root, root) &&
            Pointer == pointer && Stamps.AsSpan().SequenceEqual(ReadQueryStamps(root, pointer));
        public void Dispose() => Database.Dispose();
    }

    private static (long, long)[] ReadQueryStamps(string root, ImageSearchPointer pointer)
    {
        string generation = Path.Combine(root, "generations", pointer.GenerationId);
        return new[] { "manifest.db", "backend/images.db", "backend/quantizer.bin", "backend/invlists.bin" }
            .Select(name => { var file = new FileInfo(Path.Combine(generation, name)); return (file.Length, file.LastWriteTimeUtc.Ticks); }).ToArray();
    }

    private LocalImageSearchHit[] SearchCore(string root, byte[] input, ImageCrop? crop, LibrarySource[] sources,
        Dictionary<(string, string), GalleryInfo> copies, int nprobe, IProgress<string>? progress, CancellationToken token)
    {
        ImageSearchPointer pointer;
        lock (_generationGate)
        {
            ImageSearchPointer? candidate = null;
            try { candidate = ReadPointer(root, validateManifest: false); }
            catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
            bool cached = false;
            try { cached = candidate is not null && _queryCache?.Matches(root, candidate) == true; }
            catch (IOException) { }
            pointer = cached ? candidate! : LoadAvailablePointerCore(root, token) ?? throw new InvalidOperationException("请先建立图片索引。");
            _activeReaders[pointer.GenerationId] = _activeReaders.GetValueOrDefault(pointer.GenerationId) + 1;
        }
        try
        {
            string generation = Path.Combine(root, "generations", pointer.GenerationId);
            if (_queryCache?.Matches(root, pointer) != true)
            {
                progress?.Report("正在校验并载入图片索引…");
                var stamps = ReadQueryStamps(root, pointer);
                var backend = new ImageDatabase(Path.Combine(generation, "backend"));
                try
                {
                    long reported = Environment.TickCount64;
                    var engine = new ImageSearchEngine(backend, (done, total) =>
                    {
                        long now = Environment.TickCount64;
                        if (now - reported < 250 && done != total) return;
                        reported = now;
                        progress?.Report($"正在校验并载入图片索引：{done * 100d / Math.Max(1, total):F0}%");
                    }, token, allowLinkedPublication: true);
                    if (!stamps.AsSpan().SequenceEqual(ReadQueryStamps(root, pointer))) throw new IOException("图片索引已变化，请重新搜索。");
                    _queryCache?.Dispose();
                    _queryCache = new(root, pointer, stamps, backend, engine);
                }
                catch { backend.Dispose(); throw; }
            }
            progress?.Report("正在搜索本地图片…");
            // 首次完整校验不消耗检索时限，用户取消与退出仍覆盖整个过程。
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var queryToken = timeout.Token;
            var results = _queryCache.Engine.Search(GrayImage.Decode(input, crop),
                new() { NProbe = Math.Min(nprobe, _queryCache.Engine.Centers) }, queryToken);
            queryToken.ThrowIfCancellationRequested();
            if (results.Length == 0) return [];
            progress?.Report("正在匹配本地页…");
            using var manifest = OpenManifest(generation, writable: false);
            Execute(manifest, "PRAGMA temp_store=MEMORY; CREATE TEMP TABLE query_alias(alias TEXT PRIMARY KEY,score REAL NOT NULL);");
            using (var transaction = manifest.BeginTransaction(deferred: true))
            using (var insert = manifest.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO query_alias VALUES($alias,$score) ON CONFLICT(alias) DO UPDATE SET score=max(score,excluded.score)";
                var aliasParameter = insert.Parameters.Add("$alias", Microsoft.Data.Sqlite.SqliteType.Text);
                var scoreParameter = insert.Parameters.Add("$score", Microsoft.Data.Sqlite.SqliteType.Real);
                insert.Prepare();
                foreach (var result in results)
                foreach (string alias in result.Aliases)
                {
                    queryToken.ThrowIfCancellationRequested();
                    aliasParameter.Value = alias;
                    scoreParameter.Value = result.Score;
                    insert.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            var selected = sources.ToDictionary(s => s.Id, StringComparer.Ordinal);
            var hits = new List<LocalImageSearchHit>();
            using var command = manifest.CreateCommand();
            // CROSS JOIN 固定旧清单只扫描一次，不依赖新建 alias 索引或改写现有索引。
            command.CommandText = "SELECT m.source_id,m.stable_id,m.page,m.path,m.length,m.mtime,q.score FROM member m NOT INDEXED CROSS JOIN query_alias q ON q.alias=m.alias WHERE m.state='indexed' ORDER BY q.score DESC,m.stable_id,m.page";
            using var reader = command.ExecuteReader();
            var seen = new HashSet<(string, string, int)>();
            while (reader.Read())
            {
                queryToken.ThrowIfCancellationRequested();
                string sourceId = reader.GetString(0), stableId = reader.GetString(1);
                if (!selected.TryGetValue(sourceId, out var source) || !copies.TryGetValue((sourceId, stableId), out var gallery)) continue;
                string path = Path.GetFullPath(Path.Combine(source.RootPath, reader.GetString(3)));
                if (!IsSafeSourcePath(path, source.RootPath)) continue;
                var file = new FileInfo(path);
                long length = reader.GetInt64(4), mtime = reader.GetInt64(5);
                if (!file.Exists || file.Length != length || file.LastWriteTimeUtc.Ticks != mtime) continue;
                int page = reader.GetInt32(2);
                if (!seen.Add((sourceId, stableId, page))) continue;
                hits.Add(new(gallery, page, path, length, mtime, reader.GetFloat(6), pointer.GenerationId));
                if (hits.Count == 30) break;
            }
            return hits.GroupBy(h => (h.Gallery.SourceId, h.Gallery.StableId, h.PageNumber)).Select(g => g.MaxBy(h => h.Score)!)
                .OrderByDescending(h => h.Score).ThenBy(h => h.Gallery.StableId, StringComparer.Ordinal).ThenBy(h => h.PageNumber).Take(30).ToArray();
        }
        finally
        {
            bool reclaim = false;
            lock (_generationGate)
            {
                if (--_activeReaders[pointer.GenerationId] == 0)
                {
                    _activeReaders.Remove(pointer.GenerationId);
                    reclaim = _retiredGenerations.Remove(pointer.GenerationId);
                }
            }
            if (reclaim) Dispatcher.UIThread.Post(() => { if (Volatile.Read(ref _disposed) == 0) _ = ReclaimRetiredAsync(); });
        }
    }
}

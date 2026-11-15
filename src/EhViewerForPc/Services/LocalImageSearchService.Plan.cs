using System.Security.Cryptography;
using System.Text;
using EhViewerForPc.Core.Format;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Storage;
using Microsoft.Data.Sqlite;

namespace EhViewerForPc.Services;

public sealed record LocalIndexPlanStatistics(int EnumeratedGalleries, int RestoredPages, ImageIndexBuildPhase ResumedPhase);

public sealed partial class LocalImageSearchService
{
    private LocalIndexPlanStatistics? _planStatistics;
    public LocalIndexPlanStatistics? PlanStatistics => Volatile.Read(ref _planStatistics);

    private static void AttachBuildPlan(SqliteConnection manifest, string generation)
    {
        Execute(manifest, "ATTACH DATABASE $path AS build", ("$path", Path.Combine(generation, "build-plan.db")));
        Execute(manifest, """
            CREATE TABLE IF NOT EXISTS build.plan_gallery(source TEXT NOT NULL,stable TEXT NOT NULL,work TEXT NOT NULL,folder TEXT NOT NULL,signature TEXT NOT NULL,verified INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(source,stable));
            CREATE TABLE IF NOT EXISTS build.plan_page(sequence INTEGER PRIMARY KEY,source TEXT NOT NULL,stable TEXT NOT NULL,page INTEGER NOT NULL,done INTEGER NOT NULL DEFAULT 0,UNIQUE(source,stable,page));
            CREATE INDEX IF NOT EXISTS build.plan_pending ON plan_page(done,sequence);
            CREATE TABLE IF NOT EXISTS build.plan_candidate(sequence INTEGER NOT NULL,priority INTEGER NOT NULL,path TEXT NOT NULL,length INTEGER NOT NULL,mtime INTEGER NOT NULL,PRIMARY KEY(sequence,priority));
            """);
    }

    private static int PlanCount(SqliteConnection manifest, string condition = "1")
    {
        using var command = manifest.CreateCommand();
        command.CommandText = "SELECT count(*) FROM build.plan_page WHERE " + condition;
        return checked((int)(long)command.ExecuteScalar()!);
    }

    private static void ReconcilePlanMembership(SqliteConnection manifest, ImageSearchGallery[] galleries, CancellationToken token)
    {
        var requested = galleries.DistinctBy(g => (g.SourceId, g.StableId)).ToDictionary(g => (g.SourceId, g.StableId));
        var obsolete = new List<(string Source, string Stable, bool Removed)>();
        using (var command = manifest.CreateCommand())
        {
            command.CommandText = "SELECT source,stable,folder FROM build.plan_gallery";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string source = reader.GetString(0), stable = reader.GetString(1);
                bool removed = !requested.TryGetValue((source, stable), out var gallery);
                if (removed || !PathIdentity.Comparer.Equals(gallery!.Folder, reader.GetString(2))) obsolete.Add((source, stable, removed));
            }
        }
        using var transaction = manifest.BeginTransaction();
        using var update = manifest.CreateCommand();
        update.Transaction = transaction;
        update.Parameters.Add("$source", SqliteType.Text);
        update.Parameters.Add("$stable", SqliteType.Text);
        foreach (var entry in obsolete)
        {
            token.ThrowIfCancellationRequested();
            update.Parameters["$source"].Value = entry.Source;
            update.Parameters["$stable"].Value = entry.Stable;
            update.CommandText = entry.Removed ? """
                DELETE FROM build.plan_candidate WHERE sequence IN (SELECT sequence FROM build.plan_page WHERE source=$source AND stable=$stable);
                DELETE FROM build.plan_page WHERE source=$source AND stable=$stable;
                DELETE FROM member WHERE source_id=$source AND stable_id=$stable;
                DELETE FROM build.plan_gallery WHERE source=$source AND stable=$stable;
                """ : "DELETE FROM build.plan_gallery WHERE source=$source AND stable=$stable";
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static bool HasPlannedGallery(SqliteConnection manifest, ImageSearchGallery gallery, SqliteTransaction transaction)
    {
        using var command = manifest.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM build.plan_gallery WHERE source=$source AND stable=$stable";
        command.Parameters.AddWithValue("$source", gallery.SourceId); command.Parameters.AddWithValue("$stable", gallery.StableId);
        return command.ExecuteScalar() is not null;
    }

    private static PageScanResult ScanIndexPages(string folder, out string? error)
    {
        var files = new List<PageCandidate>();
        error = null;
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
            {
                string extension = file.Extension.ToLowerInvariant();
                if (!PageFileParser.IsSupportedExtension(extension) || (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                files.Add(new() { FilePath = file.FullName, FileName = file.Name, Extension = extension,
                    Length = file.Length, MtimeTicks = file.LastWriteTimeUtc.Ticks });
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            error = failure.GetType().Name;
        }
        var header = SpiderInfoParser.TryParseFile(Path.Combine(folder, ".ehviewer"), out _);
        return PageFileParser.Scan(files, header, out _);
    }

    private bool PrepareBuildPlan(SqliteConnection manifest, ImageSearchGallery[] galleries, Dictionary<string, LibrarySource> sources,
        bool verify, CancellationToken token, Action<int, int>? galleryProgress = null)
    {
        using var backgroundIo = BackgroundIoScope.Enter();
        bool changed = false;
        int completedGalleries = 0;
        foreach (var chunk in galleries.DistinctBy(g => (g.SourceId, g.StableId)).Chunk(16))
        {
            using var transaction = manifest.BeginTransaction();
            foreach (var gallery in chunk)
            {
                completedGalleries++;
                token.ThrowIfCancellationRequested();
                if (!sources.TryGetValue(gallery.SourceId, out var source)) continue;
                if (!verify && HasPlannedGallery(manifest, gallery, transaction)) continue;
                if (verify)
                {
                    using var checkedGallery = manifest.CreateCommand();
                    checkedGallery.Transaction = transaction;
                    checkedGallery.CommandText = "SELECT verified FROM build.plan_gallery WHERE source=$source AND stable=$stable";
                    checkedGallery.Parameters.AddWithValue("$source", gallery.SourceId); checkedGallery.Parameters.AddWithValue("$stable", gallery.StableId);
                    if (checkedGallery.ExecuteScalar() is long value && value == 1) continue;
                }
                if (!IsSafeSourcePath(gallery.Folder, source.RootPath)) throw new IOException("图库路径已变化，当前索引已保留。");
                var scan = ScanIndexPages(gallery.Folder, out string? error);
                if (error is not null) throw new IOException("图库目录暂时无法读取，当前索引已保留。");
                if (PlanStatistics is { } statistics) Volatile.Write(ref _planStatistics, statistics with { EnumeratedGalleries = statistics.EnumeratedGalleries + 1 });
                var signatureText = new StringBuilder();
                foreach (var page in scan.Pages)
                foreach (var file in page.Candidates)
                    signatureText.Append(page.Number).Append('|').Append(Path.GetRelativePath(source.RootPath, file.FilePath)).Append('|').Append(file.Length).Append('|').Append(file.MtimeTicks).Append('\n');
                string signature = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signatureText.ToString())));
                using var prior = manifest.CreateCommand();
                prior.Transaction = transaction;
                prior.CommandText = "SELECT signature FROM build.plan_gallery WHERE source=$source AND stable=$stable";
                prior.Parameters.AddWithValue("$source", gallery.SourceId); prior.Parameters.AddWithValue("$stable", gallery.StableId);
                bool same = prior.ExecuteScalar() as string == signature;
                using var command = manifest.CreateCommand(); command.Transaction = transaction;
                void Run(string sql, params (string Name, object Value)[] values)
                {
                    command.CommandText = sql; command.Parameters.Clear();
                    foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value);
                    command.ExecuteNonQuery();
                }
                if (!same)
                {
                    changed = true;
                    // 修改的画廊按页比较，已提交且签名匹配的页保持完成，候选优先级变化必须重试。
                    var present = new HashSet<int>();
                    foreach (var page in scan.Pages)
                    {
                        token.ThrowIfCancellationRequested(); present.Add(page.Number);
                        Run("INSERT OR IGNORE INTO build.plan_page(source,stable,page) VALUES($source,$stable,$page)", ("$source", gallery.SourceId), ("$stable", gallery.StableId), ("$page", page.Number));
                        command.CommandText = "SELECT sequence FROM build.plan_page WHERE source=$source AND stable=$stable AND page=$page";
                        long sequence = (long)command.ExecuteScalar()!;
                        Run("DELETE FROM build.plan_candidate WHERE sequence=$sequence", ("$sequence", sequence));
                        for (int priority = 0; priority < page.Candidates.Count; priority++)
                        {
                            var file = page.Candidates[priority];
                            Run("INSERT INTO build.plan_candidate VALUES($sequence,$priority,$path,$length,$mtime)", ("$sequence", sequence), ("$priority", priority),
                                ("$path", Path.GetRelativePath(source.RootPath, file.FilePath)), ("$length", file.Length), ("$mtime", file.MtimeTicks));
                        }
                        Run("""
                            UPDATE build.plan_page SET done=CASE WHEN EXISTS(
                            SELECT 1 FROM member m JOIN build.plan_candidate c ON c.sequence=$sequence AND c.priority=0
                            WHERE m.source_id=$source AND m.stable_id=$stable AND m.page=$page AND m.path=c.path AND m.length=c.length AND m.mtime=c.mtime AND m.state IN ('indexed','skipped')) THEN 1 ELSE 0 END WHERE sequence=$sequence
                            """, ("$sequence", sequence), ("$source", gallery.SourceId), ("$stable", gallery.StableId), ("$page", page.Number));
                        Run("DELETE FROM member WHERE source_id=$source AND stable_id=$stable AND page=$page AND NOT EXISTS(SELECT 1 FROM build.plan_page WHERE sequence=$sequence AND done=1)",
                            ("$source", gallery.SourceId), ("$stable", gallery.StableId), ("$page", page.Number), ("$sequence", sequence));
                    }
                    var removed = new List<(long Sequence, int Page)>();
                    command.Parameters.Clear(); command.CommandText = "SELECT sequence,page FROM build.plan_page WHERE source=$source AND stable=$stable";
                    command.Parameters.AddWithValue("$source", gallery.SourceId); command.Parameters.AddWithValue("$stable", gallery.StableId);
                    using (var reader = command.ExecuteReader()) while (reader.Read()) if (!present.Contains(reader.GetInt32(1))) removed.Add((reader.GetInt64(0), reader.GetInt32(1)));
                    foreach (var item in removed)
                    {
                        Run("DELETE FROM member WHERE source_id=$source AND stable_id=$stable AND page=$page", ("$source", gallery.SourceId), ("$stable", gallery.StableId), ("$page", item.Page));
                        Run("DELETE FROM build.plan_candidate WHERE sequence=$sequence", ("$sequence", item.Sequence));
                        Run("DELETE FROM build.plan_page WHERE sequence=$sequence", ("$sequence", item.Sequence));
                    }
                }
                Run("INSERT OR REPLACE INTO build.plan_gallery VALUES($source,$stable,$work,$folder,$signature,$verified)", ("$source", gallery.SourceId), ("$stable", gallery.StableId),
                    ("$work", gallery.WorkId), ("$folder", gallery.Folder), ("$signature", signature), ("$verified", verify ? 1 : 0));
            }
            transaction.Commit();
            galleryProgress?.Invoke(completedGalleries, galleries.Length);
        }
        return changed;
    }

    private static IEnumerable<(ImageSearchGallery Gallery, GalleryPage Page)> ReadPlannedPages(string generation,
        Dictionary<string, LibrarySource> sources, bool pendingOnly, CancellationToken token)
    {
        long after = 0;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(generation, "build-plan.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var window = new List<(ImageSearchGallery Gallery, GalleryPage Page)>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT p.sequence,p.source,p.stable,p.page,g.work,g.folder,c.path,c.length,c.mtime
                    FROM (SELECT * FROM plan_page WHERE sequence>$after
                    """ + (pendingOnly ? " AND done=0 " : " ") + """
                    ORDER BY sequence LIMIT 128) p JOIN plan_gallery g ON g.source=p.source AND g.stable=p.stable
                    LEFT JOIN plan_candidate c ON c.sequence=p.sequence ORDER BY p.sequence,c.priority
                    """;
                command.Parameters.AddWithValue("$after", after);
                long sequence = -1; ImageSearchGallery? gallery = null; int page = 0;
                var candidates = new List<PageCandidate>();
                void AddPage()
                {
                    if (gallery is not null) window.Add((gallery, new GalleryPage { Number = page, Candidates = candidates.ToArray() }));
                    candidates.Clear();
                }
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.GetInt64(0) != sequence)
                    {
                        AddPage(); sequence = reader.GetInt64(0); after = sequence;
                        string sourceId = reader.GetString(1);
                        if (!sources.ContainsKey(sourceId)) throw new InvalidDataException("索引任务的数据源已变化。");
                        gallery = new(sourceId, reader.GetString(2), reader.GetString(4), reader.GetString(5)); page = reader.GetInt32(3);
                    }
                    if (!reader.IsDBNull(6))
                    {
                        string path = Path.GetFullPath(Path.Combine(sources[gallery!.SourceId].RootPath, reader.GetString(6)));
                        if (!IsInside(path, sources[gallery.SourceId].RootPath)) throw new InvalidDataException("索引任务包含无效页路径。");
                        candidates.Add(new() { FilePath = path, FileName = Path.GetFileName(path), Extension = Path.GetExtension(path).ToLowerInvariant(), Length = reader.GetInt64(7), MtimeTicks = reader.GetInt64(8) });
                    }
                }
                AddPage();
            }
            // 先关闭短游标再交给 worker，避免回滚日志模式的读锁阻塞原子提交。
            if (window.Count == 0) yield break;
            foreach (var value in window) { token.ThrowIfCancellationRequested(); yield return value; }
        }
    }

    private static ExtractionResult ReadPlanResult(SqliteConnection manifest)
    {
        int indexed = 0, skipped = 0;
        using var command = manifest.CreateCommand();
        command.CommandText = "SELECT state,count(*) FROM member GROUP BY state";
        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            if (reader.GetString(0) == "indexed") indexed = reader.GetInt32(1);
            if (reader.GetString(0) == "skipped") skipped = reader.GetInt32(1);
        }
        int failed = PlanCount(manifest, "done=2"), restored = indexed + skipped;
        return new(restored + failed, indexed, skipped, failed, restored, false);
    }
}

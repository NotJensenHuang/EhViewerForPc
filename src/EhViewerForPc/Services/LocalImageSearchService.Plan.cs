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

    private static void SeedUnchangedDownloadIndex(SqliteConnection manifest, string previous, ImageSearchGallery[] galleries,
        Dictionary<string, LibrarySource> sources, HashSet<string> affectedFolders, CancellationToken token)
    {
        // 复用发布代的清单与别名，未变副本无需再次遍历图片目录。
        using var backgroundIo = BackgroundIoScope.Enter();
        string previousManifest = ManifestUri(previous) + "?mode=ro";
        Execute(manifest, "ATTACH DATABASE $path AS previous_index", ("$path", previousManifest));
        try
        {
            using var transaction = manifest.BeginTransaction(deferred: true);
            using var command = manifest.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "CREATE TEMP TABLE retained_gallery(source TEXT,stable TEXT,work TEXT,folder TEXT,PRIMARY KEY(source,stable))";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT OR IGNORE INTO temp.retained_gallery VALUES($source,$stable,$work,$folder)";
            var source = command.Parameters.Add("$source", SqliteType.Text);
            var stable = command.Parameters.Add("$stable", SqliteType.Text);
            var work = command.Parameters.Add("$work", SqliteType.Text);
            var folder = command.Parameters.Add("$folder", SqliteType.Text);
            foreach (var gallery in galleries)
            {
                token.ThrowIfCancellationRequested();
                if (!sources.ContainsKey(gallery.SourceId) || affectedFolders.Contains(gallery.Folder)) continue;
                source.Value = gallery.SourceId;
                stable.Value = gallery.StableId;
                work.Value = gallery.WorkId;
                folder.Value = gallery.Folder;
                command.ExecuteNonQuery();
            }

            command.Parameters.Clear();
            command.CommandText = """
                DELETE FROM build.plan_gallery WHERE signature='' AND NOT EXISTS(
                    SELECT 1 FROM temp.retained_gallery r WHERE r.source=plan_gallery.source AND r.stable=plan_gallery.stable);
                INSERT OR IGNORE INTO member
                    SELECT m.alias,m.source_id,m.stable_id,m.page,m.path,m.length,m.mtime,m.state
                    FROM previous_index.member m JOIN temp.retained_gallery r ON r.source=m.source_id AND r.stable=m.stable_id
                    WHERE m.state IN ('indexed','skipped');
                INSERT OR IGNORE INTO build.plan_gallery
                    SELECT r.source,r.stable,r.work,r.folder,'',1 FROM temp.retained_gallery r
                    WHERE EXISTS(SELECT 1 FROM member m WHERE m.source_id=r.source AND m.stable_id=r.stable);
                INSERT OR IGNORE INTO build.plan_page(source,stable,page,done)
                    SELECT m.source_id,m.stable_id,m.page,1 FROM member m
                    JOIN temp.retained_gallery r ON r.source=m.source_id AND r.stable=m.stable_id;
                DROP TABLE temp.retained_gallery;
                """;
            command.ExecuteNonQuery();
            token.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        finally { Execute(manifest, "DETACH DATABASE previous_index"); }
    }

    private static bool IsSafeIndexFolder(string folder, string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!IsInside(folder, root)) return false;
        for (string? path = folder; path is not null; path = Path.GetDirectoryName(path))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                return false;
            if (PathIdentity.Comparer.Equals(path, root)) return true;
        }
        return false;
    }

    private static IReadOnlyList<GalleryPage> ScanIndexPages(string folder, CancellationToken token)
    {
        var files = new List<PageCandidate>();
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
        {
            token.ThrowIfCancellationRequested();
            string extension = file.Extension.ToLowerInvariant();
            if (!PageFileParser.IsSupportedExtension(extension) || (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            files.Add(new() { FilePath = file.FullName, FileName = file.Name, Extension = extension,
                Length = file.Length, MtimeTicks = file.LastWriteTimeUtc.Ticks });
        }
        // 页号和候选排序只取决于图片条目；索引无需计算下载缺页或读取 .ehviewer。
        return PageFileParser.ScanPages(files);
    }

    private bool PrepareBuildPlan(SqliteConnection manifest, ImageSearchGallery[] galleries, Dictionary<string, LibrarySource> sources,
        bool verify, CancellationToken token, Action<int, int>? galleryProgress = null)
    {
        using var backgroundIo = BackgroundIoScope.Enter();
        bool changed = false;
        int completedGalleries = 0;
        using var write = manifest.CreateCommand();
        write.CommandText = """
            INSERT OR REPLACE INTO build.plan_gallery VALUES($source,$stable,$work,$folder,$signature,$verified)
            """;
        var sourceId = write.Parameters.Add("$source", SqliteType.Text);
        var stableId = write.Parameters.Add("$stable", SqliteType.Text);
        var workId = write.Parameters.Add("$work", SqliteType.Text);
        var folder = write.Parameters.Add("$folder", SqliteType.Text);
        var gallerySignature = write.Parameters.Add("$signature", SqliteType.Text);
        write.Parameters.Add("$verified", SqliteType.Integer).Value = verify ? 1 : 0;

        using var prior = manifest.CreateCommand();
        prior.CommandText = "SELECT signature,verified FROM build.plan_gallery WHERE source=$source AND stable=$stable";
        var priorSource = prior.Parameters.Add("$source", SqliteType.Text);
        var priorStable = prior.Parameters.Add("$stable", SqliteType.Text);

        using var batch = new PlanGalleryBatch(manifest);
        foreach (var chunk in galleries.DistinctBy(g => (g.SourceId, g.StableId)).Chunk(16))
        {
            using var transaction = manifest.BeginTransaction();
            prior.Transaction = transaction;
            write.Transaction = transaction;
            batch.SetTransaction(transaction);
            foreach (var gallery in chunk)
            {
                completedGalleries++;
                token.ThrowIfCancellationRequested();
                if (!sources.TryGetValue(gallery.SourceId, out var source)) continue;

                priorSource.Value = gallery.SourceId;
                priorStable.Value = gallery.StableId;
                string? previousSignature = null;
                using (var reader = prior.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        if (!verify || reader.GetInt64(1) == 1) continue;
                        previousSignature = reader.GetString(0);
                    }
                }

                IReadOnlyList<GalleryPage> pages;
                try
                {
                    if (!IsSafeIndexFolder(gallery.Folder, source.RootPath))
                        throw new IOException("图库路径已变化。");
                    pages = ScanIndexPages(gallery.Folder, token);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    throw new IOException("图库目录暂时无法读取，当前索引已保留。", error);
                }
                if (PlanStatistics is { } statistics) Volatile.Write(ref _planStatistics, statistics with { EnumeratedGalleries = statistics.EnumeratedGalleries + 1 });
                var signatureText = new StringBuilder();
                foreach (var page in pages)
                foreach (var file in page.Candidates)
                    signatureText.Append(page.Number).Append('|').Append(Path.GetRelativePath(source.RootPath, file.FilePath)).Append('|').Append(file.Length).Append('|').Append(file.MtimeTicks).Append('\n');
                string signature = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signatureText.ToString())));
                if (previousSignature != signature)
                {
                    changed = true;
                    batch.Replace(gallery, source.RootPath, pages, token);
                }

                sourceId.Value = gallery.SourceId;
                stableId.Value = gallery.StableId;
                workId.Value = gallery.WorkId;
                folder.Value = gallery.Folder;
                gallerySignature.Value = signature;
                write.ExecuteNonQuery();
            }
            transaction.Commit();
            galleryProgress?.Invoke(completedGalleries, galleries.Length);
        }
        return changed;
    }

    private sealed class PlanGalleryBatch : IDisposable
    {
        private readonly SqliteConnection _manifest;
        private readonly SqliteCommand _stage;
        private readonly SqliteCommand _replace;
        private readonly SqliteParameter _page;
        private readonly SqliteParameter _priority;
        private readonly SqliteParameter _path;
        private readonly SqliteParameter _length;
        private readonly SqliteParameter _mtime;
        private readonly SqliteParameter _source;
        private readonly SqliteParameter _stable;

        internal PlanGalleryBatch(SqliteConnection manifest)
        {
            _manifest = manifest;
            Execute(manifest, """
                CREATE TEMP TABLE gallery_candidate(
                    page INTEGER,priority INTEGER,path TEXT,length INTEGER,mtime INTEGER,PRIMARY KEY(page,priority)
                ) WITHOUT ROWID
                """);
            _stage = manifest.CreateCommand();
            _stage.CommandText = "INSERT INTO temp.gallery_candidate VALUES($page,$priority,$path,$length,$mtime)";
            _page = _stage.Parameters.Add("$page", SqliteType.Integer);
            _priority = _stage.Parameters.Add("$priority", SqliteType.Integer);
            _path = _stage.Parameters.Add("$path", SqliteType.Text);
            _length = _stage.Parameters.Add("$length", SqliteType.Integer);
            _mtime = _stage.Parameters.Add("$mtime", SqliteType.Integer);

            _replace = manifest.CreateCommand();
            _replace.CommandText = """
                INSERT OR IGNORE INTO build.plan_page(source,stable,page)
                    SELECT $source,$stable,page FROM temp.gallery_candidate WHERE priority=0 ORDER BY page;
                DELETE FROM build.plan_candidate WHERE sequence IN (
                    SELECT sequence FROM build.plan_page WHERE source=$source AND stable=$stable);
                DELETE FROM member WHERE source_id=$source AND stable_id=$stable AND page IN (
                    SELECT page FROM build.plan_page WHERE source=$source AND stable=$stable
                    AND page NOT IN (SELECT page FROM temp.gallery_candidate WHERE priority=0));
                DELETE FROM build.plan_page WHERE source=$source AND stable=$stable
                    AND page NOT IN (SELECT page FROM temp.gallery_candidate WHERE priority=0);
                INSERT INTO build.plan_candidate(sequence,priority,path,length,mtime)
                    SELECT p.sequence,c.priority,c.path,c.length,c.mtime
                    FROM build.plan_page p JOIN temp.gallery_candidate c ON c.page=p.page
                    WHERE p.source=$source AND p.stable=$stable;
                UPDATE build.plan_page AS p SET done=EXISTS(
                    SELECT 1 FROM temp.gallery_candidate c JOIN member m
                        ON m.source_id=$source AND m.stable_id=$stable AND m.page=c.page
                        AND m.path=c.path AND m.length=c.length AND m.mtime=c.mtime
                    WHERE c.page=p.page AND c.priority=0 AND m.state IN ('indexed','skipped'))
                    WHERE p.source=$source AND p.stable=$stable;
                DELETE FROM member WHERE source_id=$source AND stable_id=$stable AND EXISTS (
                    SELECT 1 FROM build.plan_page p WHERE p.source=member.source_id AND p.stable=member.stable_id
                    AND p.page=member.page AND p.done=0);
                DELETE FROM temp.gallery_candidate;
                """;
            _source = _replace.Parameters.Add("$source", SqliteType.Text);
            _stable = _replace.Parameters.Add("$stable", SqliteType.Text);
        }

        internal void SetTransaction(SqliteTransaction transaction)
        {
            _stage.Transaction = transaction;
            _replace.Transaction = transaction;
        }

        internal void Replace(ImageSearchGallery gallery, string root, IReadOnlyList<GalleryPage> pages, CancellationToken token)
        {
            // 一个画廊大小的暂存表，保留现有页序号并按首选候选的完整签名恢复完成状态。
            foreach (var page in pages)
            {
                _page.Value = page.Number;
                for (int priority = 0; priority < page.Candidates.Count; priority++)
                {
                    token.ThrowIfCancellationRequested();
                    var file = page.Candidates[priority];
                    _priority.Value = priority;
                    _path.Value = Path.GetRelativePath(root, file.FilePath);
                    _length.Value = file.Length;
                    _mtime.Value = file.MtimeTicks;
                    _stage.ExecuteNonQuery();
                }
            }
            _source.Value = gallery.SourceId;
            _stable.Value = gallery.StableId;
            _replace.ExecuteNonQuery();
        }

        public void Dispose()
        {
            _replace.Dispose();
            _stage.Dispose();
            Execute(_manifest, "DROP TABLE IF EXISTS temp.gallery_candidate");
        }
    }


    private static IEnumerable<(ImageSearchGallery Gallery, GalleryPage Page)> ReadPendingPlannedPages(string generation,
        Dictionary<string, LibrarySource> sources, CancellationToken token)
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
                    FROM (SELECT * FROM plan_page WHERE sequence>$after AND done=0
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

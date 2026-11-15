using Microsoft.Data.Sqlite;

namespace EhViewerForPc.Services;

public sealed partial class LocalImageSearchService
{
    // 旧清单保持只读；小窗口在连接私有的内存表中匹配，不逐页访问源文件或调度 worker。
    // 只接受首选候选的完整签名，其他候选仍由提取流水线按优先级尝试。
    internal static int ReusePlannedPages(SqliteConnection manifest, string previous, CancellationToken token,
        Action<int, int>? committed = null)
    {
        using var backgroundIo = BackgroundIoScope.Enter();
        using var old = OpenManifest(previous, writable: false);
        Execute(old, """
            PRAGMA temp_store=MEMORY;
            CREATE TEMP TABLE candidate(sequence INTEGER PRIMARY KEY,source TEXT,stable TEXT,page INTEGER,path TEXT,length INTEGER,mtime INTEGER);
            """);
        Execute(manifest, """
            CREATE TEMP TABLE reuse_page(sequence INTEGER PRIMARY KEY,alias TEXT,source_id TEXT,stable_id TEXT,
                page INTEGER,path TEXT,length INTEGER,mtime INTEGER,state TEXT);
            """);
        try
        {
            using var pending = manifest.CreateCommand();
            pending.CommandText = """
                SELECT p.sequence,p.source,p.stable,p.page,c.path,c.length,c.mtime FROM
                (SELECT sequence,source,stable,page FROM build.plan_page WHERE done=0 AND sequence>$after ORDER BY sequence LIMIT 4096) p
                LEFT JOIN build.plan_candidate c ON c.sequence=p.sequence AND c.priority=0 ORDER BY p.sequence;
                """;
            pending.Parameters.Add("$after", SqliteType.Integer);
            using var stage = old.CreateCommand();
            stage.CommandText = "INSERT INTO temp.candidate VALUES($sequence,$source,$stable,$page,$path,$length,$mtime)";
            foreach (string name in new[] { "$source", "$stable", "$path" }) stage.Parameters.Add(name, SqliteType.Text);
            foreach (string name in new[] { "$sequence", "$page", "$length", "$mtime" }) stage.Parameters.Add(name, SqliteType.Integer);
            using var matches = old.CreateCommand();
            // 固定由最多 4096 条候选驱动旧清单主键查找，不扫描整份历史清单。
            matches.CommandText = """
                SELECT c.sequence,m.alias,m.source_id,m.stable_id,m.page,m.path,m.length,m.mtime,m.state
                FROM temp.candidate c CROSS JOIN member m ON m.source_id=c.source AND m.stable_id=c.stable AND m.page=c.page
                    AND m.path=c.path AND m.length=c.length AND m.mtime=c.mtime
                WHERE m.state IN ('indexed','skipped');
                """;
            using var copy = manifest.CreateCommand();
            copy.CommandText = "INSERT INTO temp.reuse_page VALUES($sequence,$alias,$source,$stable,$page,$path,$length,$mtime,$state)";
            foreach (string name in new[] { "$alias", "$source", "$stable", "$path", "$state" }) copy.Parameters.Add(name, SqliteType.Text);
            foreach (string name in new[] { "$sequence", "$page", "$length", "$mtime" }) copy.Parameters.Add(name, SqliteType.Integer);
            long after = 0;
            int batches = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Execute(old, "DELETE FROM temp.candidate");
                int pages = 0;
                // 只修改只读连接的私有 TEMP 库；关闭短游标后再写当前任务的清单和计划。
                using (var transaction = old.BeginTransaction(deferred: true))
                {
                    stage.Transaction = transaction;
                    pending.Parameters["$after"].Value = after;
                    using var reader = pending.ExecuteReader();
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        after = reader.GetInt64(0); pages++;
                        if (reader.IsDBNull(4) || reader.GetInt64(5) <= 0) continue;
                        stage.Parameters["$sequence"].Value = after;
                        stage.Parameters["$source"].Value = reader.GetString(1);
                        stage.Parameters["$stable"].Value = reader.GetString(2);
                        stage.Parameters["$page"].Value = reader.GetInt32(3);
                        stage.Parameters["$path"].Value = reader.GetString(4);
                        stage.Parameters["$length"].Value = reader.GetInt64(5);
                        stage.Parameters["$mtime"].Value = reader.GetInt64(6);
                        stage.ExecuteNonQuery();
                    }
                    transaction.Commit();
                }
                if (pages == 0) break;
                int indexed = 0, skipped = 0;
                using (var transaction = manifest.BeginTransaction())
                {
                    using var clear = manifest.CreateCommand();
                    clear.Transaction = transaction;
                    clear.CommandText = "DELETE FROM temp.reuse_page";
                    clear.ExecuteNonQuery();
                    copy.Transaction = transaction;
                    using (var reader = matches.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            token.ThrowIfCancellationRequested();
                            copy.Parameters["$sequence"].Value = reader.GetInt64(0);
                            copy.Parameters["$alias"].Value = reader.GetString(1);
                            copy.Parameters["$source"].Value = reader.GetString(2);
                            copy.Parameters["$stable"].Value = reader.GetString(3);
                            copy.Parameters["$page"].Value = reader.GetInt32(4);
                            copy.Parameters["$path"].Value = reader.GetString(5);
                            copy.Parameters["$length"].Value = reader.GetInt64(6);
                            copy.Parameters["$mtime"].Value = reader.GetInt64(7);
                            string state = reader.GetString(8);
                            copy.Parameters["$state"].Value = state;
                            copy.ExecuteNonQuery();
                            if (state == "indexed") indexed++; else skipped++;
                        }
                    }
                    using var merge = manifest.CreateCommand();
                    merge.Transaction = transaction;
                    merge.CommandText = """
                        INSERT OR REPLACE INTO member SELECT alias,source_id,stable_id,page,path,length,mtime,state FROM temp.reuse_page;
                        UPDATE build.plan_page SET done=1 WHERE sequence IN (SELECT sequence FROM temp.reuse_page);
                        """;
                    merge.ExecuteNonQuery();
                    token.ThrowIfCancellationRequested();
                    transaction.Commit();
                }
                if (indexed + skipped == 0) continue;
                batches++;
                committed?.Invoke(indexed, skipped);
            }
            return batches;
        }
        finally { Execute(manifest, "DROP TABLE IF EXISTS temp.reuse_page"); }
    }
}

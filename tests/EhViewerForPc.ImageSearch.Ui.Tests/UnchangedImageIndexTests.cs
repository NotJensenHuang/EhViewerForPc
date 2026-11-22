using System.Security.Cryptography;
using EhViewerForPc.Services;
using Microsoft.Data.Sqlite;

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public sealed class UnchangedImageIndexTests
{
    [Fact]
    public void MatchesEveryWindowAndRequiresAnExactFirstCandidate()
    {
        DirectoryInfo? workspace = new(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "PRODUCT.md"))) workspace = workspace.Parent;
        string root = Path.Combine(workspace!.FullName, ".qa", "unchanged-index-tests", Guid.NewGuid().ToString("N"));
        string previous = Path.Combine(root, "上一代 #%+");
        Directory.CreateDirectory(previous);
        string oldPath = Path.Combine(previous, "manifest.db");
        const int total = 4097;
        var token = TestContext.Current.CancellationToken;
        try
        {
            using (var old = Open(oldPath))
                Run(old, """
                    CREATE TABLE member(alias TEXT,source_id TEXT,stable_id TEXT,page INTEGER,path TEXT,length INTEGER,mtime INTEGER,state TEXT,PRIMARY KEY(source_id,stable_id,page));
                    WITH RECURSIVE pages(page) AS (VALUES(1) UNION ALL SELECT page+1 FROM pages WHERE page<4097)
                    INSERT INTO member SELECT 'pages/old.png','src','stable',page,'原图.png',1,2,
                        CASE WHEN page=4097 THEN 'skipped' ELSE 'indexed' END FROM pages;
                    """);
            byte[] oldHash = SHA256.HashData(File.ReadAllBytes(oldPath));
            using var manifest = Open(new Uri(Path.Combine(root, "当前代 #%.db")).AbsoluteUri);
            Run(manifest, """
                ATTACH DATABASE ':memory:' AS build;
                CREATE TABLE build.plan_page(sequence INTEGER PRIMARY KEY,source TEXT,stable TEXT,page INTEGER);
                CREATE TABLE build.plan_candidate(sequence INTEGER,priority INTEGER,path TEXT,length INTEGER,mtime INTEGER,PRIMARY KEY(sequence,priority));
                WITH RECURSIVE pages(page) AS (VALUES(1) UNION ALL SELECT page+1 FROM pages WHERE page<4097)
                INSERT INTO build.plan_page SELECT page,'src','stable',page FROM pages;
                INSERT INTO build.plan_candidate SELECT sequence,0,'原图.png',1,2 FROM build.plan_page;
                """);
            var matched = LocalImageSearchService.TryReuseUnchangedIndex(manifest, previous, total, token);
            Assert.NotNull(matched);
            Assert.Equal(total - 1, matched.Indexed);
            Assert.Equal(1, matched.Skipped);
            Assert.Equal(total, matched.Reused);

            // 第二个窗口中的原候选降为次选，计划不能复用旧的发布代。
            Run(manifest, """
                UPDATE build.plan_candidate SET priority=1 WHERE sequence=4097;
                INSERT INTO build.plan_candidate VALUES(4097,0,'新图.png',1,2);
                """);
            Assert.Null(LocalImageSearchService.TryReuseUnchangedIndex(manifest, previous, total, token));
            Assert.Null(LocalImageSearchService.TryReuseUnchangedIndex(manifest, previous, total - 1, token));
            Assert.Throws<OperationCanceledException>(() => LocalImageSearchService.TryReuseUnchangedIndex(
                manifest, previous, total, new CancellationToken(canceled: true)));
            Assert.Equal(oldHash, SHA256.HashData(File.ReadAllBytes(oldPath)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

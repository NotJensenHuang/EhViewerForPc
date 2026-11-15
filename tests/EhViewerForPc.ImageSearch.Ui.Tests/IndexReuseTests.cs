using System.Security.Cryptography;
using EhViewerForPc.Services;
using Microsoft.Data.Sqlite;

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public sealed class IndexReuseTests
{
    [Fact]
    public void BulkReuseChecksSignaturesAndCandidatePriorityWithoutChangingTheOldManifest()
    {
        using var fixture = new Fixture(12);
        fixture.Run("""
            UPDATE build.plan_candidate SET length=2 WHERE sequence=2;
            UPDATE build.plan_candidate SET mtime=2 WHERE sequence=3;
            UPDATE build.plan_candidate SET path='changed.png' WHERE sequence=4;
            UPDATE build.plan_page SET stable='other' WHERE sequence=5;
            UPDATE build.plan_page SET source='other' WHERE sequence=6;
            DELETE FROM build.plan_candidate WHERE sequence=7;
            UPDATE build.plan_candidate SET priority=1 WHERE sequence=8;
            UPDATE build.plan_candidate SET length=0 WHERE sequence=9;
            """);
        byte[] before = fixture.OldHash();
        int pages = 0;
        Assert.Equal(1, LocalImageSearchService.ReusePlannedPages(fixture.Manifest, fixture.Previous, TestContext.Current.CancellationToken,
            (indexed, skipped) => pages += indexed + skipped));
        Assert.Equal(4, pages);
        Assert.Equal(4, fixture.Count("member"));
        Assert.Equal(4, fixture.Count("build.plan_page WHERE done=1"));
        Assert.Equal(before, fixture.OldHash());
    }

    [Fact]
    public void BulkReuseCancellationKeepsCommittedWindowsAndResumesOnlyPendingPages()
    {
        using var fixture = new Fixture(9000);
        byte[] before = fixture.OldHash();
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => LocalImageSearchService.ReusePlannedPages(fixture.Manifest,
            fixture.Previous, cancellation.Token, (_, _) => cancellation.Cancel()));
        Assert.Equal(4096, fixture.Count("member"));
        Assert.Equal(4096, fixture.Count("build.plan_page WHERE done=1"));
        int pages = 0;
        Assert.Equal(2, LocalImageSearchService.ReusePlannedPages(fixture.Manifest, fixture.Previous, TestContext.Current.CancellationToken,
            (indexed, skipped) => pages += indexed + skipped));
        Assert.Equal(4904, pages);
        Assert.Equal(9000, fixture.Count("member"));
        Assert.Equal(9000, fixture.Count("build.plan_page WHERE done=1"));
        Assert.Equal(before, fixture.OldHash());
    }

    [Fact]
    public void BulkReuseWriteFailureRollsBackBothMembershipAndCompletion()
    {
        using var fixture = new Fixture(20);
        fixture.Run("CREATE TRIGGER build.fail_completion BEFORE UPDATE OF done ON plan_page BEGIN SELECT RAISE(ABORT,'fixture'); END");
        Assert.Throws<SqliteException>(() => LocalImageSearchService.ReusePlannedPages(fixture.Manifest, fixture.Previous, TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Count("member"));
        Assert.Equal(0, fixture.Count("build.plan_page WHERE done=1"));
        fixture.Run("DROP TRIGGER build.fail_completion");
        LocalImageSearchService.ReusePlannedPages(fixture.Manifest, fixture.Previous, TestContext.Current.CancellationToken);
        Assert.Equal(20, fixture.Count("member"));
        Assert.Equal(20, fixture.Count("build.plan_page WHERE done=1"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        internal string Previous { get; }
        internal SqliteConnection Manifest { get; }
        internal Fixture(int pages)
        {
            DirectoryInfo? workspace = new(AppContext.BaseDirectory);
            while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "PRODUCT.md"))) workspace = workspace.Parent;
            _root = Path.Combine(workspace!.FullName, ".qa/index-reuse-tests", Guid.NewGuid().ToString("N"));
            Previous = Path.Combine(_root, "旧 清单 #1"); Directory.CreateDirectory(Previous);
            const string schema = "CREATE TABLE member(alias TEXT,source_id TEXT,stable_id TEXT,page INTEGER,path TEXT,length INTEGER,mtime INTEGER,state TEXT,PRIMARY KEY(source_id,stable_id,page))";
            using (var old = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Previous, "manifest.db"), Pooling = false }.ToString()))
            {
                old.Open();
                using var command = old.CreateCommand();
                command.CommandText = schema; command.ExecuteNonQuery();
                using var transaction = old.BeginTransaction(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO member VALUES('pages/test.png','source','stable',$page,'test.png',1,1,CASE WHEN $page%2=0 THEN 'indexed' ELSE 'skipped' END)";
                var page = command.Parameters.Add("$page", SqliteType.Integer);
                for (int i = 1; i <= pages; i++) { page.Value = i; command.ExecuteNonQuery(); }
                transaction.Commit();
            }
            Manifest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "manifest.db"), Pooling = false }.ToString());
            Manifest.Open(); Run(schema);
            using var attach = Manifest.CreateCommand();
            attach.CommandText = "ATTACH DATABASE $path AS build";
            attach.Parameters.AddWithValue("$path", Path.Combine(_root, "build-plan.db")); attach.ExecuteNonQuery();
            Run("""
                CREATE TABLE build.plan_page(sequence INTEGER PRIMARY KEY,source TEXT,stable TEXT,page INTEGER,done INTEGER DEFAULT 0);
                CREATE INDEX build.plan_pending ON plan_page(done,sequence);
                CREATE TABLE build.plan_candidate(sequence INTEGER,priority INTEGER,path TEXT,length INTEGER,mtime INTEGER,PRIMARY KEY(sequence,priority));
                """);
            using var insert = Manifest.CreateCommand(); using var tx = Manifest.BeginTransaction(); insert.Transaction = tx;
            insert.CommandText = "INSERT INTO build.plan_page VALUES($page,'source','stable',$page,0);INSERT INTO build.plan_candidate VALUES($page,0,'test.png',1,1)";
            var number = insert.Parameters.Add("$page", SqliteType.Integer);
            for (int i = 1; i <= pages; i++) { number.Value = i; insert.ExecuteNonQuery(); }
            tx.Commit();
        }
        internal void Run(string sql) { using var command = Manifest.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        internal long Count(string from) { using var command = Manifest.CreateCommand(); command.CommandText = "SELECT count(*) FROM " + from; return (long)command.ExecuteScalar()!; }
        internal byte[] OldHash() { using var file = File.OpenRead(Path.Combine(Previous, "manifest.db")); return SHA256.HashData(file); }
        public void Dispose() { Manifest.Dispose(); Directory.Delete(_root, recursive: true); }
    }
}

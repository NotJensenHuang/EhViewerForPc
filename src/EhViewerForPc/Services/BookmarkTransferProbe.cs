using System.Text.Json;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Core.Storage;
using Microsoft.Data.Sqlite;

namespace EhViewerForPc.Services;

/// <summary>Checks deployed bookmark persistence and native SQLite without initializing Avalonia or opening a window.</summary>
internal static class BookmarkTransferProbe
{
    internal static int Run(string root, string? output)
    {
        var checks = new List<(string Name, bool Passed)>(); string? error = null;
        try
        {
            root = Path.GetFullPath(root);
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidOperationException("Probe root must be empty.");
            Directory.CreateDirectory(root);
            string input = Path.Combine(root, "original.db"), exported = Path.Combine(root, "exported.db"), json = Path.Combine(root, "bookmarks.json");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = input, Pooling = false }.ToString()))
            {
                connection.Open(); using var command = connection.CreateCommand(); command.CommandText = """
                    PRAGMA user_version=8;
                    CREATE TABLE QUICK_SEARCH (_id INTEGER PRIMARY KEY,NAME TEXT,MODE INTEGER NOT NULL,CATEGORY INTEGER NOT NULL,KEYWORD TEXT,ADVANCE_SEARCH INTEGER NOT NULL,MIN_RATING INTEGER NOT NULL,PAGE_FROM INTEGER NOT NULL,PAGE_TO INTEGER NOT NULL,TIME INTEGER NOT NULL);
                    INSERT INTO QUICK_SEARCH VALUES(1,'订阅书签',5,6,'artist:"sample$" chinese',2047,4,10,250,1700000000000);
                    """; command.ExecuteNonQuery();
            }
            var bookmarks = EhBookmarkTransfer.Read(input); var original = bookmarks.Single().ToRequest();
            checks.Add(("android-db-import", original.Mode == EhSearchMode.Subscription && original.DisableTagFilter && original.SearchTorrentNames && original.MinRating == 4));
            EhBookmarkTransfer.WriteDatabase(exported, bookmarks);
            var restored = EhBookmarkTransfer.Read(exported).Single();
            checks.Add(("android-db-roundtrip", EhUrls.Search(EhSite.EHentai, original, 0) == EhUrls.Search(EhSite.EHentai, restored.ToRequest(), 0)));
            using (var stream = File.Create(json)) EhBookmarkTransfer.WriteJson(stream, bookmarks);
            checks.Add(("android-json-roundtrip", EhUrls.Search(EhSite.EHentai, original, 0) == EhUrls.Search(EhSite.EHentai, EhBookmarkTransfer.Read(json).Single().ToRequest(), 0)));
            var settings = new AppSettings { SavedSearches = bookmarks };
            var persisted = JsonSerializer.Deserialize(JsonSerializer.Serialize(settings, StorageJsonContext.Default.AppSettings), StorageJsonContext.Default.AppSettings)!;
            checks.Add(("aot-settings-metadata", persisted.SavedSearches.Single().ToRequest().SearchDownvotedTags));
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = exported, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
                checks.Add(("android-export-all-tables", (long)command.ExecuteScalar()! == 10));
            }
            checks.Add(("merge-preserves-existing", EhBookmarkTransfer.Merge(bookmarks, EhBookmarkTransfer.Read(exported)) == (0, 1) && bookmarks.Count == 1));
        }
        catch (Exception failure) { error = failure.GetType().Name + ": " + failure.Message; checks.Add(("unhandled", false)); }
        bool passed = checks.All(c => c.Passed);
        using var report = File.Create(output ?? Path.Combine(root, "bookmark-probe.json"));
        using var writer = new Utf8JsonWriter(report, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject(); writer.WriteBoolean("passed", passed); writer.WriteStartArray("checks");
        foreach (var check in checks) { writer.WriteStartObject(); writer.WriteString("id", check.Name); writer.WriteBoolean("passed", check.Passed); writer.WriteEndObject(); }
        writer.WriteEndArray(); if (error is not null) writer.WriteString("error", error); writer.WriteEndObject(); writer.Flush();
        return passed ? 0 : 3;
    }
}

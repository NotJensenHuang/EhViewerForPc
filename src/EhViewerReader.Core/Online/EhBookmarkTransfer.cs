using System.Text.Json;
using EhViewerReader.Core.Models;
using Microsoft.Data.Sqlite;

namespace EhViewerReader.Core.Online;

/// <summary>Transfers QuickSearch bookmarks using Android schema-8 backups and JSON records.</summary>
public static class EhBookmarkTransfer
{
    /// <summary>Gets the maximum bookmark count accepted in one transfer.</summary>
    public const int MaxEntries = 10000;

    /// <summary>Reads an Android backup without modifying its contents or opening any other tables.</summary>
    public static List<SavedSearchEntry> Read(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 256L * 1024 * 1024) throw new InvalidDataException("备份超过 256 MiB。");
        Span<byte> header = stackalloc byte[16];
        int read = stream.Read(header);
        if (read == 16 && header.SequenceEqual("SQLite format 3\0"u8)) return ReadDatabase(path);
        stream.Position = 0;
        if (stream.Length > 32L * 1024 * 1024) throw new InvalidDataException("JSON 书签超过 32 MiB。");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
        JsonElement array = document.RootElement;
        if (array.ValueKind == JsonValueKind.Object && array.TryGetProperty("data", out var data)) array = data;
        if (array.ValueKind == JsonValueKind.Object && array.TryGetProperty("quick_search", out var bookmarks)) array = bookmarks;
        if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("没有找到 quick_search 书签数组。");
        if (array.GetArrayLength() > MaxEntries) throw new InvalidDataException("书签数量超过上限。");
        var result = new List<SavedSearchEntry>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("书签记录无效。");
            result.Add(FromRaw(Text(item, "name"), Number(item, "mode"), Number(item, "category", -1),
                Text(item, "keyword"), Number(item, "advanceSearch", -1), Number(item, "minRating", -1),
                Number(item, "pageFrom", -1), Number(item, "pageTo", -1), Long(item, "time"), Number(item, "toplistIndex", 15)));
        }
        return result;
    }

    private static List<SavedSearchEntry> ReadDatabase(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.GetFullPath(path), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT type FROM sqlite_master WHERE name='QUICK_SEARCH' COLLATE NOCASE";
        if (command.ExecuteScalar() as string != "table") throw new InvalidDataException("备份中没有 QUICK_SEARCH 表。");
        command.CommandText = "PRAGMA table_info(QUICK_SEARCH)";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = command.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
        string[] required = ["NAME", "MODE", "CATEGORY", "KEYWORD", "ADVANCE_SEARCH", "MIN_RATING", "TIME"];
        if (required.Any(c => !columns.Contains(c))) throw new InvalidDataException("QUICK_SEARCH 字段不完整。");
        command.CommandText = "SELECT NAME,MODE,CATEGORY,KEYWORD,ADVANCE_SEARCH,MIN_RATING," +
            (columns.Contains("PAGE_FROM") ? "PAGE_FROM" : "-1") + "," + (columns.Contains("PAGE_TO") ? "PAGE_TO" : "-1") +
            ",TIME," + (columns.Contains("READER_TOPLIST") ? "READER_TOPLIST" : "15") + " FROM QUICK_SEARCH ORDER BY TIME,_id LIMIT 10001";
        var result = new List<SavedSearchEntry>();
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            if (result.Count >= MaxEntries) throw new InvalidDataException("书签数量超过上限。");
            result.Add(FromRaw(rows.IsDBNull(0) ? "" : rows.GetString(0), rows.GetInt32(1), rows.GetInt32(2),
                rows.IsDBNull(3) ? "" : rows.GetString(3), rows.GetInt32(4), rows.GetInt32(5),
                rows.GetInt32(6), rows.GetInt32(7), rows.GetInt64(8), rows.GetInt32(9)));
        }
        return result;
    }

    /// <summary>Writes an Android-importable backup with empty non-bookmark tables, replacing the destination atomically.</summary>
    public static void WriteDatabase(string path, IReadOnlyList<SavedSearchEntry> entries)
    {
        if (entries.Count > MaxEntries) throw new InvalidDataException("书签数量超过上限。");
        string temporary = Path.GetFullPath(path) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = temporary, Pooling = false }.ToString()))
            {
                connection.Open();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                using var schema = typeof(EhBookmarkTransfer).Assembly.GetManifestResourceStream("EhViewerReader.Core.Online.EhBookmarkSchema.sql")!;
                using var reader = new StreamReader(schema);
                command.CommandText = reader.ReadToEnd(); command.ExecuteNonQuery();
                // Optional trailing column is ignored by greenDAO; preserves desktop leaderboard periods.
                command.CommandText = "ALTER TABLE QUICK_SEARCH ADD COLUMN READER_TOPLIST INTEGER NOT NULL DEFAULT 15"; command.ExecuteNonQuery();
                for (int index = 0; index < entries.Count; index++)
                {
                    SavedSearchEntry entry = entries[index]; EhSearchRequest request = entry.ToRequest();
                    Validate(entry.Name, request.Mode, request.CategoryMask, Advance(request), request.MinRating, request.MinPages, request.MaxPages);
                    command.CommandText = "INSERT INTO QUICK_SEARCH(NAME,MODE,CATEGORY,KEYWORD,ADVANCE_SEARCH,MIN_RATING,PAGE_FROM,PAGE_TO,TIME,READER_TOPLIST) VALUES($name,$mode,$category,$keyword,$advance,$rating,$from,$to,$time,$toplist)";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("$name", entry.Name);
                    command.Parameters.AddWithValue("$mode", (int)request.Mode);
                    command.Parameters.AddWithValue("$category", request.CategoryMask == EhCategories.All ? -1 : request.CategoryMask);
                    command.Parameters.AddWithValue("$keyword", request.EffectiveQuery);
                    command.Parameters.AddWithValue("$advance", Advance(request));
                    command.Parameters.AddWithValue("$rating", request.MinRating > 0 ? request.MinRating : -1);
                    command.Parameters.AddWithValue("$from", request.MinPages > 0 ? request.MinPages : -1);
                    command.Parameters.AddWithValue("$to", request.MaxPages > 0 ? request.MaxPages : -1);
                    // Android sorts by TIME; emit list order even after explicit reordering.
                    command.Parameters.AddWithValue("$time", index + 1L);
                    command.Parameters.AddWithValue("$toplist", request.ToplistIndex);
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Writes the JSON records understood by Android QuickSearch's JSON reader.</summary>
    public static void WriteJson(Stream stream, IReadOnlyList<SavedSearchEntry> entries)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject(); writer.WritePropertyName("quick_search"); writer.WriteStartArray();
        for (int index = 0; index < entries.Count; index++)
        {
            var entry = entries[index]; var r = entry.ToRequest();
            writer.WriteStartObject(); writer.WriteString("name", entry.Name); writer.WriteNumber("mode", (int)r.Mode);
            writer.WriteNumber("category", r.CategoryMask == EhCategories.All ? -1 : r.CategoryMask);
            writer.WriteString("keyword", r.EffectiveQuery); writer.WriteNumber("advanceSearch", Advance(r));
            writer.WriteNumber("minRating", r.MinRating > 0 ? r.MinRating : -1);
            writer.WriteNumber("pageFrom", r.MinPages > 0 ? r.MinPages : -1); writer.WriteNumber("pageTo", r.MaxPages > 0 ? r.MaxPages : -1);
            writer.WriteNumber("time", index + 1L); writer.WriteNumber("toplistIndex", r.ToplistIndex); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();
    }

    /// <summary>Merges parsed bookmarks without overwriting existing names; returns added and skipped counts.</summary>
    public static (int Added, int Skipped) Merge(List<SavedSearchEntry> destination, IEnumerable<SavedSearchEntry> incoming)
    {
        var names = destination.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        var add = new List<SavedSearchEntry>(); int skipped = 0;
        foreach (var entry in incoming) { if (names.Add(entry.Name)) add.Add(entry); else skipped++; }
        if (destination.Count + add.Count > MaxEntries) throw new InvalidDataException("合并后书签数量超过上限。");
        destination.AddRange(add); return (add.Count, skipped);
    }

    private static SavedSearchEntry FromRaw(string name, int mode, int category, string keyword, int advance, int rating, int from, int to, long time, int toplist)
    {
        Validate(name, (EhSearchMode)mode, category, advance, rating, from, to);
        if (keyword.Length > 8192) throw new InvalidDataException("书签关键词过长。");
        var request = new EhSearchRequest { Mode = (EhSearchMode)mode, Query = keyword,
            CategoryMask = category == -1 ? EhCategories.All : category, AdvancedEnabled = advance >= 0,
            MinRating = Math.Max(0, rating), MinPages = Math.Max(0, from), MaxPages = Math.Max(0, to),
            ToplistIndex = toplist is 11 or 12 or 13 or 15 ? toplist : 15 };
        if (advance >= 0)
        {
            request.SearchName = (advance & 1) != 0; request.SearchTags = (advance & 2) != 0;
            request.SearchDescription = (advance & 4) != 0; request.SearchTorrentNames = (advance & 8) != 0;
            request.OnlyTorrents = (advance & 16) != 0; request.SearchLowPowerTags = (advance & 32) != 0;
            request.SearchDownvotedTags = (advance & 64) != 0; request.OnlyExpunged = (advance & 128) != 0;
            request.DisableLanguageFilter = (advance & 256) != 0; request.DisableUploaderFilter = (advance & 512) != 0;
            request.DisableTagFilter = (advance & 1024) != 0;
        }
        var entry = SavedSearchEntry.Create(name, request); entry.Time = time; return entry;
    }
    private static int Advance(EhSearchRequest r) => !r.AdvancedEnabled ? -1 :
        (r.SearchName ? 1 : 0) | (r.SearchTags ? 2 : 0) | (r.SearchDescription ? 4 : 0) | (r.SearchTorrentNames ? 8 : 0) |
        (r.OnlyTorrents ? 16 : 0) | (r.SearchLowPowerTags ? 32 : 0) | (r.SearchDownvotedTags ? 64 : 0) | (r.OnlyExpunged ? 128 : 0) |
        (r.DisableLanguageFilter ? 256 : 0) | (r.DisableUploaderFilter ? 512 : 0) | (r.DisableTagFilter ? 1024 : 0);
    private static void Validate(string name, EhSearchMode mode, int category, int advance, int rating, int from, int to)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 1024) throw new InvalidDataException("书签名称为空或过长。");
        if (!Enum.IsDefined(mode)) throw new InvalidDataException($"不支持的书签模式：{(int)mode}。");
        if (category is < -1 or > 1023 || advance is < -1 or > 2047 || rating is < -1 or > 5 || from < -1 || to < -1)
            throw new InvalidDataException("书签搜索条件无效。");
    }
    private static string Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null
        ? value.GetString() ?? "" : "";
    private static int Number(JsonElement item, string key, int fallback = 0) => item.TryGetProperty(key, out var value) ? value.GetInt32() : fallback;
    private static long Long(JsonElement item, string key) => item.TryGetProperty(key, out var value) ? value.GetInt64() : 0;
}

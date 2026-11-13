using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Online;
using EhViewerReader.Core.Storage;
using Microsoft.Data.Sqlite;

namespace EhViewerReader.Core.Tests;

public sealed class OnlineParityTests
{
    [Fact]
    public void CompleteAdvancedFormUsesDistinctUpstreamParameters()
    {
        var request = CompleteRequest();
        string url = EhUrls.Search(EhSite.ExHentai, request, 2);
        Assert.StartsWith("https://exhentai.org/watched?f_search=", url);
        foreach (string parameter in new[] { "f_sname", "f_stags", "f_sdesc", "f_storr", "f_sto", "f_sh", "f_sdt1", "f_sdt2", "f_sfl", "f_sfu", "f_sft", "f_sr", "f_sp" })
            Assert.Contains("&" + parameter + "=on", url);
        Assert.Contains("f_srdd=4", url); Assert.Contains("f_spf=12&f_spt=250", url); Assert.Contains("&page=2", url);
        request.AdvancedEnabled = false; url = EhUrls.Search(EhSite.EHentai, request, 0);
        Assert.DoesNotContain("advsearch", url); Assert.DoesNotContain("f_sh", url); Assert.DoesNotContain("f_sp", url);
    }

    [Theory]
    [InlineData(EhSearchMode.Uploader, "/uploader/a%20b%2F%26/3")]
    [InlineData(EhSearchMode.Tag, "/tag/a%20b%2F%26/3")]
    public void DedicatedModesEncodeOnePathComponent(EhSearchMode mode, string suffix)
    {
        string url = EhUrls.Search(EhSite.EHentai, new() { Mode = mode, Query = "a b/&", Language = "chinese" }, 3);
        Assert.Equal("https://e-hentai.org" + suffix, url);
    }

    [Fact]
    public void RankingUsesPublicEndpointAndItsOwnPageParameter()
    {
        Assert.Equal("https://e-hentai.org/toplist.php?tl=13&p=2", EhUrls.Toplist(EhSite.ExHentai, 13, 2));
    }

    [Fact]
    public void JsonFromAndroidPreservesEveryBitAndQuote()
    {
        using var files = new TransferFiles();
        File.WriteAllText(files.Input, """
            {"data":{"quick_search":[
              {"name":"艺术家：\"ohtomo takuji$\" chinese","mode":2,"category":-1,"keyword":"artist:\"ohtomo takuji$\" chinese","advanceSearch":-1,"minRating":-1,"pageFrom":-1,"pageTo":-1,"time":1700000000000},
              {"name":"订阅与高级","mode":5,"category":6,"keyword":"中文 & artist:test","advanceSearch":2047,"minRating":4,"pageFrom":12,"pageTo":250,"time":1700000000010}
            ]}}
            """);
        var entries = EhBookmarkTransfer.Read(files.Input);
        Assert.Equal(2, entries.Count); Assert.Equal(EhCategories.All, entries[0].ToRequest().CategoryMask);
        Assert.False(entries[0].ToRequest().AdvancedEnabled); Assert.Contains("\"ohtomo takuji$\"", entries[0].Query);
        var r = entries[1].ToRequest(); Assert.True(r.SearchTorrentNames && r.OnlyTorrents && r.OnlyExpunged && r.SearchLowPowerTags && r.SearchDownvotedTags);
        Assert.True(r.DisableTagFilter && r.DisableUploaderFilter && r.DisableLanguageFilter);
        Assert.Equal(1700000000010, entries[1].Time);
    }

    [Fact]
    public void OriginalSqliteBackupReadsInOrderWithoutChangingSource()
    {
        using var files = new TransferFiles(); CreateOriginalBackup(files.Input);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(files.Input));
        var entries = EhBookmarkTransfer.Read(files.Input);
        Assert.Equal(new[] { "先显示", "后显示" }, entries.Select(e => e.Name));
        var r = entries[0].ToRequest(); Assert.Equal(EhSearchMode.Subscription, r.Mode); Assert.Equal(6, r.CategoryMask);
        Assert.True(r.OnlyExpunged); Assert.Equal(4, r.MinRating); Assert.Equal(12, r.MinPages); Assert.Equal(250, r.MaxPages);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(files.Input)));
        Assert.False(File.Exists(files.Input + "-wal"));
    }

    [Fact]
    public void DatabaseExportProvidesAllAndroidTablesAndRoundTripsCompleteOptions()
    {
        using var files = new TransferFiles(); var source = SavedSearchEntry.Create("书签\"$ 中文", CompleteRequest());
        EhBookmarkTransfer.WriteDatabase(files.Input, [source]);
        using (var connection = new SqliteConnection($"Data Source={files.Input};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version"; Assert.Equal(8L, command.ExecuteScalar());
            foreach (string table in new[] { "Gallery_Tags", "DOWNLOADS", "DOWNLOAD_LABELS", "DOWNLOAD_DIRNAME", "HISTORY", "QUICK_SEARCH", "Black_List", "LOCAL_FAVORITES", "BOOKMARKS", "FILTER" })
            { command.CommandText = "SELECT COUNT(*) FROM \"" + table + "\""; Assert.Equal(table == "QUICK_SEARCH" ? 1L : 0L, command.ExecuteScalar()); }
            command.CommandText = "SELECT ADVANCE_SEARCH FROM QUICK_SEARCH"; Assert.Equal(2047L, command.ExecuteScalar());
            command.CommandText = "SELECT ARCHIVE_URI FROM DOWNLOADS"; command.ExecuteScalar();
            command.CommandText = "SELECT LOCATION FROM Gallery_Tags"; command.ExecuteScalar();
        }
        var restored = Assert.Single(EhBookmarkTransfer.Read(files.Input));
        Assert.Equal(source.Name, restored.Name);
        Assert.Equal(EhUrls.Search(EhSite.EHentai, source.ToRequest(), 0), EhUrls.Search(EhSite.EHentai, restored.ToRequest(), 0));
    }

    [Fact]
    public void JsonRoundTripAndMergeKeepOrderAndExistingDuplicates()
    {
        using var files = new TransferFiles();
        var first = SavedSearchEntry.Create("同名", CompleteRequest());
        using (var output = File.Create(files.Input)) EhBookmarkTransfer.WriteJson(output, [first, SavedSearchEntry.Create("第二项", new() { Query = "tag" })]);
        var list = new List<SavedSearchEntry> { SavedSearchEntry.Create("同名", new() { Query = "existing" }) };
        var result = EhBookmarkTransfer.Merge(list, EhBookmarkTransfer.Read(files.Input));
        Assert.Equal((1, 1), result); Assert.Equal("existing", list[0].Query); Assert.Equal("第二项", list[1].Name);
    }

    [Fact]
    public void LegacySettingsMigrateSavedSearchAndSerializeCompleteRequestWithoutReflection()
    {
        const string json = """{"schemaVersion":7,"savedSearches":[{"name":"旧书签","query":"artist:test","categoryMask":6,"minPages":5,"maxPages":20,"language":"chinese"}]}""";
        var settings = JsonSerializer.Deserialize(json, StorageJsonContext.Default.AppSettings)!;
        Assert.Equal(5, settings.SavedSearches[0].ToRequest().MinPages);
        settings.SavedSearches.Add(SavedSearchEntry.Create("完整条件", CompleteRequest()));
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(settings, StorageJsonContext.Default.AppSettings), StorageJsonContext.Default.AppSettings)!;
        Assert.True(restored.SavedSearches[1].ToRequest().SearchTorrentNames); Assert.True(restored.SavedSearches[1].ToRequest().DisableTagFilter);
    }

    [Fact]
    public void InvalidImportDoesNotPartiallyMergeAndViewsCannotMasqueradeAsTables()
    {
        using var files = new TransferFiles();
        File.WriteAllText(files.Input, """[{"name":"valid","mode":0},{"name":"unsupported","mode":4}]""");
        var destination = new List<SavedSearchEntry>();
        Assert.Throws<InvalidDataException>(() => EhBookmarkTransfer.Merge(destination, EhBookmarkTransfer.Read(files.Input)));
        Assert.Empty(destination); File.Delete(files.Input);
        using (var connection = new SqliteConnection($"Data Source={files.Input};Pooling=False"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "CREATE VIEW QUICK_SEARCH AS SELECT 1"; command.ExecuteNonQuery(); }
        Assert.Throws<InvalidDataException>(() => EhBookmarkTransfer.Read(files.Input));
    }

    [Fact]
    public void FailedExportKeepsAnExistingBackupIntact()
    {
        using var files = new TransferFiles(); CreateOriginalBackup(files.Input);
        byte[] before = SHA256.HashData(File.ReadAllBytes(files.Input));
        Assert.Throws<InvalidDataException>(() => EhBookmarkTransfer.WriteDatabase(files.Input, [SavedSearchEntry.Create("", new())]));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(files.Input)));
        Assert.Equal(2, EhBookmarkTransfer.Read(files.Input).Count);
    }

    [Fact]
    public void AuthCookiesAreAvailableToTheUpstreamImageUploadHosts()
    {
        var session = new EhSession(); session.Import(new Dictionary<string, string> { ["ipb_member_id"] = "123", ["ipb_pass_hash"] = "fixture" });
        foreach (string upload in new[] { EhUrls.ImageSearch(EhSite.EHentai), EhUrls.ImageSearch(EhSite.ExHentai) })
            Assert.Contains("ipb_member_id=123", session.Cookies.GetCookieHeader(new Uri(upload)));
    }

    [Fact]
    public async Task ImageSearchUsesRealMultipartFieldsAndPreservesResultSession()
    {
        var handler = new CaptureHandler(); using var client = new EhClient(new AppSettings(), new HttpClient(handler));
        var page = await client.SearchImageAsync([255, 216, 255, 217], true, true, true, CancellationToken.None);
        Assert.Equal("https://upld.e-hentai.org/image_lookup.php", handler.Url);
        foreach (string field in new[] { "sfile", "fs_similar", "fs_covers", "fs_exp", "f_sfile" }) Assert.Contains("name=" + field, handler.Body);
        Assert.Contains("query.jpg", handler.Body); Assert.Contains("File Search", handler.Body);
        Assert.Equal("https://e-hentai.org/?f_shash=abc", page.SourceUrl);
        Assert.Equal("https://e-hentai.org/?f_shash=abc&next=123", page.NextUrl);
    }

    [Fact]
    public void RankingOverviewParsesAllSevenKindsAndFourPeriods()
    {
        string html = RankingFixture(); var kinds = EhRankingParser.Parse(html);
        Assert.Equal(7, kinds.Count); Assert.Equal("画廊", kinds[0].Label); Assert.Equal("评分与评论", kinds[6].Label);
        Assert.Equal(new[] { "昨日", "本月", "今年", "全部" }, kinds[0].Periods.Select(p => p.Label));
        Assert.Equal(15, kinds[0].Periods[0].GalleryListIndex); Assert.True(kinds[0].Periods[0].HasGalleryList);
        Assert.Equal("https://e-hentai.org/g/42/token/", kinds[0].Periods[0].Items[0].Href);
        Assert.False(kinds[1].Periods[0].HasGalleryList);
    }

    private static EhSearchRequest CompleteRequest() => new()
    {
        Query = "artist:\"sample$\" 中文 &", Mode = EhSearchMode.Subscription, CategoryMask = 6,
        SearchName = true, SearchTags = true, SearchDescription = true, SearchTorrentNames = true,
        OnlyTorrents = true, OnlyExpunged = true, SearchLowPowerTags = true, SearchDownvotedTags = true,
        DisableLanguageFilter = true, DisableUploaderFilter = true, DisableTagFilter = true, MinRating = 4, MinPages = 12, MaxPages = 250,
    };
    private static void CreateOriginalBackup(string path)
    {
        // Exact original column names and sentinels, without desktop extension columns.
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = """
            PRAGMA user_version=8;
            CREATE TABLE QUICK_SEARCH (_id INTEGER PRIMARY KEY, NAME TEXT, MODE INTEGER NOT NULL, CATEGORY INTEGER NOT NULL, KEYWORD TEXT, ADVANCE_SEARCH INTEGER NOT NULL, MIN_RATING INTEGER NOT NULL, PAGE_FROM INTEGER NOT NULL, PAGE_TO INTEGER NOT NULL, TIME INTEGER NOT NULL);
            INSERT INTO QUICK_SEARCH VALUES(1,'后显示',2,-1,'artist:"sample$" chinese',-1,-1,-1,-1,2000);
            INSERT INTO QUICK_SEARCH VALUES(2,'先显示',5,6,'中文 & artist:test',2047,4,12,250,1000);
            """; command.ExecuteNonQuery();
    }
    private static string RankingFixture()
    {
        var html = new StringBuilder("<div class='ido'><h1>Toplists</h1>");
        for (int kind = 0; kind < 7; kind++)
        {
            html.Append("<div><h2>Kind</h2>");
            for (int period = 0; period < 4; period++) html.Append("<div><h3>Time</h3><table><tr><td class='tun'><a href='/g/42/token/'>Fixture title</a></td></tr></table></div>");
            html.Append("</div><hr>");
        }
        return html.Append("</div>").ToString();
    }
    private sealed class CaptureHandler : HttpMessageHandler
    {
        internal string Url = "", Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.AbsoluteUri; Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://e-hentai.org/?f_shash=abc"),
                Content = new StringContent("<a id='dnext' href='/?f_shash=abc&amp;next=123'>Next</a>"),
            };
        }
    }
    private sealed class TransferFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "EhBookmarks-" + Guid.NewGuid().ToString("N"));
        internal string Input => Path.Combine(_root, "原版 备份.db");
        internal TransferFiles() => Directory.CreateDirectory(_root);
        public void Dispose() => Directory.Delete(_root, true);
    }
}

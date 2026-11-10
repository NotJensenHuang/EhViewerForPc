using EhViewerReader.Core.Models;
using EhViewerReader.Core.Online;

namespace EhViewerReader.Core.Tests;

/// <summary>
/// Regressions for the second wave of online features: torrents, comments,
/// archive options, tag translations and the list filter. Markup is trimmed
/// from the live pages (2026-11).
/// </summary>
public sealed class OnlineExtrasTests
{
    private const string DetailWithActions = """
        <html><body>
        <h1 id="gn">Some Gallery</h1><h1 id="gj"></h1>
        <div id="gdc"><div class="cs ct2">Doujinshi</div></div>
        <div id="gdn"><a href="https://e-hentai.org/uploader/Evyy">Evyy</a></div>
        <div id="gdd"><table>
          <tr><td class="gdt1">Posted:</td><td class="gdt2">2026-11-10 10:05</td></tr>
          <tr><td class="gdt1">Language:</td><td class="gdt2">Japanese</td></tr>
          <tr><td class="gdt1">File Size:</td><td class="gdt2">318.8 MiB</td></tr>
          <tr><td class="gdt1">Length:</td><td class="gdt2">37 pages</td></tr>
          <tr><td class="gdt1">Favorited:</td><td class="gdt2" id="favcount">28 times</td></tr>
        </table></div>
        <div id="rating_label">Average: 4.25</div>
        <div id="rating_count">120</div>
        <div id="gd5">
          <p class="g2 gsp"><a href="#" onclick="return popUp('https://e-hentai.org/archiver.php?gid=4319211&amp;token=5c2103fb19',480,320)">Archive Download</a></p>
          <p class="g2"><a href="#" onclick="return popUp('https://e-hentai.org/gallerytorrents.php?gid=4319211&amp;t=5c2103fb19',610,590)">Torrent Download (2)</a></p>
        </div>
        <div id="cdiv" class="gm">
          <a name="c8802856"></a>
          <div class="c1">
            <div class="c2">
              <div class="c3">Posted on 10 November 2026, 10:18 by: &nbsp; <a href="https://e-hentai.org/uploader/Evyy">Evyy</a>&nbsp;</div>
              <div class="c5 nosel"><span id="comment_score_8802856">+35</span></div>
            </div>
            <div class="c6" id="comment_8802856">The things I would do with Bradamante&#039;s ass.</div>
            <div class="c7" id="cvotes_8802856" style="display:none">Base +23, <span>Epherion +12</span></div>
          </div>
          <div id="chd"><p id="postnewcomment">[<a href="#">Post New Comment</a>]</p></div>
          <div id="formdiv"><textarea name="commenttext_new"></textarea></div>
        </div>
        <script>
          var gid = 4319211;
          var token = "5c2103fb19";
          var apiuid = 123456;
          var apikey = "341ba9d027ae772a1d4c";
        </script>
        </body></html>
        """;

    private const string TorrentPage = """
        <html><body>
        <form method="post" action="https://e-hentai.org/gallerytorrents.php?gid=4319211&amp;t=5c2103fb19">
        <div>
        <table style="width:99%">
        <tr>
         <td style="width:180px"><span style="font-weight:bold">Posted:</span> <span>2026-11-10 10:05</span></td>
         <td style="width:150px"><span style="font-weight:bold">Size:</span> 318.8 MiB</td>
         <td></td>
         <td style="width:80px"><span style="font-weight:bold">Seeds:</span> 8</td>
         <td style="width:80px"><span style="font-weight:bold">Peers:</span> 0</td>
         <td style="width:100px; text-align:center"><span style="font-weight:bold">Downloads:</span> 23</td>
        </tr>
        <tr>
         <td colspan="5"><span style="font-weight:bold">Uploader:</span> -Lumina-</td>
         <td rowspan="2"><input type="submit" name="torrent_info" value="Information" /></td>
        </tr>
        <tr>
         <td colspan="5"> &nbsp; <a href="https://ehtracker.org/get/4319211/4xbbu9q5lzqxfufb7v/abc.torrent">(C95) [Uguisuya] HH[K2].zip</a></td>
        </tr>
        </table>
        </div>
        </form>
        <form method="post" action="https://e-hentai.org/gallerytorrents.php?gid=4319211&amp;t=5c2103fb19">
        <div>
        <table>
        <tr>
         <td><span style="font-weight:bold">Posted:</span> <span>2026-11-10 10:50</span></td>
         <td><span style="font-weight:bold">Size:</span> 4.90 MiB</td>
         <td><span style="font-weight:bold">Seeds:</span> 2</td>
         <td><span style="font-weight:bold">Peers:</span> 1</td>
         <td><span style="font-weight:bold">Downloads:</span> 7</td>
        </tr>
        <tr><td colspan="5"><span style="font-weight:bold">Uploader:</span> someone</td></tr>
        <tr><td colspan="5"><a href="https://ehtracker.org/get/4319211/xyz/def.torrent?p=2">small.zip</a></td></tr>
        </table>
        </div>
        </form>
        </body></html>
        """;

    private const string ArchiverPage = """
        <html><body>
        <div id="db"><p>Select the archive type you want to download.</p></div>
        <form id="hathdl_form" action="https://e-hentai.org/archiver.php?gid=4319211&amp;token=5c2103fb19&amp;or=5f3a" method="post">
          <div>
            <a href="#" onclick="return do_hathdl('org')">Original Files</a>
            <a href="#" onclick="return do_hathdl('res')">Resampled Images</a>
          </div>
        </form>
        </body></html>
        """;

    [Fact]
    public void DetailParsesActionsApiIdentityAndComments()
    {
        EhGalleryDetails details = EhParser.ParseGalleryDetail(DetailWithActions, EhSite.EHentai, 4319211, "5c2103fb19");

        Assert.Equal(2, details.TorrentCount);
        Assert.Equal(EhUrls.Torrents(EhSite.EHentai, 4319211, "5c2103fb19"), details.TorrentUrl);
        Assert.Equal("https://e-hentai.org/archiver.php?gid=4319211&token=5c2103fb19", details.ArchiveUrl);
        Assert.Equal(123456, details.ApiUid);
        Assert.Equal("341ba9d027ae772a1d4c", details.ApiKey);
        Assert.True(details.CanUseApi);
        Assert.Equal(120, details.RatingCount);

        EhComment comment = Assert.Single(details.Comments);
        Assert.Equal(8802856, comment.Id);
        Assert.Equal("Evyy", comment.User);
        Assert.Equal("10 November 2026, 10:18", comment.Posted);
        Assert.Equal(35, comment.Score);
        Assert.Equal("+35", comment.ScoreText);
        Assert.Contains("Bradamante", comment.Text);
        Assert.Equal("Base +23, Epherion +12", comment.VoteDetail);
        Assert.True(details.CanPostComment);
        Assert.False(comment.IsUploader); // uploader flag is applied by the view model
    }

    [Fact]
    public void DetailReadsFavoriteStateFromLinkText()
    {
        const string favorited = """
            <html><body><h1 id="gn">Fav</h1>
            <div id="gdf" onclick="return pop_fav()"><div id="fav"></div>
              <div>&nbsp; <a id="favoritelink" href="#"><img src="/g/mr.gif" /> Favorite 3</a></div></div>
            </body></html>
            """;

        EhGalleryDetails on = EhParser.ParseGalleryDetail(favorited, EhSite.EHentai, 1, "tok");
        Assert.True(on.IsFavorited);
        Assert.Equal("Favorite 3", on.FavoriteName);
        Assert.True(on.IsFavorite);

        const string plain = """
            <html><body><h1 id="gn">New</h1>
            <div id="gdf"><a id="favoritelink" href="#"><img src="/g/mr.gif" /> Add to Favorites</a></div>
            </body></html>
            """;

        EhGalleryDetails off = EhParser.ParseGalleryDetail(plain, EhSite.EHentai, 1, "tok");
        Assert.False(off.IsFavorited);
        Assert.False(off.IsFavorite);
    }

    [Fact]
    public void FavoritePopupParsesSelectedSlot()
    {
        const string popup = """
            <html><body><form method="post" action="gallerypopups.php?gid=1&amp;t=tok&amp;act=addfav">
            <select name="favcat">
              <option value="0">Favorite 0</option>
              <option value="3" selected="selected">Favorite 3</option>
            </select>
            <input type="submit" name="delete" value="Remove from Favorites" />
            <input type="submit" name="apply" value="Apply Changes" />
            </form></body></html>
            """;

        Assert.Equal(3, EhParser.ParseFavoriteSlot(popup));
        Assert.Equal(-1, EhParser.ParseFavoriteSlot("<html><body><p>not a popup</p></body></html>"));
    }

    [Fact]
    public void DetailWithoutCommentsStillParses()
    {
        const string html = """
            <html><body><h1 id="gn">Bare</h1><div id="gdd"><table>
            <tr><td class="gdt1">Length:</td><td class="gdt2">3 pages</td></tr>
            </table></div></body></html>
            """;

        EhGalleryDetails details = EhParser.ParseGalleryDetail(html, EhSite.EHentai, 1, "tok");

        Assert.Empty(details.Comments);
        Assert.Empty(details.TorrentUrl);
        Assert.Equal(-1, details.ApiUid);
        Assert.False(details.CanUseApi);
    }

    [Fact]
    public void TorrentPageParsesEachForm()
    {
        EhTorrentList list = EhParser.ParseTorrents(TorrentPage, EhSite.EHentai);

        Assert.Equal(2, list.Items.Count);

        EhTorrent first = list.Items[0];
        Assert.Equal("(C95) [Uguisuya] HH[K2].zip", first.Name);
        Assert.Equal("https://ehtracker.org/get/4319211/4xbbu9q5lzqxfufb7v/abc.torrent", first.Url);
        Assert.Equal("318.8 MiB", first.Size);
        Assert.Equal("2026-11-10 10:05", first.Posted);
        Assert.Equal(8, first.Seeds);
        Assert.Equal(0, first.Peers);
        Assert.Equal(23, first.Downloads);
        Assert.Equal("-Lumina-", first.Uploader);

        // The redistributable form: "?p=" is a page hint and must be stripped.
        Assert.Equal("https://ehtracker.org/get/4319211/xyz/def.torrent", list.Items[1].Url);
    }

    [Fact]
    public void TorrentPageWithoutEntriesReportsMessage()
    {
        EhTorrentList list = EhParser.ParseTorrents("<html><body><p>No torrents found</p></body></html>", EhSite.EHentai);

        Assert.Empty(list.Items);
        Assert.False(string.IsNullOrWhiteSpace(list.Message));
    }

    [Fact]
    public void ArchiverPageParsesOrAndOptions()
    {
        EhArchivePage page = EhParser.ParseArchivePage(ArchiverPage, EhSite.EHentai);

        Assert.Equal("5f3a", page.Or);
        Assert.Equal(2, page.Options.Count);
        Assert.Equal("org", page.Options[0].Key);
        Assert.Equal("Original Files", page.Options[0].Label);
        Assert.Equal("res", page.Options[1].Key);
        Assert.False(page.RequiresSignIn);
    }

    [Fact]
    public void ArchiverPageWithoutLoginIsFlagged()
    {
        const string html = "<html><body><p>This page requires you to log on.</p></body></html>";

        EhArchivePage page = EhParser.ParseArchivePage(html, EhSite.EHentai);

        Assert.True(page.RequiresSignIn);
        Assert.Contains("登录", page.Message);
    }

    [Fact]
    public void ArchiveResultClassifiesOutcomes()
    {
        EhArchiveResult hath = EhParser.ParseArchiveResult(
            "<html><body>You must have a H@H client assigned to your account to use this feature.</body></html>",
            EhSite.EHentai);
        Assert.True(hath.NeedsHathClient);
        Assert.False(hath.Success);

        EhArchiveResult ready = EhParser.ParseArchiveResult(
            "<html><body><a href=\"/archives/4319211-5f3a.zip\">Download</a></body></html>",
            EhSite.EHentai);
        Assert.True(ready.Success);
        Assert.Equal("https://e-hentai.org/archives/4319211-5f3a.zip", ready.DownloadUrl);

        EhArchiveResult queued = EhParser.ParseArchiveResult(
            "<html><body><p>Your archive is being generated.</p></body></html>",
            EhSite.EHentai);
        Assert.True(queued.Success);
        Assert.True(queued.Queued);
    }

    [Fact]
    public void TagTranslationParsesNamespacesAndTags()
    {
        const string json = """
            {
              "version": 7,
              "data": [
                { "namespace": "rows", "data": {
                    "female": { "name": "女性" },
                    "language": { "name": "语言" }
                } },
                { "namespace": "female", "data": {
                    "sole female": { "name": "单女主" },
                    "big breasts": { "name": "巨乳" }
                } },
                { "namespace": "language", "data": {
                    "chinese": { "name": "中文" }
                } }
              ]
            }
            """;

        TagTranslationDatabase database = TagTranslationParser.Parse(json);

        Assert.Equal("7", database.Version);
        Assert.Equal(3, database.Count);
        Assert.Equal("单女主", database.Translate("female", "sole female"));
        Assert.Equal("中文", database.Translate("language", "chinese"));
        Assert.Null(database.Translate("female", "unknown"));
        Assert.Equal("女性", database.NamespaceName("female"));
        Assert.Equal("artist", database.NamespaceName("artist"));
    }

    [Fact]
    public void FilterRespectsExcludeAndIncludeRules()
    {
        var item = new EhGallerySummary { Gid = 1, Title = "Some Title", Uploader = "Evyy" };
        item.Tags.Add("female:sole female");
        item.Tags.Add("language:chinese");

        Assert.True(EhFilter.IsVisible(item, []));

        var exclude = new List<FilterRule> { new() { Text = "sole female", Target = FilterTarget.Tag } };
        Assert.False(EhFilter.IsVisible(item, exclude));

        var otherExclude = new List<FilterRule> { new() { Text = "guro", Target = FilterTarget.Tag } };
        Assert.True(EhFilter.IsVisible(item, otherExclude));

        var includeHit = new List<FilterRule> { new() { Text = "chinese", Target = FilterTarget.Tag, Include = true } };
        Assert.True(EhFilter.IsVisible(item, includeHit));

        var includeMiss = new List<FilterRule> { new() { Text = "english", Target = FilterTarget.Tag, Include = true } };
        Assert.False(EhFilter.IsVisible(item, includeMiss));

        var uploader = new List<FilterRule> { new() { Text = "evyy", Target = FilterTarget.Uploader } };
        Assert.False(EhFilter.IsVisible(item, uploader));

        var disabled = new List<FilterRule> { new() { Text = "sole female", Enabled = false } };
        Assert.True(EhFilter.IsVisible(item, disabled));
    }

    [Fact]
    public void ListRowsExposeTagChipsForFiltering()
    {
        const string compact = """
            <table class="itg gltc"><tr>
              <td class="gl1c glcat"><div class="cn ct1">Misc</div></td>
              <td class="gl2c"><div class="glthumb"><img src="https://ehgt.org/w/02/1.webp" /></div></td>
              <td class="gl3c glname">
                <a href="https://e-hentai.org/g/4319235/fe490a0209/">
                  <div class="glink">Real Title Only</div>
                  <div><div class="gt" title="language:chinese">chinese</div><div class="gt" title="other:ai generated">ai generated</div></div>
                </a>
              </td>
            </tr></table>
            """;

        EhGalleryListPage page = EhParser.ParseGalleryList(compact, EhSite.EHentai, 0);

        EhGallerySummary item = Assert.Single(page.Items);
        Assert.Equal(new[] { "language:chinese", "other:ai generated" }, item.Tags);
    }
}

using EhViewerForPc.Core.Online;

namespace EhViewerForPc.Core.Tests;

/// <summary>
/// Parser regressions captured from live E-Hentai markup (2026-11). The site's
/// current layout differs from the classic documentation: metadata lives in
/// #gdd rows, tags start with a namespace cell, previews are sprite slices and
/// viewer URLs are /s/{hash}/{gid}-{page}.
/// </summary>
public sealed class OnlineParserTests
{
    [Theory]
    [InlineData(" https://e-hentai.org/g/42/abcdef1234/?p=2#comments  ", EhSite.EHentai)]
    [InlineData("http://EXHENTAI.org/g/42/abcdef1234", EhSite.ExHentai)]
    [InlineData("exhentai.org/g/42/abcdef1234/", EhSite.ExHentai)]
    public void PastedGalleryLinkKeepsItsSiteAndGalleryIdentity(string link, EhSite expectedSite)
    {
        Assert.True(EhUrls.TryParseGalleryUrl(link, out EhSite site, out long gid, out string token));
        Assert.Equal(expectedSite, site);
        Assert.Equal(42, gid);
        Assert.Equal("abcdef1234", token);
    }

    [Theory]
    [InlineData("gid:42")]
    [InlineData("https://e-hentai.org/s/abcdef1234/42-1")]
    [InlineData("https://e-hentai.org.example.org/g/42/abcdef1234/")]
    [InlineData("https://example.org/?url=https://exhentai.org/g/42/abcdef1234/")]
    [InlineData("https://exhentai.org/g/42/abcdef1234/extra")]
    [InlineData("https://exhentai.org/g/0/abcdef1234/")]
    [InlineData("https://exhentai.org/g/42/")]
    [InlineData("https://exhentai.org/g/42/abc%2Fdef/")]
    public void OtherQueriesDoNotOpenGalleryDetails(string query) =>
        Assert.False(EhUrls.TryParseGalleryUrl(query, out _, out _, out _));

    // 安卓上游 GalleryDetailParser 从 #gd1 子元素的 CSS 中读取封面。
    [Theory]
    [InlineData(EhSite.EHentai, "background:transparent url(https://ehgt.org/t/4f/4319211-0.jpg) 0 0 no-repeat", "https://ehgt.org/t/4f/4319211-0.jpg")]
    [InlineData(EhSite.ExHentai, "background-image: url('//s.exhentai.org/t/cover.webp?a=1&amp;b=2')", "https://s.exhentai.org/t/cover.webp?a=1&b=2")]
    [InlineData(EhSite.ExHentai, "background: url(&quot;/t/cover.webp&quot;) center no-repeat", "https://exhentai.org/t/cover.webp")]
    [InlineData(EhSite.ExHentai, "background-image: url(cover.webp)", "https://exhentai.org/g/4319211/5c2103fb19/cover.webp")]
    public void DetailCoverCanBeAStyledElement(EhSite site, string style, string expected)
    {
        string html = Detail.Replace(
            """<img src="https://ehgt.org/t/4f/4319211-0.jpg" />""",
            $"""<div style="width:250px; height:350px; {style}"></div>""");

        EhGalleryDetails details = EhParser.ParseGalleryDetail(html, site, 4319211, "5c2103fb19");

        Assert.NotEmpty(details.Title);
        Assert.Equal(expected, details.ThumbUrl);
    }

    // 2026-11-13 匿名首页抓取：评分使用 ir 元素的 sprite 位移，首两种为真实片段。
    [Theory]
    [InlineData("background-position:-32px -21px;opacity:1", 2.5)]
    [InlineData("background-position:-48px -1px;opacity:1", 2)]
    [InlineData("background-position:0px -1px", 5)]
    [InlineData("background-position: -16px -21px", 3.5)]
    [InlineData("background-position:-80px -1px", 0)]
    [InlineData("background-position:invalid", 0)]
    public void ListRatingsComeFromTheSiteSprite(string style, double expected)
    {
        string html = ListRow.Replace("<td class=\"gl4c glfav\">", $"<td class=\"gl4c glfav\"><div class=\"ir\" style=\"{style}\"></div>");
        Assert.Equal(expected, Assert.Single(EhParser.ParseGalleryList(html, EhSite.EHentai, 0).Items).Rating);
    }

    private const string ListRow = """
        <table class="itg gld"><tr>
        <td class="gl1c glcat"><div class="glink">Doujinshi</div></td>
        <td class="gl2c">
          <a href="https://e-hentai.org/g/4319211/5c2103fb19/">
            <div class="glthumb"><img src="https://ehgt.org/t/4f/4319211-0.jpg" /></div>
          </a>
          <div class="glink">(C95) [Uguisuya] HH[K2] (Fate/Grand Order) (English)</div>
          <div class="glhide">
            <div><a href="https://e-hentai.org/uploader/dragonslayerGG">dragonslayerGG</a></div>
            <div class="gl2m"><div class="gl3e"><div>15 pages</div><div>2026-11-10 10:04</div></div></div>
          </div>
        </td>
        <td class="gl4c glfav"><div class="i" title="Favorited"></div></td>
        </tr></table>
        """;

    private const string Detail = """
        <html><body>
        <h1 id="gn">(C95) [Uguisuya] HH[K2]</h1><h1 id="gj"></h1>
        <div id="gmid">
          <div id="gd3">
            <div id="gdc"><div class="cs ct2" onclick="document.location='https://e-hentai.org/doujinshi'">Doujinshi</div></div>
            <div id="gdn"><a href="https://e-hentai.org/uploader/dragonslayerGG">dragonslayerGG</a></div>
            <div id="gdd"><table>
              <tr><td class="gdt1">Posted:</td><td class="gdt2">2026-11-10 10:04</td></tr>
              <tr><td class="gdt1">Parent:</td><td class="gdt2">None</td></tr>
              <tr><td class="gdt1">Visible:</td><td class="gdt2">Yes</td></tr>
              <tr><td class="gdt1">Language:</td><td class="gdt2">English &nbsp;<span class="halp">TR</span></td></tr>
              <tr><td class="gdt1">File Size:</td><td class="gdt2">340.0 MiB</td></tr>
              <tr><td class="gdt1">Length:</td><td class="gdt2">15 pages</td></tr>
              <tr><td class="gdt1">Favorited:</td><td class="gdt2" id="favcount">28 times</td></tr>
            </table></div>
            <div id="gdr"><table>
              <tr><td id="grt1">Rating:</td><td id="grt2"><div class="ir"></div></td></tr>
              <tr><td id="rating_label" colspan="3">Average: 4.25</td></tr>
            </table></div>
          </div>
          <div id="gd1"><img src="https://ehgt.org/t/4f/4319211-0.jpg" /></div>
          <div id="gd4"><div id="taglist"><table>
            <tr><td class="tc">language:</td>
              <td>
                <div id="td_language:english" class="gt"><a href="https://e-hentai.org/tag/language:english">english</a></div>
                <div id="td_language:translated" class="gt"><a href="https://e-hentai.org/tag/language:translated">translated</a></div>
              </td></tr>
            <tr><td class="tc">parody:</td>
              <td><div id="td_parody:fate_grand_order" class="gtl"><a href="https://e-hentai.org/tag/parody:fate+grand+order">fate grand order</a></div></td></tr>
          </table></div></div>
        </div></body></html>
        """;

    private const string PageList = """
        <html><body>
        <div class="gtb"><p class="gpc">Showing 1 - 15 of 198 images</p>
          <table class="ptt"><tr>
            <td class="ptdd">&lt;</td>
            <td class="ptds"><a href="https://e-hentai.org/g/4319211/5c2103fb19/">1</a></td>
            <td class="ptds"><a href="https://e-hentai.org/g/4319211/5c2103fb19/?p=1">2</a></td>
            <td class="ptdd">&gt;</td>
          </tr></table>
        </div>
        <div id="gdt" class="gt200">
          <a href="https://e-hentai.org/s/49de22d977/4319211-1"><div title="Page 1: 1.png" style="width:200px;height:282px;background:transparent url(https://h/x.webp) -0px 0 no-repeat"></div></a>
          <a href="https://e-hentai.org/s/218d73748d/4319211-2"><div title="Page 2: 2.png" style="width:200px;height:285px;background:transparent url(https://h/x.webp) -200px 0 no-repeat"></div></a>
        </div>
        </body></html>
        """;

    private const string ImagePage = """
        <html><body>
        <div id="i3"><a href="https://e-hentai.org/s/218d73748d/4319211-2">
          <img id="img" src="https://wavwkib.hath.network/h/abc/keystamp=1;fileindex=2;xres=1280/1.webp"
               style="width:1280px;height:1807px" onerror="this.onerror=null; nl('44239-498420')" /></a></div>
        <div id="i4"><div>1.png :: 1280 x 1807 :: 192.8 KiB</div></div>
        </body></html>
        """;

    [Fact]
    public void GalleryListParsesRow()
    {
        EhGalleryListPage page = EhParser.ParseGalleryList(ListRow, EhSite.EHentai, 0);

        EhGallerySummary item = Assert.Single(page.Items);
        Assert.Equal(4319211, item.Gid);
        Assert.Equal("5c2103fb19", item.Token);
        Assert.Equal("(C95) [Uguisuya] HH[K2] (Fate/Grand Order) (English)", item.Title);
        Assert.Equal("Doujinshi", item.Category);
        Assert.Equal(EhCategory.Doujinshi.ToString(), item.CategoryKey);
        Assert.Equal(15, item.Pages);
        Assert.Equal("2026-11-10 10:04", item.Posted);
        Assert.Equal("dragonslayerGG", item.Uploader);
        Assert.StartsWith("https://ehgt.org/", item.ThumbUrl);
    }

    [Fact]
    public void CompactListTitleExcludesTagChips()
    {
        const string compact = """
            <table class="itg gltc"><tr>
              <td class="gl1c glcat"><div class="cn ct1" onclick="document.location='https://e-hentai.org/misc'">Misc</div></td>
              <td class="gl2c">
                <div class="glthumb" id="it1"><div><img alt="T" title="T" src="https://ehgt.org/w/02/1.webp" /></div>
                  <div><div><div class="cn ct1">Misc</div><div id="postedpop_1">2026-11-10 10:23</div></div>
                  <div><div class="ir"></div><div>91 pages</div></div></div></div>
              </td>
              <td class="gl3c glname" onmouseover="show_image_pane(1)">
                <a href="https://e-hentai.org/g/4319235/fe490a0209/">
                  <div class="glink">Real Title Only</div>
                  <div><div class="gt" title="language:chinese">chinese</div><div class="gt" title="other:ai generated">ai generated</div></div>
                </a>
              </td>
            </tr></table>
            """;

        EhGalleryListPage page = EhParser.ParseGalleryList(compact, EhSite.EHentai, 0);

        EhGallerySummary item = Assert.Single(page.Items);
        Assert.Equal("Real Title Only", item.Title);
        Assert.Equal(91, item.Pages);
        Assert.Equal("Misc", item.Category);
        Assert.StartsWith("https://ehgt.org/", item.ThumbUrl);
    }

    [Fact]
    public void GalleryDetailParsesMetadataAndTags()
    {
        EhGalleryDetails details = EhParser.ParseGalleryDetail(Detail, EhSite.EHentai, 4319211, "5c2103fb19");
        Assert.Equal("https://ehgt.org/t/4f/4319211-0.jpg", details.ThumbUrl);

        Assert.Equal("(C95) [Uguisuya] HH[K2]", details.Title);
        Assert.Equal("Doujinshi", details.Category);
        Assert.Equal(15, details.Pages);
        Assert.Equal(4.25, details.Rating, 3);
        Assert.Equal("340.0 MiB", details.FileSize);
        Assert.Equal("English", details.Language);
        Assert.Equal("Yes", details.Visible);
        Assert.Equal("28 times", details.Favorited);
        Assert.Equal("dragonslayerGG", details.Uploader);

        Assert.Contains(details.Tags, t => t.Namespace == "language" && t.Name == "english");
        Assert.Contains(details.Tags, t => t.Namespace == "parody" && t.SearchTerm == "parody:fate grand order");
    }

    [Fact]
    public void PageListParsesGidFirstViewerUrls()
    {
        bool ok = EhParser.TryParsePageList(PageList, EhSite.EHentai, 0, out EhPageList list, out string? error);

        Assert.True(ok, error);
        Assert.Equal(2, list.Pages.Count);
        Assert.Equal(4319211, list.Pages[0].Gid);
        Assert.Equal(1, list.Pages[0].Page);
        Assert.Equal("49de22d977", list.Pages[0].Hash);
        Assert.Equal(200, list.Pages[0].Width);
        Assert.Equal(282, list.Pages[0].Height);
        Assert.True(list.HasMore);
    }

    [Fact]
    public void PageListWithoutNextLinkStops()
    {
        string html = PageList
            .Replace("<td class=\"ptds\"><a href=\"https://e-hentai.org/g/4319211/5c2103fb19/?p=1\">2</a></td>", string.Empty)
            .Replace("of 198 images", "of 2 images");
        bool ok = EhParser.TryParsePageList(html, EhSite.EHentai, 0, out EhPageList list, out _);

        Assert.True(ok);
        Assert.False(list.HasMore);
    }

    [Fact]
    public void ImagePageParsesSourceAndFullImageKey()
    {
        bool ok = EhParser.TryParseImagePage(ImagePage, out string url, out string? nlKey, out string? error);

        Assert.True(ok, error);
        Assert.StartsWith("https://wavwkib.hath.network/", url);
        Assert.Equal("44239-498420", nlKey);
    }

    [Fact]
    public void ViewerUrlRoundTrips()
    {
        var page = new EhPageRef { Hash = "49de22d977", Gid = 4319211, Page = 3 };

        Assert.Equal("https://e-hentai.org/s/49de22d977/4319211-3", EhUrls.ImagePage(EhSite.EHentai, page));
        Assert.True(EhUrls.TryParseImageHref(EhUrls.ImagePage(EhSite.EHentai, page), out string hash, out long gid, out int number));
        Assert.Equal("49de22d977", hash);
        Assert.Equal(4319211, gid);
        Assert.Equal(3, number);
    }

    [Fact]
    public void ImageLimitsComeFromTheHomePagePair()
    {
        string html = "<html><body><div class='home'><p>Image Limits</p><div>1,234 / 5,000</div></div></body></html>";

        Assert.True(EhParser.TryParseImageLimits(html, out EhImageLimits limits));
        Assert.Equal(1234, limits.Used);
        Assert.Equal(5000, limits.Maximum);
        Assert.False(limits.IsExceeded);
    }

    [Fact]
    public void UnrestrictedImageLimitsComeFromTheSitesOwnWording()
    {
        string html = "<p>Image Limits</p><p>Due to widespread usage of bulk downloaders, high-resolution "
                      + "images can be limited. You are currently using IP-based limits. "
                      + "No restrictions are currently in effect.</p>";

        Assert.True(EhParser.TryParseImageLimits(html, out EhImageLimits limits));
        Assert.True(limits.IsUnrestricted);
        Assert.False(limits.IsExceeded);
    }

    [Fact]
    public void ExceededImageLimitsAreFlagged()
    {
        string html = "<p>Image Limits</p><span>30,123 / 5,000</span><p>Reset</p>";

        Assert.True(EhParser.TryParseImageLimits(html, out EhImageLimits limits));
        Assert.Equal(30123, limits.Used);
        Assert.True(limits.IsExceeded);
    }

    [Fact]
    public void HomePageWithoutImageLimitsDoesNotInventNumbers()
    {
        Assert.False(EhParser.TryParseImageLimits("<p>Image Limits are temporarily unavailable</p>", out _));
        Assert.False(EhParser.TryParseImageLimits("<p>Nothing here</p>", out _));
    }

    [Fact]
    public void SearchUrlUsesExcludedCategoryMask()
    {
        var request = new EhSearchRequest
        {
            Query = "fate grand order",
            CategoryMask = (1 << (int)EhCategory.Doujinshi) | (1 << (int)EhCategory.Manga),
            MinPages = 10,
            MaxPages = 100,
        };

        string url = EhUrls.Search(EhSite.EHentai, request, 2);

        Assert.Contains("f_search=fate%20grand%20order", url);
        Assert.Contains("f_cats=1017", url); // 1023 without doujinshi(2) and manga(4)
        Assert.Contains("f_spf=10", url);
        Assert.Contains("f_spt=100", url);
        Assert.EndsWith("&page=2", url);
    }

    [Fact]
    public void HtmlLiteToleratesMalformedNesting()
    {
        HtmlNode document = HtmlLite.Parse("<div class='a'><p>one<b>two</p></div><span>x &amp; y</span>");

        // Inline markup without whitespace stays fused, exactly like a browser.
        Assert.Equal("onetwo", document.FirstDescendantByClass("a")!.InnerText);
        Assert.Equal("x & y", document.Descendants("span").First().InnerText);
    }
}

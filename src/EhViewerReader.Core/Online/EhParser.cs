using System.Globalization;
using System.Text.RegularExpressions;

namespace EhViewerReader.Core.Online;

/// <summary>
/// HTML → model translation for the pages the client scrapes.
///
/// The live markup is not the classic layout documented by older clients:
/// detail fields live in <c>#gdd</c> label/value rows, tags are a
/// <c>#taglist</c> table whose first cell holds the namespace, preview
/// thumbnails are CSS sprite slices (<c>div[title][style]</c>) rather than
/// <c>img</c> tags, and the full-size image is reached through the
/// <c>nl('keystamp')</c> hook. Every field therefore has a fallback and a
/// missing value never fails a request.
/// </summary>
public static partial class EhParser
{
    [GeneratedRegex(@"(\d+)\s*pages?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PagesRegex();

    [GeneratedRegex(@"(\d{4}-\d{2}-\d{2})\s*(\d{2}:\d{2})?", RegexOptions.CultureInvariant)]
    private static partial Regex PostedRegex();

    [GeneratedRegex(@"(?:Average|Rating):\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RatingRegex();

    [GeneratedRegex(@"[?&]p=(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex PageParamRegex();

    [GeneratedRegex(@"of\s+([0-9,]+)\s+images", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TotalImagesRegex();

    [GeneratedRegex(@"width:\s*(\d+)px;\s*height:\s*(\d+)px", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ThumbBoxRegex();

    [GeneratedRegex(@"nl\('([^']+)'\)", RegexOptions.CultureInvariant)]
    private static partial Regex NlKeyRegex();

    [GeneratedRegex(@"<option[^>]*value\s*=\s*""(?<slot>[0-9]+)""[^>]*selected", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SelectedFavSlotRegex();

    // ------------------------------------------------------------- listings

    public static EhGalleryListPage ParseGalleryList(string html, EhSite site, int pageIndex)
    {
        var page = new EhGalleryListPage { PageIndex = pageIndex };
        HtmlNode document = HtmlLite.Parse(html);

        var seen = new HashSet<long>();
        foreach (HtmlNode anchor in document.Descendants("a"))
        {
            if (!EhUrls.TryParseGalleryHref(anchor.GetAttribute("href"), out long gid, out string token)) continue;
            if (!seen.Add(gid)) continue;

            HtmlNode? container =
                anchor.AncestorOrSelf(n => string.Equals(n.Name, "tr", StringComparison.OrdinalIgnoreCase)) ??
                anchor.AncestorOrSelf(n => n.HasClass("gl1e") || n.HasClass("gl2c") || n.HasClass("gl1c"));

            HtmlNode scope = container ?? anchor.Parent ?? anchor;
            var summary = new EhGallerySummary
            {
                Gid = gid,
                Token = token,
                Title = ExtractTitle(scope, anchor),
                Category = ExtractCategory(scope),
                ThumbUrl = ExtractThumb(scope),
                Pages = ExtractPages(scope),
                Posted = ExtractPosted(scope),
                Uploader = ExtractUploader(scope),
            };

            EhCategory? category = EhCategories.Parse(summary.Category);
            summary.CategoryKey = category?.ToString() ?? string.Empty;
            summary.CategoryColor = EhCategories.GetColor(category ?? EhCategory.Misc);
            page.Items.Add(summary);
        }

        HtmlNode? next = document.FirstDescendantById("dnext");
        string nextHref = next?.GetAttribute("href") ?? string.Empty;
        if (nextHref.Length == 0)
        {
            foreach (HtmlNode anchor in document.Descendants("a"))
            {
                if (!string.Equals(anchor.GetAttribute("rel"), "next", StringComparison.OrdinalIgnoreCase)) continue;
                nextHref = anchor.GetAttribute("href");
                if (nextHref.Length > 0) break;
            }
        }

        // The gallery list pager exposes a "next" link relative to the site
        // root; the front page uses ?page=N (1-based) while search uses the
        // same parameter, so trusting the site's own href is safest.
        page.NextUrl = EhUrls.Absolute(site, nextHref);
        page.HasMore = page.NextUrl.Length > 0;

        if (page.Items.Count == 0)
        {
            page.StatusMessage = DetectMessage(html) ?? "没有结果";
        }

        return page;
    }

    private static string ExtractTitle(HtmlNode scope, HtmlNode anchor)
    {
        HtmlNode? name = scope.FirstDescendantByClass("glname");
        if (name is not null)
        {
            // The compact layout wraps the title AND the tag chips inside one
            // anchor (<div class="glink">title</div><div class="gt">tag</div>…),
            // so the anchor text must never be used as the title.
            HtmlNode? glink = name.FirstDescendantByClass("glink");
            string text = Clean(glink?.InnerText ?? string.Empty);
            if (text.Length > 0) return text;

            HtmlNode? link = name.FirstDescendant("a");
            HtmlNode? titleOnly = link?.FirstDescendantByClass("glink");
            text = Clean(titleOnly?.InnerText ?? link?.GetAttribute("title") ?? string.Empty);
            if (text.Length > 0) return text;
        }

        foreach (HtmlNode node in scope.DescendantsByClass("glink"))
        {
            if (node.AncestorOrSelf(n => n.HasClass("glcat")) is not null) continue;
            HtmlNode? link = node.FirstDescendant("a");
            string text = Clean(link?.InnerText ?? node.InnerText);
            if (text.Length > 0) return text;
        }

        HtmlNode? titleNode = scope.FirstDescendantByClass("gl4e") ?? scope.FirstDescendantByClass("gl3e");
        if (titleNode is not null)
        {
            HtmlNode? link = titleNode.FirstDescendant("a");
            if (link is not null && link.InnerText.Length > 0) return Clean(link.InnerText);
        }

        // Fallback: the longest anchor text in the row (image anchors are empty).
        string best = anchor.GetAttribute("title");
        foreach (HtmlNode candidate in scope.Descendants("a"))
        {
            if (!EhUrls.TryParseGalleryHref(candidate.GetAttribute("href"), out _, out _)) continue;
            string text = Clean(candidate.InnerText);
            if (text.Length > best.Length) best = text;
        }

        return Clean(best);
    }

    private static string ExtractCategory(HtmlNode scope)
    {
        HtmlNode? cell = scope.FirstDescendantByClass("glcat");
        string text = cell?.InnerText ?? string.Empty;
        return Clean(text);
    }

    private static string ExtractThumb(HtmlNode scope)
    {
        HtmlNode? thumbBox = scope.FirstDescendantByClass("glthumb") ??
                             scope.FirstDescendantByClass("gl2e") ??
                             scope.FirstDescendantByClass("gl1e");
        HtmlNode? image = (thumbBox ?? scope).FirstDescendant("img") ?? scope.FirstDescendant("img");
        if (image is null) return string.Empty;

        string src = image.GetAttribute("data-src");
        if (src.Length == 0) src = image.GetAttribute("src");
        return NormalizeUrl(src);
    }

    private static int ExtractPages(HtmlNode scope)
    {
        Match match = PagesRegex().Match(scope.InnerText);
        return match.Success && int.TryParse(match.Groups[1].Value, out int pages) ? pages : 0;
    }

    private static string ExtractPosted(HtmlNode scope)
    {
        Match match = PostedRegex().Match(scope.InnerText);
        if (!match.Success) return string.Empty;
        return match.Groups[2].Success ? $"{match.Groups[1].Value} {match.Groups[2].Value}" : match.Groups[1].Value;
    }

    private static string ExtractUploader(HtmlNode scope)
    {
        foreach (HtmlNode anchor in scope.Descendants("a"))
        {
            if (!anchor.GetAttribute("href").Contains("uploader", StringComparison.OrdinalIgnoreCase)) continue;
            if (anchor.InnerText.Length > 0) return Clean(anchor.InnerText);
        }

        return string.Empty;
    }

    // -------------------------------------------------------------- details

    public static EhGalleryDetails ParseGalleryDetail(string html, EhSite site, long gid, string token)
    {
        HtmlNode document = HtmlLite.Parse(html);
        var details = new EhGalleryDetails
        {
            Gid = gid,
            Token = token,
            Title = Clean(document.FirstDescendantById("gn")?.InnerText ?? string.Empty),
            JapaneseTitle = Clean(document.FirstDescendantById("gj")?.InnerText ?? string.Empty),
        };

        // Category: #gdc holds one div.cs per category link, without a label.
        HtmlNode? gdc = document.FirstDescendantById("gdc");
        if (gdc is not null)
        {
            foreach (HtmlNode cell in gdc.DescendantsByClass("cs"))
            {
                string text = cell.InnerText;
                if (text.Contains(':')) continue;
                if (text.Length == 0) continue;
                details.Category = Clean(text);
                break;
            }
        }

        // Metadata rows: #gdd > table > tr > td.gdt1 (label) / td.gdt2 (value).
        HtmlNode? gdd = document.FirstDescendantById("gdd");
        if (gdd is not null)
        {
            foreach (HtmlNode row in gdd.Descendants("tr"))
            {
                List<HtmlNode> cells = row.Descendants("td").ToList();
                if (cells.Count < 2) continue;
                string label = Clean(cells[0].InnerText).TrimEnd(':');
                string value = Clean(cells[1].InnerText);
                ApplyDetailField(details, label, value);
            }
        }

        if (details.Uploader.Length == 0)
        {
            HtmlNode? gdn = document.FirstDescendantById("gdn");
            HtmlNode? link = gdn?.FirstDescendant("a");
            if (link is not null) details.Uploader = Clean(link.InnerText);
        }

        if (details.Category.Length > 0)
        {
            EhCategory? category = EhCategories.Parse(details.Category);
            details.CategoryKey = category?.ToString() ?? string.Empty;
            details.CategoryColor = EhCategories.GetColor(category ?? EhCategory.Misc);
        }

        HtmlNode? rating = document.FirstDescendantById("rating_label");
        if (rating is not null)
        {
            Match match = RatingRegex().Match(rating.InnerText);
            if (match.Success &&
                double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                details.Rating = value;
            }
        }

        HtmlNode? favoriteCount = document.FirstDescendantById("favcount");
        if (favoriteCount is not null) details.Favorited = Clean(favoriteCount.InnerText);

        HtmlNode? thumb = document.FirstDescendantById("gd1");
        if (thumb is not null)
        {
            HtmlNode? image = thumb.FirstDescendant("img");
            if (image is not null) details.ThumbUrl = NormalizeUrl(image.GetAttribute("src"));
        }

        HtmlNode? taglist = document.FirstDescendantById("taglist");
        if (taglist is not null) ParseTags(taglist, details);

        if (details.Pages == 0)
        {
            Match match = PagesRegex().Match(document.InnerText);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int pages)) details.Pages = pages;
        }

        details.FavoriteSlot = ParseFavoriteSlot(html);
        return details;
    }

    private static void ApplyDetailField(EhGalleryDetails details, string label, string value)
    {
        switch (label.Trim().ToLowerInvariant())
        {
            case "posted":
                details.Posted = value;
                break;
            case "parent":
                details.Parent = value;
                break;
            case "visible":
                details.Visible = value;
                break;
            case "language":
                // "English TR" — the translation marker is a separate span.
                details.Language = value.Replace(" TR", string.Empty, StringComparison.Ordinal).Trim();
                break;
            case "file size":
                details.FileSize = value;
                break;
            case "length":
                Match pages = PagesRegex().Match(value);
                if (pages.Success && int.TryParse(pages.Groups[1].Value, out int count)) details.Pages = count;
                break;
        }
    }

    private static void ParseTags(HtmlNode taglist, EhGalleryDetails details)
    {
        foreach (HtmlNode row in taglist.Descendants("tr"))
        {
            List<HtmlNode> cells = row.Descendants("td").ToList();
            if (cells.Count < 2) continue;

            string space = Clean(cells[0].InnerText).TrimEnd(':');
            if (space.Length == 0) continue;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (HtmlNode group in cells[1].Descendants())
            {
                if (!group.HasClass("gt") && !group.HasClass("gtl")) continue;
                HtmlNode? anchor = group.FirstDescendant("a");
                string name = Clean(anchor?.InnerText ?? group.InnerText);
                if (name.Length == 0 || !seen.Add(name)) continue;

                details.Tags.Add(new EhTag
                {
                    Namespace = space,
                    Name = name,
                    DisplayName = name,
                });
            }
        }
    }

    // ------------------------------------------------------------ page list

    public static bool TryParsePageList(
        string html,
        EhSite site,
        int pageIndex,
        out EhPageList list,
        out string? error)
    {
        error = null;
        list = new EhPageList();
        HtmlNode document = HtmlLite.Parse(html);
        HtmlNode? gdt = document.FirstDescendantById("gdt") ?? document.FirstDescendantByClass("gdt");

        if (gdt is null)
        {
            error = DetectMessage(html) ?? "无法解析页面列表";
            return false;
        }

        foreach (HtmlNode anchor in gdt.Descendants("a"))
        {
            string href = anchor.GetAttribute("href");
            if (!EhUrls.TryParseImageHref(href, out string hash, out long gid, out int pageNumber)) continue;

            // Each preview is a CSS sprite slice: div[title][style] carries the
            // per-page box size, which preserves the aspect ratio.
            HtmlNode? box = anchor.FirstDescendant("div");
            int width = 0;
            int height = 0;
            string style = box?.GetAttribute("style") ?? string.Empty;
            Match match = ThumbBoxRegex().Match(style);
            if (match.Success)
            {
                int.TryParse(match.Groups[1].Value, out width);
                int.TryParse(match.Groups[2].Value, out height);
            }

            list.Pages.Add(new EhPageRef
            {
                Hash = hash,
                Gid = gid,
                Page = pageNumber,
                PageUrl = EhUrls.Absolute(site, href),
                Width = width,
                Height = height,
            });
        }

        list.Pages.Sort((a, b) => a.Page.CompareTo(b.Page));
        list.HasMore = HasNextPreviewPage(document, pageIndex, list.Pages.Count);
        if (list.Pages.Count == 0)
        {
            error = DetectMessage(html) ?? "页面列表为空";
            return false;
        }

        return true;
    }

    private static bool HasNextPreviewPage(HtmlNode document, int pageIndex, int itemsOnPage)
    {
        int maxPage = -1;
        foreach (HtmlNode anchor in document.Descendants("a"))
        {
            Match match = PageParamRegex().Match(anchor.GetAttribute("href"));
            if (!match.Success) continue;
            if (int.TryParse(match.Groups[1].Value, out int page) && page > maxPage) maxPage = page;
        }

        if (maxPage >= 0) return maxPage > pageIndex;

        Match total = TotalImagesRegex().Match(document.InnerText);
        if (total.Success &&
            int.TryParse(total.Groups[1].Value.Replace(",", string.Empty), out int images))
        {
            return (pageIndex + 1) * Math.Max(itemsOnPage, 1) < images;
        }

        return itemsOnPage >= 15;
    }

    // ------------------------------------------------------------ image page

    public static bool TryParseImagePage(string html, out string imageUrl, out string? nlKey, out string? error)
    {
        imageUrl = string.Empty;
        nlKey = null;
        error = null;

        HtmlNode document = HtmlLite.Parse(html);
        HtmlNode? image = document.FirstDescendantById("img");
        if (image is null)
        {
            error = DetectMessage(html) ?? "未找到图片";
            return false;
        }

        imageUrl = NormalizeUrl(image.GetAttribute("src"));

        // Full-size image: the site exposes nl('keystamp') on the error hook.
        Match nl = NlKeyRegex().Match(html);
        if (nl.Success) nlKey = nl.Groups[1].Value;

        if (imageUrl.Length == 0)
        {
            error = "图片地址为空";
            return false;
        }

        return true;
    }

    /// <summary>Reads the currently selected favorite slot from the popup HTML.</summary>
    public static int ParseFavoriteSlot(string html)
    {
        Match match = SelectedFavSlotRegex().Match(html);
        if (match.Success && int.TryParse(match.Groups["slot"].Value, out int slot)) return slot;

        foreach (Match radio in Regex.Matches(html,
                     "<input[^>]*name=\"favcat\"[^>]*value=\"(?<slot>[0-9]+)\"[^>]*checked",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (int.TryParse(radio.Groups["slot"].Value, out slot)) return slot;
        }

        return -1;
    }

    public static string? DetectMessage(string html)
    {
        if (html.Contains("Sad Panda", StringComparison.OrdinalIgnoreCase))
        {
            return "ExHentai 拒绝访问（Sad Panda）：请先登录，并确认账号可访问 ExHentai";
        }

        if (html.Contains("Your IP address has been temporarily banned", StringComparison.OrdinalIgnoreCase))
        {
            return "IP 已被临时封禁，请稍后再试或检查代理";
        }

        if (html.Contains("You have exceeded your image viewing limits", StringComparison.OrdinalIgnoreCase))
        {
            return "图片配额已用尽，请等待或使用账号配额";
        }

        if (html.Contains("This gallery is unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return "画廊不可用（可能已删除或需登录）";
        }

        if (html.Contains("Key missing, or incorrect key provided", StringComparison.OrdinalIgnoreCase))
        {
            return "链接密钥无效";
        }

        if (html.Contains("No hits found", StringComparison.OrdinalIgnoreCase))
        {
            return "没有匹配结果";
        }

        return null;
    }

    private static string NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        string trimmed = url.Trim().Replace("&amp;", "&");
        if (trimmed.StartsWith("//", StringComparison.Ordinal)) trimmed = "https:" + trimmed;
        if (trimmed.StartsWith('/') && !trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            trimmed = EhUrls.EHentaiBase + trimmed;
        }

        return trimmed;
    }

    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}

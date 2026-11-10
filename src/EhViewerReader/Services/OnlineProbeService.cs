using EhViewerReader.Core.Online;
using EhViewerReader.Services;

namespace EhViewerReader.Services;

/// <summary>
/// Unattended network probe used to validate the scrapers and the proxy stack
/// against the live site: front page → gallery detail → page list → image URL.
/// Never downloads image content; only resolves the first image URL's host.
/// </summary>
public static class OnlineProbeService
{
    public static async Task<int> RunAsync(AppServices services, string? outputPath)
    {
        var report = new Dictionary<string, object?>
        {
            ["utc"] = DateTime.UtcNow.ToString("O"),
            ["site"] = services.Online.Site.ToString(),
            ["signedIn"] = services.Online.IsSignedIn,
            ["exAccess"] = services.Settings.Account.ExAccess,
            ["proxyMode"] = services.Settings.Network.ProxyMode.ToString(),
            ["doh"] = services.Settings.Network.DnsOverHttps,
        };

        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        int exitCode = 0;

        try
        {
            EhGalleryListPage front = await services.Online
                .GetFrontPageAsync(0, cancellation.Token)
                .ConfigureAwait(false);

            report["front"] = new
            {
                items = front.Items.Count,
                hasMore = front.HasMore,
                status = front.StatusMessage,
                first = front.Items.Take(5).Select(i => new
                {
                    gid = i.Gid,
                    token = i.Token,
                    title = i.Title,
                    category = i.Category,
                    pages = i.Pages,
                    posted = i.Posted,
                    thumbHost = EhUrls.HostOf(i.ThumbUrl),
                }).ToArray(),
            };

            if (front.Items.Count > 0 && front.Items[0].ThumbUrl.Length > 0)
            {
                string thumbUrl = front.Items[0].ThumbUrl;
                try
                {
                    byte[] bytes = await services.Online
                        .DownloadBytesAsync(thumbUrl, EhUrls.Base(services.Online.Site) + "/", cancellation.Token)
                        .ConfigureAwait(false);
                    report["thumbnail"] = new
                    {
                        host = EhUrls.HostOf(thumbUrl),
                        bytes = bytes.Length,
                        ok = bytes.Length > 512,
                    };
                }
                catch (Exception ex) when (ex is EhException or HttpRequestException or TaskCanceledException)
                {
                    report["thumbnail"] = new { host = EhUrls.HostOf(thumbUrl), error = ex.Message };
                }
            }

            if (front.Items.Count == 0)
            {
                report["error"] = "首页未解析出画廊（可能被拦截或页面结构变化）";
                exitCode = 1;
            }
            else
            {
                EhGallerySummary first = front.Items[0];
                EhGalleryDetails details = await services.Online
                    .GetDetailsAsync(first.Gid, first.Token, cancellation.Token)
                    .ConfigureAwait(false);

                report["details"] = new
                {
                    title = details.Title,
                    japanese = details.JapaneseTitle,
                    category = details.Category,
                    pages = details.Pages,
                    rating = details.Rating,
                    fileSize = details.FileSize,
                    language = details.Language,
                    uploader = details.Uploader,
                    favoriteSlot = details.FavoriteSlot,
                    tags = details.Tags.Count,
                    tagSample = details.Tags.Take(12).Select(t => t.SearchTerm).ToArray(),
                };

                EhPageList pageList = await services.Online
                    .GetPageListAsync(first.Gid, first.Token, 0, cancellation.Token)
                    .ConfigureAwait(false);

                report["pageList"] = new
                {
                    pages = pageList.Pages.Count,
                    hasMore = pageList.HasMore,
                    first = pageList.Pages.FirstOrDefault() is { } p
                        ? new { p.Page, p.Hash, thumbHost = EhUrls.HostOf(p.ThumbUrl) }
                        : null,
                };

                if (pageList.Pages.Count > 0)
                {
                    string imageUrl = await services.Online
                        .GetImageUrlAsync(pageList.Pages[0], original: false, cancellation.Token)
                        .ConfigureAwait(false);
                    report["image"] = new
                    {
                        host = EhUrls.HostOf(imageUrl),
                        resolved = imageUrl.Length > 0,
                    };
                }

                if (details.Pages <= 0 || details.Tags.Count == 0)
                {
                    report["warning"] = "详情字段不完整（页数或标签为空）";
                    exitCode = 1;
                }
            }
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException or TaskCanceledException)
        {
            report["error"] = ex.Message;
            exitCode = 1;
        }

        string? path = outputPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(services.Paths.BaseDirectory, "online-probe.json");
        }

        await DiagnosticReport.WriteAsync(path, report).ConfigureAwait(false);
        Console.WriteLine("Online probe report: " + path);
        return exitCode;
    }
}

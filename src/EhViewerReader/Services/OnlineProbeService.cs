using EhViewerReader.Core.Online;
using EhViewerReader.Services;

namespace EhViewerReader.Services;

/// <summary>
/// Unattended network probe used to validate the scrapers and the proxy stack
/// against the live site: front page → gallery detail → page list → image URL →
/// torrent file → tag translation. Never downloads image content; only the
/// first torrent (a few KB) is fetched to prove the save path.
///
/// Values are plain dictionaries instead of anonymous objects so the report is
/// fully structured on NativeAOT as well, where reflection serialization is
/// unavailable.
/// </summary>
public static class OnlineProbeService
{
    private static Dictionary<string, object?> Obj(params (string Key, object? Value)[] pairs)
    {
        var result = new Dictionary<string, object?>(pairs.Length);
        foreach ((string key, object? value) in pairs) result[key] = value;
        return result;
    }

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

        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        int exitCode = 0;

        try
        {
            EhGalleryListPage front = await services.Online
                .GetFrontPageAsync(0, cancellation.Token)
                .ConfigureAwait(false);

            report["front"] = Obj(
                ("items", front.Items.Count),
                ("hasMore", front.HasMore),
                ("status", front.StatusMessage),
                ("first", front.Items.Take(5).Select(i => Obj(
                    ("gid", i.Gid),
                    ("token", i.Token),
                    ("title", i.Title),
                    ("category", i.Category),
                    ("pages", i.Pages),
                    ("posted", i.Posted),
                    ("tags", i.Tags.Count),
                    ("tagSample", i.Tags.Take(4).ToArray()),
                    ("thumbHost", EhUrls.HostOf(i.ThumbUrl))
                )).ToArray()));

            if (front.Items.Count > 0 && front.Items[0].ThumbUrl.Length > 0)
            {
                string thumbUrl = front.Items[0].ThumbUrl;
                try
                {
                    byte[] bytes = await services.Online
                        .DownloadBytesAsync(thumbUrl, EhUrls.Base(services.Online.Site) + "/", cancellation.Token)
                        .ConfigureAwait(false);
                    report["thumbnail"] = Obj(
                        ("host", EhUrls.HostOf(thumbUrl)),
                        ("bytes", bytes.Length),
                        ("ok", bytes.Length > 512));
                }
                catch (Exception ex) when (ex is EhException or HttpRequestException or TaskCanceledException)
                {
                    report["thumbnail"] = Obj(("host", EhUrls.HostOf(thumbUrl)), ("error", ex.Message));
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

                report["details"] = Obj(
                    ("title", details.Title),
                    ("japanese", details.JapaneseTitle),
                    ("category", details.Category),
                    ("pages", details.Pages),
                    ("rating", details.Rating),
                    ("ratingCount", details.RatingCount),
                    ("fileSize", details.FileSize),
                    ("language", details.Language),
                    ("uploader", details.Uploader),
                    ("favoriteSlot", details.FavoriteSlot),
                    ("torrents", details.TorrentCount),
                    ("torrentUrl", details.TorrentUrl.Length > 0),
                    ("archiveUrl", details.ArchiveUrl.Length > 0),
                    ("apiUid", details.ApiUid),
                    ("apiKey", details.ApiKey.Length > 0),
                    ("comments", details.Comments.Count),
                    ("tags", details.Tags.Count),
                    ("tagSample", details.Tags.Take(12).Select(t => t.SearchTerm).ToArray()));

                if (details.Comments.Count > 0)
                {
                    EhComment comment = details.Comments[0];
                    report["commentSample"] = Obj(
                        ("user", comment.User),
                        ("posted", comment.Posted),
                        ("score", comment.Score),
                        ("length", comment.Text.Length),
                        ("hasVoteDetail", comment.VoteDetail.Length > 0));
                }

                if (details.TorrentUrl.Length > 0)
                {
                    try
                    {
                        EhTorrentList torrents = await services.Online
                            .GetTorrentsAsync(details, cancellation.Token)
                            .ConfigureAwait(false);

                        EhTorrent? torrent = torrents.Items.FirstOrDefault();
                        report["torrents"] = Obj(
                            ("count", torrents.Items.Count),
                            ("message", torrents.Message),
                            ("first", torrent is null
                                ? null
                                : Obj(
                                    ("name", torrent.Name),
                                    ("size", torrent.Size),
                                    ("seeds", torrent.Seeds),
                                    ("peers", torrent.Peers),
                                    ("host", EhUrls.HostOf(torrent.Url)))));

                        if (torrent is not null)
                        {
                            try
                            {
                                byte[] file = await services.Online
                                    .DownloadTorrentAsync(torrent.Url, cancellation.Token)
                                    .ConfigureAwait(false);
                                report["torrentFile"] = Obj(
                                    ("bytes", file.Length),
                                    ("bencoded", file.Length > 0 && file[0] == (byte)'d'));
                            }
                            catch (Exception ex) when (ex is EhException or HttpRequestException or TaskCanceledException)
                            {
                                report["torrentFile"] = Obj(("error", ex.Message));
                            }
                        }
                    }
                    catch (Exception ex) when (ex is EhException or HttpRequestException or TaskCanceledException)
                    {
                        report["torrents"] = Obj(("error", ex.Message));
                    }
                }

                if (details.ArchiveUrl.Length > 0)
                {
                    try
                    {
                        EhArchivePage archive = await services.Online
                            .GetArchivePageAsync(details, cancellation.Token)
                            .ConfigureAwait(false);
                        report["archive"] = Obj(
                            ("options", archive.Options.Count),
                            ("or", archive.Or),
                            ("requiresSignIn", archive.RequiresSignIn),
                            ("message", archive.Message));
                    }
                    catch (Exception ex) when (ex is EhException or HttpRequestException or TaskCanceledException)
                    {
                        report["archive"] = Obj(("error", ex.Message));
                    }
                }

                EhPageList pageList = await services.Online
                    .GetPageListAsync(first.Gid, first.Token, 0, cancellation.Token)
                    .ConfigureAwait(false);

                EhPageRef? pageRef = pageList.Pages.FirstOrDefault();
                report["pageList"] = Obj(
                    ("pages", pageList.Pages.Count),
                    ("hasMore", pageList.HasMore),
                    ("first", pageRef is null
                        ? null
                        : Obj(
                            ("page", pageRef.Page),
                            ("hash", pageRef.Hash),
                            ("thumbHost", EhUrls.HostOf(pageRef.ThumbUrl)))));

                if (pageRef is not null)
                {
                    string imageUrl = await services.Online
                        .GetImageUrlAsync(pageRef, original: false, cancellation.Token)
                        .ConfigureAwait(false);
                    report["image"] = Obj(
                        ("host", EhUrls.HostOf(imageUrl)),
                        ("resolved", imageUrl.Length > 0));
                }

                if (details.Pages <= 0 || details.Tags.Count == 0)
                {
                    report["warning"] = "详情字段不完整（页数或标签为空）";
                    exitCode = 1;
                }
            }

            // Tag translation: install once so the probe proves download, gzip
            // decode and parser against the real community release.
            if (services.TagTranslation.Count == 0)
            {
                bool updated = await services.TagTranslation
                    .UpdateAsync(null, cancellation.Token)
                    .ConfigureAwait(false);
                report["tagTranslation"] = Obj(
                    ("downloaded", updated),
                    ("count", services.TagTranslation.Count),
                    ("status", services.TagTranslation.StatusText),
                    ("sample", services.Settings.Online.ShowTagTranslations
                        ? services.TagTranslation.Translate("female", "sole female")
                        : null));
            }
            else
            {
                report["tagTranslation"] = Obj(
                    ("downloaded", false),
                    ("count", services.TagTranslation.Count),
                    ("status", services.TagTranslation.StatusText));
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

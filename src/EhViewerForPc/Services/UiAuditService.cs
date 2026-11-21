using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using EhViewerForPc.Core.Download;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Controls;
using EhViewerForPc.Core.Online;
using EhViewerForPc.ViewModels;
using EhViewerForPc.Views;

namespace EhViewerForPc.Services;

public sealed class UiAuditReport
{
    public string? Error { get; set; }
    public string SyntheticLibrary { get; set; } = string.Empty;
    public List<string> Screenshots { get; set; } = [];
    public List<string> Uncovered { get; set; } = [];
    public Dictionary<string, string> Measurements { get; set; } = [];
    public List<string> HorizontalOverflows { get; set; } = [];
}

/// <summary>Captures isolated synthetic content in the actual application shell.</summary>
public static class UiAuditService
{
    public static async Task<int> RunAsync(MainWindow window, ShellViewModel shell, string? outputPath)
    {
        string output = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(Path.GetTempPath(), "EhReaderAudit-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"))
            : Path.GetFullPath(outputPath);
        Directory.CreateDirectory(output);
        var shots = new List<string>();
        var missing = new List<string>();
        var report = new UiAuditReport { Screenshots = shots, Uncovered = missing };
        string root = Path.Combine(Path.GetTempPath(), "EhReaderAuditLib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        UiSmokeService.CreateSyntheticLibrary(root);
        string? error = await shell.Library.SetRootAsync(root);
        report.SyntheticLibrary = root;
        if (error is not null) { report.Error = error; await File.WriteAllTextAsync(Path.Combine(output, "audit.json"), JsonSerializer.Serialize(report, ReaderJsonContext.Default.UiAuditReport)); return 1; }
        for (int i = 0; i < 450 && (shell.Library.IsScanning || shell.Library.Galleries.Count == 0); i++) await Task.Delay(200);
        string backup = Path.Combine(Path.GetTempPath(), "EhReaderAuditBackup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        UiSmokeService.CreateBackupLibrary(backup);
        await shell.Library.AddSourceAsync(backup);
        for (int i = 0; i < 450 && shell.Library.IsScanning; i++) await Task.Delay(200);

        var summary = new EhGallerySummary { Gid = 912345, Token = "audit", Title = "[中文] 合成在线画廊：用于检查长标题和布局的示例", Category = "漫画", Pages = 128, Rating = 4.25, Uploader = "audit-user", Posted = "2026-01-01" };
        summary.Tags.Add("language:chinese");
        var details = new EhGalleryDetails { Gid = summary.Gid, Token = summary.Token, Title = summary.Title, JapaneseTitle = "合成サンプル", Category = "漫画", Pages = 128, Rating = 4.25, RatingCount = 42, Uploader = "audit-user", Posted = "2026-01-01", Language = "中文", FileSize = "256 MB", TorrentCount = 1, ArchiveUrl = "http://127.0.0.1/archive" };
        details.Tags.Add(new EhTag { Namespace = "language", Name = "chinese", DisplayName = "中文" });
        details.Tags.Add(new EhTag { Namespace = "artist", Name = "synthetic artist", DisplayName = "合成作者" });
        details.Tags.Add(new EhTag { Namespace = "group", Name = "a very long synthetic tag used to verify that a single tag wraps inside the minimum width window" });
        details.Comments.Add(new EhComment { Id = 1, User = "audit-user", Posted = "2026-01-01", Score = 5, Text = "这是用于检查评论区域排版的合成内容。" });

        async Task Capture(string scenario, string variant)
        {
            double? Bottom(Control? control)
            {
                if (control is null || !control.IsEffectivelyVisible || control.Bounds.Height <= 0) return null;
                return control.TranslatePoint(new Point(0, control.Bounds.Height), window)?.Y;
            }
            await Task.Delay(220);
            string name = scenario + "-" + variant + ".png";
            UiSmokeService.SaveScreenshot(window, Path.Combine(output, name));
            shots.Add(name);
            var title = window.FindControl<TextBlock>("DocumentTitle");
            var titlePoint = title?.TranslatePoint(default, window);
            var framePoint = window.NavigationFrame.TranslatePoint(default, window);
            report.Measurements[name] = $"titleY={titlePoint?.Y:F1}; frameY={framePoint?.Y:F1}; viewport={window.ClientSize}";
            if (scenario is "settings-reader" or "settings-downloads")
            {
                var rows = window.GetVisualDescendants().OfType<SettingsRow>()
                    .Where(r => r.IsEffectivelyVisible && r.Bounds.Height > 0).ToArray();
                var row = rows.FirstOrDefault();
                if (row is not null)
                    report.Measurements[name + ":row"] =
                        $"count={rows.Length}; header='{row.Header}'; height={row.Bounds.Height:F0}; hasDescription={row.HasDescription}";
            }
            if (scenario == "library")
            {
                double? accountBottom = Bottom(window.AccountLineControl);
                double? statusBottom = Bottom(window.LibraryStatusBar);
                double delta = accountBottom is { } a && statusBottom is { } b ? Math.Abs(a - b) : double.NaN;
                report.Measurements[name + ":bottoms"] =
                    $"accountBottom={accountBottom:F1}; statusBottom={statusBottom:F1}; delta={delta:F1}; paneOpen={window.IsPaneOpenForAudit}";
                // Window decoration evidence: the client area is smaller than the
                // frame, which proves the OS (not the app) draws the title bar.
                report.Measurements[name + ":window"] =
                    $"decor={window.WindowDecorations}; extend={window.ExtendClientAreaToDecorationsHint}; client={window.ClientSize}; frame={window.FrameSize}";
            }
            foreach (var control in window.GetVisualDescendants().Where(v => v is Button or ComboBox or TextBox or Slider or CheckBox).OfType<Control>())
            {
                if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0) continue;
                var point = control.TranslatePoint(default, window);
                if (point is not { } p || p.Y >= window.ClientSize.Height || p.Y + control.Bounds.Height <= 0) continue;
                if (p.X < -1 || p.X + control.Bounds.Width > window.ClientSize.Width + 1)
                    report.HorizontalOverflows.Add($"{name}: {control.GetType().Name} {control.Name} {p.X:F0}+{control.Bounds.Width:F0} > {window.ClientSize.Width:F0}");
            }
        }
        void SeedFeed(OnlineFeedViewModel feed)
        {
            var page = new EhGalleryListPage();
            for (int i = 0; i < 8; i++)
            {
                var item = new EhGallerySummary { Gid = summary.Gid + i, Token = "audit", Title = summary.Title + " " + (i + 1), Category = summary.Category, Pages = summary.Pages, Rating = summary.Rating, Uploader = summary.Uploader, Posted = summary.Posted };
                item.Tags.Add("language:chinese");
                page.Items.Add(item);
            }
            feed.ApplyPage(page, "http://127.0.0.1/fixture", true);
            feed.Items.Clear();
            foreach (var item in page.Items)
                feed.Items.Add(new OnlineGalleryItemViewModel(item, shell.Library.SnapshotAllWorks().First(g => g.CanRead).CoverRequest, shell));
        }
        void SeedDownloads()
        {
            var manager = AppServices.Current.Downloads;
            if (manager.FindTask(912345) is null)
            {
                var failed = manager.Enqueue(details, startImmediately: false);
                failed.State = DownloadState.Failed;
                failed.DownloadedPages = 25;
                failed.Message = "下载失败：连接超时，请检查网络设置并重试。此文本用于检查窄窗口中的错误信息是否完整可见。";
                var paused = manager.Enqueue(new EhGalleryDetails { Gid = 912346, Token = "audit", Title = "合成暂停任务", Pages = 100 }, startImmediately: false);
                paused.State = DownloadState.Paused;
                paused.DownloadedPages = 32;
                var completed = manager.Enqueue(new EhGalleryDetails { Gid = 912347, Token = "audit", Title = "合成已完成任务", Pages = 10 }, startImmediately: false);
                completed.State = DownloadState.Completed;
                completed.DownloadedPages = 10;
                var unavailable = manager.Enqueue(new EhGalleryDetails { Gid = 912348, Token = "audit", Title = "合成画廊不可用任务", Pages = 64 }, startImmediately: false);
                unavailable.State = DownloadState.Failed;
                unavailable.DownloadedPages = 12;
                unavailable.Message = EhParser.GalleryUnavailableMessage;
                manager.MarkGalleryUnavailable(unavailable);
                manager.ClearFinished();
            }
            shell.Downloads.SyncAll();
            shell.Downloads.ApplyArchive([new DownloadArchiveEntry { Gid = 912347, Title = "合成已完成任务", PageCount = 128, TotalBytes = 268435456, FinishedUtc = "2026-11-12T00:00:00Z", FolderPath = root }]);
        }
        async Task Document(DocumentPage page, string name, string variant)
        {
            if (page.Title.Length == 0) page.Title = name switch { "archive" => "归档下载", "torrent" => "种子", "web-login" => "登录账号", _ => "详情" };
            window.ShowDiagnosticPage(page);
            await Capture(name, variant);
            page.Close();
            await Task.Delay(200);
        }

        foreach (var (theme, themeName) in new[] { (AppTheme.Dark, "dark"), (AppTheme.Light, "light") })
        foreach (var (width, sizeName) in new[] { (1240d, "wide"), (540d, "narrow") })
        {
            App.ApplyTheme(theme);
            window.Width = width;
            window.Height = 760;
            window.SetPaneOpenForAudit(width > 920);
            await Task.Delay(200);
            string variant = themeName + "-" + sizeName;
            SeedFeed(shell.Online); shell.Navigate("online"); await Task.Delay(250);
            await Capture("online", variant);
            shell.Online.ShowAdvanced = true; await Capture("online-advanced", variant); shell.Online.ShowAdvanced = false;
            SeedFeed(shell.Favorites); shell.Navigate("favorites"); await Task.Delay(250); await Capture("favorites", variant);
            shell.Navigate("library"); await Capture("library", variant);
            shell.Library.SearchText = "不存在的合成搜索结果"; await Task.Delay(500); await Capture("library-no-results", variant); shell.Library.SearchText = string.Empty;
            foreach (var work in shell.Library.SnapshotAllWorks()) shell.Library.Services.Progress.RecordRead(work.WorkId, 1, 0, 0, work.PageCount, false);
            shell.History.Reload(); shell.Navigate("history"); await Capture("history", variant);
            shell.Navigate("downloads"); await Task.Delay(250); SeedDownloads();
            var transfer = shell.Downloads.GetArchiveSession(details, shell);
            transfer.ApplyPage(new EhArchivePage { Message = "可选择归档格式" });
            var downloadTabs = window.GetVisualDescendants().OfType<DownloadsPage>().FirstOrDefault()?.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
            string[] downloadTabNames = ["downloads-tasks", "downloads-unavailable", "downloads-completed", "downloads-transfers"];
            for (int i = 0; i < downloadTabNames.Length; i++) { if (downloadTabs is not null) downloadTabs.SelectedIndex = i; await Capture(downloadTabNames[i], variant); }
            shell.Navigate("settings"); await Task.Delay(250);
            var settingsPage = window.GetVisualDescendants().OfType<SettingsPage>().FirstOrDefault();
            var categories = settingsPage?.FindControl<ListBox>("SettingsCategories");
            string[] names = ["account", "network", "browse", "downloads", "reader", "library", "maintenance"];
            for (int i = 0; i < names.Length; i++)
            {
                if (categories is not null) categories.SelectedIndex = i;
                else report.Uncovered.Add("settings-" + names[i] + ": SettingsCategories not found");
                await Task.Delay(120);
                if (window.Shell?.Settings.SelectedCategoryIndex != i)
                    report.Uncovered.Add("settings-" + names[i] + ": category " + i + " was not selected");
                if (names[i] == "network")
                {
                    int savedMode = shell.Settings.ProxyModeIndex;
                    shell.Settings.ProxyModeIndex = (int)ProxyMode.Auto;
                    await Capture("settings-network", variant);
                    shell.Settings.ProxyModeIndex = (int)ProxyMode.EhDirect;
                    await Capture("settings-network-direct", variant);
                    shell.Settings.ProxyModeIndex = savedMode;
                }
                else
                {
                    await Capture("settings-" + names[i], variant);
                }
            }

            shell.Navigate("library"); await Task.Delay(250);
            var item = shell.Library.Galleries.FirstOrDefault(g => g.PageCount > 3);
            var merged = shell.Library.Galleries.FirstOrDefault(g => g.HasMultipleVersions);
            if (merged is not null) await Document(new VersionsPage(new VersionsViewModel(merged, shell.Library.Services)), "versions", variant);
            if (item is not null)
            {
                var cached = WorkRecord.FromDetails(new EhGalleryDetails { Gid = item.Gid!.Value, Token = item.Info.Token, Title = item.Title, Pages = item.PageCount });
                cached.Metadata!.Tags = details.Tags.Select(t => t.SearchTerm).ToArray();
                AppServices.Current.Works.Upsert(cached);
                var local = new GalleryDetailsPage(item, shell.Library.Services);
                window.ShowDiagnosticPage(local); await Capture("local-detail", variant);
                var expander = local.GetVisualDescendants().OfType<Expander>().FirstOrDefault();
                if (expander is not null) expander.IsExpanded = true;
                await Capture("local-detail-diagnostics", variant); local.Close(); await Task.Delay(200);
                {
                    var readerVm = new ReaderViewModel(shell.Library.Services, item.Info);
                    await readerVm.SetModeAsync(ReadingMode.Single);
                    var reader = new ReaderPage(readerVm) { Title = item.Title };
                    window.ShowDiagnosticPage(reader); await Capture("reader-single", variant);
                    await readerVm.SetModeAsync(ReadingMode.Continuous); await Task.Delay(500); await Capture("reader-continuous", variant);
                    await readerVm.SetModeAsync(ReadingMode.Double); await Task.Delay(500); await Capture("reader-horizontal", variant);
                    reader.ToggleImmersive();
                    await Capture("reader-immersive", variant);
                    reader.ToggleImmersive(); await Task.Delay(150);
                    reader.Close(); await Task.Delay(200);
                }
            }
            var onlineVm = new OnlineGalleryViewModel(AppServices.Current, shell, summary);
            onlineVm.ApplyDetails(details);
            onlineVm.Presentation.CoverRequest = shell.Library.SnapshotAllWorks().First(g => g.CanRead).CoverRequest;
            await Document(new OnlineGalleryPage(onlineVm, false), "online-detail", variant);
            var archiveVm = new ArchiveViewModel(AppServices.Current, shell, details);
            var archiveFixture = new EhArchivePage { Message = "选择归档格式" };
            archiveFixture.Options.Add(new EhArchiveOption { Key = "original", Label = "原图 · 256 MB" });
            archiveFixture.Options.Add(new EhArchiveOption { Key = "resampled", Label = "压缩图 · 120 MB" });
            archiveVm.ApplyPage(archiveFixture);
            await Document(new ArchivePage(archiveVm, false), "archive", variant);
            var torrentVm = new TorrentViewModel(AppServices.Current, details);
            torrentVm.Items.Add(new TorrentItemViewModel(new EhTorrent { Name = "synthetic-gallery.torrent", Size = "256 MB", Seeds = 12, Peers = 3, Uploader = "audit-user", Posted = "2026-01-01" }));
            torrentVm.SetStatus("1 个种子");
            await Document(new TorrentPage(torrentVm, false), "torrent", variant);
            var loginVm = new WebLoginViewModel(AppServices.Current) { ShowManual = true };
            loginVm.SetStatus("离线检查：可手动粘贴 Cookie");
            await Document(new WebLoginPage(loginVm, false), "web-login", variant);
        }
        missing.Add("欢迎和扫描中状态未模拟；实际网络提交和键盘辅助技术需另行验证。");

        // Extra boundary pass: compact rail (640-920) and the minimum height.
        foreach (var (extraWidth, extraHeight, extraName) in new[] { (800d, 760d, "compact"), (540d, 420d, "short") })
        {
            App.ApplyTheme(AppTheme.Dark);
            window.Width = extraWidth;
            window.Height = extraHeight;
            window.SetPaneOpenForAudit(extraWidth > 920);
            await Task.Delay(300);
            shell.Navigate("online"); await Task.Delay(200); await Capture("online", extraName);
            shell.Navigate("library"); await Task.Delay(250); await Capture("library", extraName);
            shell.Navigate("settings"); await Task.Delay(200);
            shell.Settings.SelectedCategoryIndex = 1;
            int savedMode = shell.Settings.ProxyModeIndex;
            shell.Settings.ProxyModeIndex = (int)ProxyMode.Auto;
            await Capture("settings-network", extraName);
            shell.Settings.ProxyModeIndex = savedMode;
            shell.Settings.SelectedCategoryIndex = 4; await Task.Delay(150);
            await Capture("settings-reader", extraName);
            shell.Settings.SelectedCategoryIndex = 5; await Task.Delay(150);
            await Capture("settings-library", extraName);
            shell.Navigate("library"); await Task.Delay(200);
            var extraItem = shell.Library.Galleries.FirstOrDefault(g => g.PageCount > 3);
            if (extraItem is not null)
            {
                var extraVm = new ReaderViewModel(shell.Library.Services, extraItem.Info);
                await extraVm.SetModeAsync(ReadingMode.Single);
                var extraReader = new ReaderPage(extraVm) { Title = extraItem.Title };
                window.ShowDiagnosticPage(extraReader);
                await Capture("reader-single", extraName);
                extraReader.Close();
                await Task.Delay(200);
            }
        }

        await File.WriteAllTextAsync(Path.Combine(output, "audit.json"), JsonSerializer.Serialize(report, ReaderJsonContext.Default.UiAuditReport));
        return 0;
    }
}

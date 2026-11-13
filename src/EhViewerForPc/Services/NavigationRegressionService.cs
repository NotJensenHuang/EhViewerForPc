using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Core.Scanning;
using EhViewerForPc.ViewModels;
using EhViewerForPc.Views;
using EhViewerForPc.Controls;
using SkiaSharp;
using Avalonia;
using Avalonia.LogicalTree;

namespace EhViewerForPc.Services;

/// <summary>Exercises shell navigation and live library updates with isolated, offline fixtures.</summary>
internal static class NavigationRegressionService
{
    public static async Task<int> RunAsync(MainWindow window, ShellViewModel shell, string? outputPath)
    {
        string output = Path.GetFullPath(outputPath ?? Path.Combine(Path.GetTempPath(), "EhNavigationRegression"));
        Directory.CreateDirectory(output);
        var results = new List<string>();
        bool passed = true;
        void Check(string name, bool success)
        {
            results.Add($"{(success ? "PASS" : "FAIL")} {name}");
            passed &= success;
        }

        shell.Navigate("online");
        await Task.Delay(100);
        var title = window.FindControl<TextBlock>("DocumentTitle")!;
        var titlePosition = title.TranslatePoint(default, window);
        Check("wide navigation header has no reserved blank titlebar row", titlePosition is { Y: >= 0 and <= 24 });
        var online = new OnlineGalleryPage(new OnlineGalleryViewModel(AppServices.Current, shell,
            new EhGallerySummary { Gid = 42, Token = "fixture", Title = "Navigation fixture" }));
        window.ShowDiagnosticPage(online);
        shell.SearchTag("artist:fixture");
        await Task.Delay(250);
        Check("tag search leaves online details even when online is already selected",
            window.FindControl<ContentControl>("ContentHost")?.Content is OnlineFeedPage && shell.Online.Query == "artist:fixture");
        var frame = window.NavigationFrame;
        frame.GoBack();
        Check("back restores the same online detail instance", ReferenceEquals(frame.Content, online));
        frame.GoForward();
        Check("forward restores search and sidebar selection", frame.Content is OnlineFeedPage && shell.SelectedNav?.Key == "online");

        string root = Path.Combine(AppServices.Current.Paths.BaseDirectory, "fixture-library");
        string folder = Path.Combine(root, "42-Fixture [Chinese]");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, ".ehviewer"), "VERSION2\n0\n42\nfixture\n0\n0\n0\n3\n");
        // Synthetic geometric cover; only one of three pages has arrived.
        using (var bitmap = new SKBitmap(320, 420))
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColor.Parse("#465A70"), IsAntialias = true })
        {
            canvas.Clear(SKColor.Parse("#283545"));
            canvas.DrawRect(new SKRect(40, 40, 280, 380), paint);
            paint.Color = SKColor.Parse("#FF8A9B");
            canvas.DrawRect(new SKRect(64, 70, 256, 82), paint);
            paint.Color = SKColor.Parse("#CBD6E2");
            for (int line = 0; line < 6; line++) canvas.DrawRect(new SKRect(64, 120 + line * 30, 210, 126 + line * 30), paint);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(Path.Combine(folder, "00000001.png"), data.ToArray());
        }
        var fixture = new EhGalleryDetails { Gid = 42, Token = "fixture", Title = "详情回归样本", Pages = 3, Category = "漫画", Language = "中文", Uploader = "Fixture", Posted = "2026-11-12" };
        fixture.Tags.Add(new EhTag { Namespace = "language", Name = "chinese" });
        fixture.Tags.Add(new EhTag { Namespace = "artist", Name = "fixture" });
        AppServices.Current.Works.Upsert(WorkRecord.FromDetails(fixture));
        var info = AppServices.Current.Scanner.Scan(root, null).Galleries.Single();
        info.Tags = ["artist:fixture"];
        var item = new GalleryItemViewModel(LibraryMerger.Group([info]).Single(),
            g => new CoverImageRequest(g, AppServices.Current.Covers), null);
        shell.Navigate("library");
        var local = new GalleryDetailsPage(item, AppServices.Current);
        local.TagSearchRequested += (_, term) => shell.SearchTag(term);
        window.ShowDiagnosticPage(local);
        await Task.Delay(250);
        Check("local details render clickable tags", local.GetVisualDescendants().OfType<Button>().Any(b => b.Classes.Contains("chip")));
        Check("local and online details share the same control", local.GetVisualDescendants().OfType<GalleryDetailsView>().Any() && online.GetLogicalDescendants().OfType<GalleryDetailsView>().Any());
        Check("shared action slots retain the local page binding", local.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "开始阅读") && b.IsEnabled));
        UiSmokeService.SaveScreenshot(window, Path.Combine(output, "local-details.png"));
        var tag = local.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is TagChip { Term: "artist:fixture" });
        tag.Command!.Execute(null);
        await Task.Delay(100);
        Check("local tag command opens online search", frame.Content is OnlineFeedPage && shell.Online.Query == "artist:fixture");
        frame.GoBack();
        Check("back from tag search restores local details and section", ReferenceEquals(frame.Content, local) && shell.SelectedNav?.Key == "library");
        window.Width = 620;
        await Task.Delay(200);
        tag = local.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is TagChip { Term: "artist:fixture" });
        var tagPosition = tag.TranslatePoint(default, window);
        Check("narrow details keep tags inside the first viewport", tag.IsEffectivelyVisible && tag.Bounds.Width > 0 &&
            tagPosition is { Y: >= 0 } point && point.Y + tag.Bounds.Height <= window.ClientSize.Height);
        UiSmokeService.SaveScreenshot(window, Path.Combine(output, "local-details-narrow.png"));
        window.Width = 1240;
        App.ApplyTheme(AppTheme.Light);
        await Task.Delay(200);
        UiSmokeService.SaveScreenshot(window, Path.Combine(output, "local-details-light.png"));
        App.ApplyTheme(AppTheme.Dark);
        local.Close();
        Check("close releases document from forward history", !frame.ForwardStack.Any(e => e.Context is NavigationTarget t && ReferenceEquals(t.View, local)));

        var reader = new ReaderPage(new ReaderViewModel(AppServices.Current, item.Info)) { Title = item.Title };
        window.ShowDiagnosticPage(reader);
        window.Width = 540;
        await Task.Delay(250);
        reader.ToggleImmersive();
        await Task.Delay(150);
        Check("minimal immersive reader removes the navigation header", frame.TranslatePoint(default, window) is { Y: <= 2 });
        UiSmokeService.SaveScreenshot(window, Path.Combine(output, "reader-immersive-narrow.png"));
        reader.Close();
        window.Width = 1240;

        var services = AppServices.Current;
        services.Settings.Download.AddAsSource = true;
        var task = services.Downloads.Enqueue(new EhGalleryDetails { Gid = 42, Token = "fixture", Title = "Partial fixture", Pages = 3 }, startImmediately: false);
        task.FolderPath = folder;
        task.DestinationRoot = root;
        task.DownloadedPages = 1;
        services.Downloads.Pause(task);
        for (int i = 0; i < 30 && !shell.Library.TryGetAnyLocalCopy(42, out _); i++) await Task.Delay(100);
        Check("unfinished download appears automatically after its first saved page", shell.Library.TryGetAnyLocalCopy(42, out var partial) && partial is { IsIncomplete: true, PageCount: 1 });
        if (partial is not null)
        {
            var sameItem = partial;
            for (int page = 2; page <= 3; page++) File.Copy(Path.Combine(folder, "00000001.png"), Path.Combine(folder, $"{page:D8}.png"), true);
            task.DownloadedPages = 3;
            services.Downloads.Pause(task);
            for (int i = 0; i < 30 && !shell.Library.HasLocalCopy(42); i++) await Task.Delay(100);
            Check("incremental refresh completes the same library card", shell.Library.TryGetLocalCopy(42, out var complete) && complete!.PageCount == 3 && ReferenceEquals(sameItem, complete));
        }
        // Capture theme resolution too, so a missing resource cannot masquerade as a usable theme.
        foreach (string key in new[] { "AppBgBrush", "SolidBackgroundFillColorBase", "TextFillColorPrimary", "CardBackgroundFillColorDefault" })
        {
            Check("theme resource resolves: " + key, window.TryFindResource(key, window.ActualThemeVariant, out var value) && value is not null);
            results.Add($"RESOURCE {key} = {value}");
        }
        await File.WriteAllLinesAsync(Path.Combine(output, "results.txt"), results);
        return passed ? 0 : 1;
    }
}

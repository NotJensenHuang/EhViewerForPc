using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EhViewerReader.Controls;
using EhViewerReader.Core.Models;
using EhViewerReader.ViewModels;
using EhViewerReader.Views;
using SkiaSharp;

namespace EhViewerReader.Services;

/// <summary>
/// Generates a synthetic library (geometric images only) and captures the real
/// visual tree to PNG so layout/contrast can be reviewed without exposing any
/// content from the user's library.
/// </summary>
public static class UiSmokeService
{
    public static async Task RunAsync(MainWindow window, MainViewModel viewModel, string? outputPath)
    {
        string output = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(Path.GetTempPath(), "EhReaderSmoke-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"))
            : Path.GetFullPath(outputPath);
        Directory.CreateDirectory(output);

        var report = new Dictionary<string, object?>();
        var stopwatch = Stopwatch.StartNew();

        string library = Path.Combine(Path.GetTempPath(), "EhReaderSmokeLib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        CreateSyntheticLibrary(library);
        report["syntheticLibrary"] = library;

        string? error = await viewModel.SetRootAsync(library);
        if (error is not null)
        {
            report["error"] = error;
            await WriteReport(output, report);
            return;
        }

        await WaitUntilAsync(() => !viewModel.IsScanning && viewModel.Galleries.Count > 0, TimeSpan.FromSeconds(90));
        await Task.Delay(1600);

        report["galleries"] = viewModel.TotalGalleryCount;
        report["filtered"] = viewModel.FilteredCount;
        report["status"] = viewModel.StatusText;
        report["layout"] = new
        {
            window = DescribeBounds(window),
            content = window.Content is Visual content ? DescribeBounds(content) : null,
            scaling = window.RenderScaling,
            screen = window.Screens.Primary is { } screen
                ? $"bounds {screen.Bounds} · work {screen.WorkingArea} · scale {screen.Scaling}"
                : "unknown",
        };
        report["windowIcon"] = window.Icon is not null;
        report["libraryLayout"] = DescribeLayout(window);
        report["statusBar"] = window.FindControl<Control>("StatusBar") is { } statusBar
            ? DescribeBounds(statusBar)
            : "missing";
        report["libraryScroll"] = window.FindControl<Control>("LibraryScroll") is { } scroll
            ? DescribeBounds(scroll)
            : "missing";

        SaveScreenshot(window, Path.Combine(output, "library.png"));
        report["uiTree"] = DescribeTree(window, 4);

        // Search / sort / filter smoke.
        viewModel.SearchText = "合成样本 3";
        await Task.Delay(500);
        report["searchMatches"] = viewModel.FilteredCount;
        SaveScreenshot(window, Path.Combine(output, "library-search.png"));
        viewModel.SearchText = string.Empty;
        await Task.Delay(400);

        // Multi-source: a second root that holds copies of the first three works
        // plus one work that only exists there. Copies must collapse into a
        // single card and be listed by the version window.
        string backup = Path.Combine(Path.GetTempPath(), "EhReaderSmokeBackup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        CreateBackupLibrary(backup);
        report["backupLibrary"] = backup;
        string? addError = await viewModel.AddSourceAsync(backup);
        await WaitUntilAsync(() => !viewModel.IsScanning && viewModel.Galleries.Count > 0, TimeSpan.FromSeconds(90));
        await Task.Delay(1200);

        var multi = viewModel.Galleries.Where(g => g.HasMultipleVersions).ToList();
        report["multiSource"] = new
        {
            sources = viewModel.SourceCount,
            works = viewModel.TotalGalleryCount,
            multiVersionCards = multi.Count,
            copies = multi.Sum(m => m.Versions.Count),
            addError,
            allShareWorkId = multi.All(m => m.Versions.All(v => v.WorkId == m.WorkId)),
            distinctStableIds = multi.All(m => m.Versions.Select(v => v.StableId).Distinct().Count() == m.Versions.Count),
        };
        SaveScreenshot(window, Path.Combine(output, "library-multi-source.png"));

        // Regression guard: a Button inside the cover overlay used to leave the
        // sibling badge measured at zero width when ItemsRepeater reused the
        // container, so the page count silently disappeared.
        if (window.Content is Visual probe)
        {
            Border? firstCard = probe.GetVisualDescendants().OfType<Border>()
                .Where(b => b.Classes.Contains("card"))
                .OrderBy(b => b.Bounds.Y)
                .ThenBy(b => b.Bounds.X)
                .FirstOrDefault();
            TextBlock? badge = firstCard?.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.IsVisible && t.Text is { Length: > 0 } &&
                                     t.Text.EndsWith("页", StringComparison.Ordinal) && t.Bounds.Y < 40);
            report["firstCardBadge"] = badge is null
                ? "missing"
                : $"{badge.Text} width={badge.Bounds.Width:F0}";
            report["firstCardBadgeOk"] = badge is not null && badge.Bounds.Width > 4;

            // Any incomplete card whose warning chip failed to measure shows up
            // here; the guard exists because a recycled container used to keep
            // the TextBlock at zero width.
            var broken = new List<string>();
            foreach (Border card in probe.GetVisualDescendants().OfType<Border>()
                         .Where(b => b.Classes.Contains("card")))
            {
                if (card.DataContext is not GalleryItemViewModel cardItem) continue;
                if (!cardItem.IsIncomplete || cardItem.IncompleteText.Length == 0) continue;
                TextBlock? chip = card.GetVisualDescendants().OfType<TextBlock>()
                    .FirstOrDefault(t => t.IsEffectivelyVisible && t.Text == cardItem.IncompleteText);
                if (chip is null || chip.Bounds.Width <= 4) broken.Add(cardItem.Title);
            }

            report["emptyBadges"] = broken.Count == 0 ? "none" : string.Join(" | ", broken);
        }

        GalleryItemViewModel? merged = multi.OrderByDescending(m => m.Versions.Count).FirstOrDefault();
        if (merged is not null)
        {
            // Reading progress must survive a copy switch: record one on the
            // first copy, then read it back through the second.
            var versionsVm = new VersionsViewModel(merged, viewModel.Services);
            var versionsWindow = new VersionsWindow(versionsVm)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Position = new PixelPoint(70, 70),
            };
            versionsWindow.Show();
            await Task.Delay(1200);
            SaveScreenshot(versionsWindow, Path.Combine(output, "versions.png"));
            report["versionsWindow"] = new
            {
                rows = versionsVm.Versions.Count,
                summary = versionsVm.SummaryText,
                defaultSource = versionsVm.Versions[0].SourceName,
                pageCounts = versionsVm.Versions.Select(v => v.Info.PageCount).ToArray(),
            };
            versionsWindow.Close();
            await Task.Delay(300);
        }

        GalleryItemViewModel? item = viewModel.Galleries.FirstOrDefault(i => i.PageCount > 3);
        if (item is not null)
        {
            var details = new GalleryDetailsWindow(item, viewModel.Services)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Position = new PixelPoint(60, 60),
            };
            details.Show();
            await Task.Delay(1500);
            SaveScreenshot(details, Path.Combine(output, "details.png"));
            details.Close();
            await Task.Delay(200);

            var readerViewModel = new ReaderViewModel(viewModel.Services, item.Info);
            var reader = new ReaderWindow(readerViewModel)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Position = new PixelPoint(40, 40),
            };
            reader.Show();
            await Task.Delay(1800);
            SaveScreenshot(reader, Path.Combine(output, "reader-single.png"));
            report["readerPage"] = readerViewModel.PageLabel;
            report["readerLayout"] = DescribeLayout(reader);

            readerViewModel.Step(1);
            await Task.Delay(500);
            string pageBeforeClose = readerViewModel.PageLabel;

            await readerViewModel.SetModeAsync(ReadingMode.Continuous);
            await WaitUntilAsync(() => !readerViewModel.IsLoadingDimensions, TimeSpan.FromSeconds(60));
            await Task.Delay(1200);
            SaveScreenshot(reader, Path.Combine(output, "reader-continuous.png"));
            report["continuousItems"] = readerViewModel.ContinuousItems.Count;

            reader.Close();
            await Task.Delay(400);
            viewModel.RefreshItemProgress(item);

            var restored = new ReaderViewModel(viewModel.Services, item.Info);
            report["progressRestore"] = new
            {
                expected = pageBeforeClose,
                restored = restored.PageLabel,
                ok = restored.PageLabel == pageBeforeClose,
            };
            restored.Dispose();
        }

        var settings = new SettingsWindow(new SettingsViewModel(viewModel.Services), viewModel)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(80, 80),
        };
        settings.Show();
        await Task.Delay(1500);
        SaveScreenshot(settings, Path.Combine(output, "settings.png"));
        settings.Close();
        await Task.Delay(200);

        // Reader window behaviour: must be non-modal (library stays usable) and
        // must not disturb the library viewport or its ordering when it closes.
        var libraryScroll = window.FindControl<ScrollViewer>("LibraryScroll");
        object? readerWindowReport = null;
        if (libraryScroll is not null)
        {
            libraryScroll.Offset = new Vector(0, 640);
            await Task.Delay(500);

            GalleryItemViewModel? target = viewModel.Galleries
                .Skip(viewModel.Galleries.Count / 2)
                .FirstOrDefault(g => g.PageCount > 3);
            if (target is not null)
            {
                double offsetBefore = libraryScroll.Offset.Y;
                List<string> orderBefore = viewModel.Galleries.Take(6).Select(g => g.StableId).ToList();

                viewModel.OpenGalleryCommand.Execute(target);
                await Task.Delay(1600);

                ReaderWindow? reader = window.OwnedWindows.OfType<ReaderWindow>().FirstOrDefault();
                bool nonModal = reader is not null && window.IsEnabled;
                string? readerPage = (reader?.DataContext as ReaderViewModel)?.PageLabel;
                bool singleWindowPerGallery = false;
                if (reader is not null)
                {
                    // Opening the same gallery again must activate, not duplicate.
                    viewModel.OpenGalleryCommand.Execute(target);
                    await Task.Delay(700);
                    singleWindowPerGallery = window.OwnedWindows.OfType<ReaderWindow>().Count() == 1;
                    reader.Close();
                }

                await Task.Delay(900);
                double offsetAfter = libraryScroll.Offset.Y;
                List<string> orderAfter = viewModel.Galleries.Take(6).Select(g => g.StableId).ToList();

                readerWindowReport = new
                {
                    nonModal,
                    mainWindowEnabled = window.IsEnabled,
                    singleWindowPerGallery,
                    readerPage,
                    offsetBefore = Math.Round(offsetBefore, 1),
                    offsetAfter = Math.Round(offsetAfter, 1),
                    scrollStable = Math.Abs(offsetBefore - offsetAfter) < 1,
                    orderStable = orderBefore.SequenceEqual(orderAfter),
                };
            }
        }

        report["readerWindow"] = readerWindowReport;

        // Corrupt page regression: page 3 of the synthetic gallery 900029 is a
        // zero-byte file. Decoding fails, but the wheel must still turn pages.
        GalleryItemViewModel? corrupt = viewModel.Galleries.FirstOrDefault(g =>
            g.FolderName.StartsWith("900029-", StringComparison.Ordinal));
        if (corrupt is not null)
        {
            var corruptViewModel = new ReaderViewModel(viewModel.Services, corrupt.Info);
            var corruptWindow = new ReaderWindow(corruptViewModel)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Position = new PixelPoint(80, 80),
            };
            corruptWindow.Show();
            await Task.Delay(1200);
            corruptViewModel.GoToIndex(2, scrollContinuous: false);
            await Task.Delay(900);
            string corruptBefore = corruptViewModel.PageLabel;
            bool handled = corruptWindow.SimulateWheelForTest(-1);
            await Task.Delay(500);
            string corruptAfter = corruptViewModel.PageLabel;
            report["corruptPageWheel"] = new
            {
                page = corruptBefore,
                after = corruptAfter,
                handled,
                ok = corruptBefore != corruptAfter,
            };
            corruptWindow.Close();
            await Task.Delay(300);
        }

        // Virtualization stress: 6,315 metadata-only galleries (no images).
        string stressRoot = Path.Combine(Path.GetTempPath(), "EhReaderStressLib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stressRoot);
        CreateStressLibrary(stressRoot, 6315);
        var stressWatch = Stopwatch.StartNew();
        string? stressError = await viewModel.SetRootAsync(stressRoot);
        await WaitUntilAsync(() => !viewModel.IsScanning, TimeSpan.FromSeconds(180));
        stressWatch.Stop();
        await Task.Delay(900);

        int realized = window.Content is Visual contentRoot ? CountDescendants<AsyncImage>(contentRoot) : -1;
        double extent = 0;
        int realizedBottom = realized;
        if (libraryScroll is not null)
        {
            extent = libraryScroll.Extent.Height;
            libraryScroll.Offset = new Vector(0, Math.Max(0, extent - libraryScroll.Viewport.Height));
            await Task.Delay(1000);
            realizedBottom = window.Content is Visual scrolled ? CountDescendants<AsyncImage>(scrolled) : -1;
        }

        report["stress"] = new
        {
            galleries = viewModel.TotalGalleryCount,
            scanSeconds = Math.Round(stressWatch.Elapsed.TotalSeconds, 2),
            realizedContainers = realized,
            realizedAfterScroll = realizedBottom,
            extentHeight = Math.Round(extent, 0),
            error = stressError,
        };

        report["elapsedSeconds"] = Math.Round(stopwatch.Elapsed.TotalSeconds, 2);
        report["screenshots"] = new[]
        {
            "library.png", "library-search.png", "details.png", "reader-single.png", "reader-continuous.png",
            "settings.png",
        };
        await WriteReport(output, report);
        Console.WriteLine("UI smoke screenshots: " + output);
    }

    private static async Task WriteReport(string directory, Dictionary<string, object?> report)
    {
        string path = Path.Combine(directory, "ui-smoke.json");
        await DiagnosticReport.WriteAsync(path, report);
        Console.WriteLine("UI smoke report: " + path);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (condition()) return;
            await Task.Delay(150);
        }
    }

    private static void SaveScreenshot(Window window, string path)
    {
        Visual visual = window.Content as Visual ?? window;
        Size size = visual.Bounds.Size;
        if (size.Width < 10 || size.Height < 10)
        {
            size = new Size(1200, 800);
        }

        // Render at physical resolution so glyphs are rasterised at the same
        // scale as on screen (avoids subpixel ghosting in the capture).
        double scaling = Math.Max(1.0, window.RenderScaling);
        var pixelSize = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(size.Width * scaling)),
            Math.Max(1, (int)Math.Ceiling(size.Height * scaling)));
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scaling, 96 * scaling));
        bitmap.Render(visual);
        bitmap.Save(path);
    }

    private static string DescribeTree(Visual visual, int depth)
    {
        var lines = new List<string>();
        Walk(visual, 0, depth, lines);
        return string.Join("\n", lines);
    }

    private static string DescribeBounds(Visual visual) =>
        $"{visual.Bounds.X:F0},{visual.Bounds.Y:F0} {visual.Bounds.Width:F0}x{visual.Bounds.Height:F0}";

    private static string DescribeLayout(Window window)
    {
        var lines = new List<string>();
        if (window.Content is Visual root)
        {
            CollectLayout(root, root, lines);
        }

        return string.Join("\n", lines);
    }

    private static void CollectLayout(Visual visual, Visual root, List<string> lines)
    {
        if (lines.Count >= 80) return;
        string? label = visual switch
        {
            TextBlock text when !string.IsNullOrWhiteSpace(text.Text) =>
                text.Text.Length > 28 ? text.Text[..28] + "…" : text.Text,
            Button button => "Button:" + (button.Content?.ToString() ?? string.Empty),
            _ => null,
        };

        if (label is not null)
        {
            Point point = visual.TranslatePoint(new Point(0, 0), root) ?? new Point();
            lines.Add($"{point.X:F0},{point.Y:F0} {visual.Bounds.Width:F0}x{visual.Bounds.Height:F0}  {label}");
        }

        foreach (Visual child in visual.GetVisualChildren())
        {
            CollectLayout(child, root, lines);
        }
    }

    private static void Walk(Visual visual, int level, int maxDepth, List<string> lines)
    {
        if (level > maxDepth) return;
        string name = visual.GetType().Name;
        if (visual is TextBlock text && !string.IsNullOrWhiteSpace(text.Text))
        {
            name += " \"" + (text.Text.Length > 32 ? text.Text[..32] + "…" : text.Text) + "\"";
        }

        lines.Add(new string(' ', level * 2) + name);
        foreach (Visual child in visual.GetVisualChildren())
        {
            Walk(child, level + 1, maxDepth, lines);
        }
    }

    private static int CountDescendants<T>(Visual root) where T : Visual
    {
        int count = root is T ? 1 : 0;
        foreach (Visual child in root.GetVisualChildren())
        {
            count += CountDescendants<T>(child);
        }

        return count;
    }

    private static void CreateStressLibrary(string root, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Directory.CreateDirectory(Path.Combine(root, $"{800000 + i}-[Chinese] 压力样本 {i:00000}"));
        }
    }

    private static void CreateBackupLibrary(string root)
    {
        for (int i = 0; i < 3; i++)
        {
            long gid = 900000 + i;
            int pageCount = 2 + i;
            string folder = Path.Combine(root, $"{gid}-[Japanese] 备份副本 {i + 1:00}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, ".ehviewer"),
                $"VERSION2\n00000000\n{gid}\ntoken\n1\n{pageCount}\n{pageCount}\n{pageCount}\n");
            for (int page = 1; page <= pageCount; page++)
            {
                File.WriteAllBytes(Path.Combine(folder, $"{page:00000000}.png"),
                    RenderPage(page, pageCount, 40 + i, 620, 880));
            }

            File.WriteAllBytes(Path.Combine(folder, ".thumb"), RenderPage(1, pageCount, 40 + i, 320, 440));
        }

        string only = Path.Combine(root, "900100-[Chinese] 仅备份存在");
        Directory.CreateDirectory(only);
        File.WriteAllText(Path.Combine(only, ".ehviewer"), "VERSION2\n00000000\n900100\ntoken\n1\n2\n2\n2\n");
        for (int page = 1; page <= 2; page++)
        {
            File.WriteAllBytes(Path.Combine(only, $"{page:00000000}.png"), RenderPage(page, 2, 77, 620, 880));
        }
    }

    private static void CreateSyntheticLibrary(string root)
    {
        var random = new Random(20261108);
        string[] tags = ["[Chinese]", "[English]", "[Japanese]", "[Chinese] [Digital]"];
        for (int i = 0; i < 30; i++)
        {
            long gid = 900000 + i;
            int pageCount = 4 + (i % 6);
            string folderName = $"{gid}-{tags[i % tags.Length]} 合成样本 {i + 1:00}";
            if (i == 27)
            {
                folderName = "无 GID 合成样本";
            }

            if (i == 28)
            {
                gid = 900000; // duplicate gid
                folderName = $"{gid}-[English] 重复 GID 样本";
            }

            string folder = Path.Combine(root, folderName);
            Directory.CreateDirectory(folder);

            var header = $"VERSION2\n00000000\n{gid}\ntoken\n1\n{pageCount}\n{pageCount}\n{pageCount}\n";
            File.WriteAllText(Path.Combine(folder, ".ehviewer"), header);

            for (int page = 1; page <= pageCount; page++)
            {
                if (i == 29 && page == 3)
                {
                    File.WriteAllBytes(Path.Combine(folder, $"{page:00000000}.png"), []);
                    continue;
                }

                if (i == 26 && page == pageCount)
                {
                    continue; // trailing missing page
                }

                byte[] image = RenderPage(page, pageCount, i, 620, 880);
                File.WriteAllBytes(Path.Combine(folder, $"{page:00000000}.png"), image);
            }

            byte[] thumb = RenderPage(1, pageCount, i, 320, 440);
            File.WriteAllBytes(Path.Combine(folder, ".thumb"), thumb);

            if (i % 9 == 4)
            {
                File.Delete(Path.Combine(folder, ".thumb"));
            }
        }

        // One empty directory and one hidden temp directory that must be skipped.
        Directory.CreateDirectory(Path.Combine(root, "910000-[Chinese] 空样本"));
        Directory.CreateDirectory(Path.Combine(root, new string('a', 64) + ".tmp"));
    }

    private static byte[] RenderPage(int page, int pageCount, int galleryIndex, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        var hue = (galleryIndex * 37 + page * 11) % 360;
        var background = SKColor.FromHsl(hue, 42, 24);
        canvas.Clear(background);

        using var stripe = new SKPaint { Color = SKColor.FromHsl(hue, 60, 46), IsAntialias = true };
        canvas.DrawRect(0, height * 0.12f, width, 6, stripe);
        canvas.DrawRect(0, height * 0.88f, width, 6, stripe);
        canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) * 0.28f, stripe);

        using var text = new SKPaint
        {
            Color = SKColors.White,
            TextSize = Math.Max(28, width / 9f),
            IsAntialias = true,
            TextAlign = SKTextAlign.Center,
        };
        canvas.DrawText($"P{page}", width / 2f, height / 2f + text.TextSize / 3f, text);
        using var small = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 170),
            TextSize = Math.Max(16, width / 18f),
            IsAntialias = true,
            TextAlign = SKTextAlign.Center,
        };
        canvas.DrawText($"{page} / {pageCount}", width / 2f, height * 0.78f, small);
        canvas.Flush();

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }
}

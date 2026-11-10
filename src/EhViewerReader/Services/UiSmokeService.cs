using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EhViewerReader.Controls;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Online;
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
        var pageShots = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        ReadingMode originalMode = viewModel.Services.Settings.DefaultReadingMode;

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
        report["statusBar"] = window.LibraryStatusBar is { } statusBar
            ? DescribeBounds(statusBar)
            : "missing";
        report["libraryScroll"] = window.LibraryScroll is { } scroll
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

            await readerViewModel.SetModeAsync(ReadingMode.Continuous);
            await WaitUntilAsync(() => !readerViewModel.IsLoadingDimensions, TimeSpan.FromSeconds(60));
            await Task.Delay(1200);
            SaveScreenshot(reader, Path.Combine(output, "reader-continuous.png"));
            report["continuousItems"] = readerViewModel.ContinuousItems.Count;

            // Horizontal continuous mode (ReadingMode.Double): pages side by
            // side at a common height; merged two-page images keep their width.
            await readerViewModel.SetModeAsync(ReadingMode.Double);
            await WaitUntilAsync(() => !readerViewModel.IsLoadingDimensions, TimeSpan.FromSeconds(60));
            await Task.Delay(1200);
            var horizontalScroll = reader.FindControl<ScrollViewer>("HorizontalScroll");
            if (horizontalScroll is not null)
            {
                readerViewModel.SetContinuousHeight(horizontalScroll.Viewport.Height);
            }

            await Task.Delay(700);
            double firstItemWidth = 0;
            if (horizontalScroll?.Content is Visual hContent)
            {
                Border? firstHBorder = hContent.GetVisualDescendants().OfType<Border>().FirstOrDefault();
                firstItemWidth = firstHBorder?.Bounds.Width ?? 0;
            }
            double xBefore = horizontalScroll?.Offset.X ?? 0;
            string pageAtProbe = readerViewModel.PageLabel;
            readerViewModel.Step(1);
            await Task.Delay(800);
            double xAfter = horizontalScroll?.Offset.X ?? 0;
            string pageAfterStep = readerViewModel.PageLabel;
            report["horizontalMode"] = new
            {
                items = readerViewModel.HorizontalItems.Count,
                displayItems = readerViewModel.HorizontalDisplayItems.Count,
                rtlReversed = readerViewModel.RightToLeft &&
                    !ReferenceEquals(readerViewModel.HorizontalDisplayItems.FirstOrDefault(),
                                     readerViewModel.HorizontalItems.FirstOrDefault()),
                extentWidth = Math.Round(horizontalScroll?.Extent.Width ?? 0, 0),
                viewportWidth = Math.Round(horizontalScroll?.Viewport.Width ?? 0, 0),
                firstItemWidth = Math.Round(firstItemWidth, 1),
                vmItemWidth = readerViewModel.HorizontalItems.Count > 0
                    ? Math.Round(readerViewModel.HorizontalItems[0].DisplayWidth, 1)
                    : 0,
                vmPageWidths = readerViewModel.HorizontalItems.Select(i => Math.Round(i.DisplayWidth, 1)).ToArray(),
                uiPageWidths = horizontalScroll is not null && horizontalScroll.Content is Visual hc
                    ? hc.GetVisualDescendants().OfType<Border>().Select(b => Math.Round(b.Bounds.Width, 1)).ToArray()
                    : null,
                vmLefts = readerViewModel.HorizontalDisplayItems.Select(i => Math.Round(i.Left, 1)).ToArray(),
                widerThanViewport = (horizontalScroll?.Extent.Width ?? 0) > (horizontalScroll?.Viewport.Width ?? 0),
                pageAtProbe,
                pageAfterStep,
                offsetXBefore = Math.Round(xBefore, 1),
                offsetXAfter = Math.Round(xAfter, 1),
                stepMoved = Math.Abs(xAfter - xBefore) > 10,
            };
            SaveScreenshot(reader, Path.Combine(output, "reader-horizontal.png"));

            // Slider precision: a fractional slider position must land inside
            // the target page (5.5 → page 5, halfway), matching scroll pixels.
            ReadingMode savedMode = viewModel.Services.Settings.DefaultReadingMode;
            readerViewModel.SliderValue = 5.5;
            await Task.Delay(800);
            double expectedSliderOffset = readerViewModel.GetHorizontalOffsetForPage(4, 0.5);
            var offsetSamples = new List<double>();
            for (int i = 0; i < 3; i++)
            {
                offsetSamples.Add(Math.Round(horizontalScroll?.Offset.X ?? 0, 1));
                await Task.Delay(400);
            }

            report["sliderPrecision"] = new
            {
                landedPage = readerViewModel.CurrentPageNumber,
                slider = Math.Round(readerViewModel.SliderValue, 2),
                itemWidth = Math.Round(readerViewModel.HorizontalItems[4].DisplayWidth, 1),
                expectedOffset = Math.Round(expectedSliderOffset, 1),
                raisedTarget = Math.Round(readerViewModel.LastHorizontalTarget ?? -1, 1),
                pageOffset = Math.Round(readerViewModel.PageOffsetForTest, 3),
                offsetSamples,
                ok = Math.Abs((horizontalScroll?.Offset.X ?? 0) - expectedSliderOffset) < 24,
            };

            // The mode chosen in the reader persists as the default...
            report["modePersisted"] = viewModel.Services.Settings.DefaultReadingMode == ReadingMode.Double;

            // ...and clicking the active mode chip again returns to single page.
            var toggleVm = readerViewModel;
            await toggleVm.SetModeAsync(toggleVm.IsHorizontalMode ? ReadingMode.Single : ReadingMode.Double);
            await Task.Delay(600);
            report["modeToggleBack"] = new
            {
                singleAfterToggle = toggleVm.IsSingleMode,
                savedMode = (int)viewModel.Services.Settings.DefaultReadingMode,
                ok = toggleVm.IsSingleMode &&
                     viewModel.Services.Settings.DefaultReadingMode == ReadingMode.Single,
            };

            // In horizontal mode the probe point advances pages as the window
            // shows several at once, so the progress to restore is whatever the
            // reader is on when it actually closes.
            string pageBeforeClose = readerViewModel.PageLabel;

            reader.Close();
            await Task.Delay(400);
            viewModel.RefreshItemProgress(item);

            // Restore the user's default reading mode after the toggle test.
            viewModel.Services.Settings.DefaultReadingMode = savedMode;
            viewModel.Services.TrySaveSettings(out _);

            var restored = new ReaderViewModel(viewModel.Services, item.Info);
            report["progressRestore"] = new
            {
                expected = pageBeforeClose,
                restored = restored.PageLabel,
                ok = restored.PageLabel == pageBeforeClose,
            };
            restored.Dispose();
        }

        // History is local: the reader just recorded progress for the item above.
        HistoryViewModel? history = window.Shell?.History;
        history?.Reload();
        window.Shell?.Navigate("history");
        await Task.Delay(700);
        SaveScreenshot(window, Path.Combine(output, "page-history.png"));
        pageShots.Add("page-history.png");
        window.Shell?.Navigate("library");
        await Task.Delay(200);
        report["localHistory"] = new Dictionary<string, object?>
        {
            ["items"] = history?.Items.Count ?? 0,
            ["first"] = history?.Items.FirstOrDefault()?.Title,
            ["lastRead"] = history?.Items.FirstOrDefault()?.LastReadText,
            ["progress"] = history?.Items.FirstOrDefault()?.ProgressText,
        };

        // Animated GIF regression: synthetic gallery "900000-[Chinese] 合成样本 01"
        // carries a two-frame GIF as page 2. The 900000 card is a merged
        // three-version card whose preferred version is NOT the one holding the
        // GIF, so the lookup must scan every version's folder.
        GalleryInfo? gifInfo = viewModel.Galleries
            .SelectMany(g => g.Versions.Count > 1 ? g.Versions : [g.Info])
            .FirstOrDefault(info => info.FolderPath is not null &&
                                    File.Exists(Path.Combine(info.FolderPath, "00000002.gif")));
        report["gifLookup"] = new
        {
            found = gifInfo is not null,
            folder = gifInfo?.FolderPath,
        };
        if (gifInfo is not null)
        {
            string gifPath = Path.Combine(gifInfo.FolderPath, "00000002.gif");
            ImageDecoder decoder = viewModel.Services.Images.Decoder;
            DecodedImage? gif = await decoder.DecodeAsync(gifPath, null, null, 24, 16000, CancellationToken.None);
            DecodedImage? gifFrame1 = gif is not null ? await gif.DecodeNextFrameAsync(CancellationToken.None) : null;
            report["animatedGif"] = new
            {
                isAnimated = gif?.IsAnimated == true,
                frameCount = gif?.FrameCount ?? 0,
                frame1Ok = gifFrame1 is not null && gifFrame1.FrameIndex == 1,
                durationMs = gif?.FrameDurationMs ?? 0,
            };
            gifFrame1?.Dispose();
            gif?.Dispose();

            // Per-frame decode cost. Frames of a "dirty rect" GIF depend on their
            // predecessor, so this separates "decoding is slow" from "presenting
            // is slow" when the animation stutters.
            var decodeTimes = new List<double>();
            AnimationCursor? decodeCursor = decoder.TryOpenAnimationCursor(gifPath, null, null, 24, 16000);
            if (decodeCursor is not null)
            {
                for (int i = 1; i < decodeCursor.FrameCount; i++)
                {
                    var frameWatch = Stopwatch.StartNew();
                    DecodedImage? frame = await decodeCursor.DecodeFrameAsync(i, CancellationToken.None);
                    frameWatch.Stop();
                    decodeTimes.Add(Math.Round(frameWatch.Elapsed.TotalMilliseconds, 1));
                    frame?.Dispose();
                }

                decodeCursor.Dispose();
            }

            report["gifDecodeProbe"] = new
            {
                frames = decodeTimes.Count,
                avgMs = decodeTimes.Count > 0 ? Math.Round(decodeTimes.Average(), 1) : 0,
                maxMs = decodeTimes.Count > 0 ? decodeTimes.Max() : 0,
                lastMs = decodeTimes.Count > 0 ? decodeTimes[^1] : 0,
                perFrame = decodeTimes,
            };

            var gifViewModel = new ReaderViewModel(viewModel.Services, gifInfo);
            var gifReader = new ReaderWindow(gifViewModel)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Position = new PixelPoint(100, 100),
            };
            gifReader.Show();
            await Task.Delay(1200);
            // The reading mode is a persisted setting and may be horizontal;
            // animation playback is asserted on the single-page surface.
            await gifViewModel.SetModeAsync(ReadingMode.Single);
            await Task.Delay(600);
            gifViewModel.GoToIndex(1, scrollContinuous: false);
            await Task.Delay(1500);
            ZoomPanView? singleView = gifReader.FindControl<ZoomPanView>("SingleView");

            // Each applied frame is a NEW DecodedImage object (FrameIndex is
            // immutable), so re-read the control's current session every time.
            //
            // Frame-pacing and stall regression: the synthetic GIF declares 60 ms
            // per frame (16 fps). observedFps catches "runs too slow", maxGapMs
            // catches "freezes for a while in the middle".
            var sampledFrames = new List<int>();
            var sampleWatch = Stopwatch.StartNew();
            int transitions = 0;
            long maxGapMs = 0;
            long lastChangeMs = 0;
            int previousFrame = singleView?.AnimatedImageForTest?.FrameIndex ?? -1;
            while (sampleWatch.ElapsedMilliseconds < 3000)
            {
                int frame = singleView?.AnimatedImageForTest?.FrameIndex ?? -1;
                long nowMs = sampleWatch.ElapsedMilliseconds;
                if (previousFrame >= 0 && frame >= 0 && frame != previousFrame)
                {
                    transitions++;
                    if (lastChangeMs > 0)
                    {
                        long gap = nowMs - lastChangeMs;
                        if (gap > maxGapMs) maxGapMs = gap;
                    }

                    lastChangeMs = nowMs;
                }

                previousFrame = frame;
                if (sampledFrames.Count < 24) sampledFrames.Add(frame);
                await Task.Delay(20);
            }

            double observedSeconds = Math.Max(0.001, sampleWatch.Elapsed.TotalSeconds);
            double observedFps = Math.Round(transitions / observedSeconds, 1);
            const int declaredFps = 16; // 60 ms per frame, rounded down
            report["readerAnimation"] = new
            {
                hasSession = (singleView?.AnimatedImageForTest) is not null,
                frameCount = singleView?.AnimatedImageForTest?.FrameCount ?? 0,
                samples = sampledFrames,
                transitions,
                observedFps,
                declaredFps,
                maxGapMs,
                // A frame is skipped only when its decode overruns its own
                // duration; a gap of several periods is a visible freeze.
                pacingOk = observedFps >= declaredFps * 0.8,
                noStall = maxGapMs < 400,
                advancing = transitions >= 2,
                stopReason = singleView?.AnimationStopReason,
            };
            SaveScreenshot(gifReader, Path.Combine(output, "reader-animated.png"));
            gifReader.Close();
            await Task.Delay(300);
            gifViewModel.Dispose();
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
        var libraryScroll = window.LibraryScroll;
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
            // The default reading mode is a persisted setting and may be
            // horizontal; this regression is about single-page wheel turning.
            await corruptViewModel.SetModeAsync(ReadingMode.Single);
            await Task.Delay(400);
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

        // Shell navigation: every page must instantiate and lay out. Network
        // loads are fire-and-forget, so the capture happens while they are in
        // flight and a missing page shows up as an empty content host.
        if (window.Shell is { } shell)
        {
            foreach (string key in new[] { "online", "favorites", "downloads", "settings" })
            {
                shell.Navigate(key);
                await Task.Delay(900);
                string name = "page-" + key + ".png";
                SaveScreenshot(window, Path.Combine(output, name));
                pageShots.Add(name);
            }

            shell.Navigate("online");
            shell.Online.ShowAdvanced = true;
            shell.Online.ShowHistory = true;
            await Task.Delay(700);
            SaveScreenshot(window, Path.Combine(output, "page-online-advanced.png"));
            pageShots.Add("page-online-advanced.png");

            // The online settings live below the fold; scroll there so the
            // translation / filter / favourite-slot editors are captured too.
            shell.Navigate("settings");
            await Task.Delay(400);
            if (window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } settingsScroll)
            {
                settingsScroll.Offset = new Vector(0, 470);
                await Task.Delay(500);
                SaveScreenshot(window, Path.Combine(output, "page-settings-online.png"));
                pageShots.Add("page-settings-online.png");
                settingsScroll.Offset = new Vector(0, 0);
            }
            shell.Online.ShowAdvanced = false;

            // Online gallery detail (network dependent): validates the XAML and
            // the scrape → view model path. A failure is recorded, not fatal.
            if (shell.Online.Items.FirstOrDefault() is { } first)
            {
                try
                {
                    var detailViewModel = new OnlineGalleryViewModel(AppServices.Current, shell, first.Summary);
                    var detailWindow = new OnlineGalleryWindow(detailViewModel)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Position = new PixelPoint(60, 60),
                    };
                    detailWindow.Show();
                    await Task.Delay(4000);
                    // The comment box can take focus and scroll the body; capture
                    // the header state instead.
                    if (detailWindow.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } detailScroll)
                    {
                        detailScroll.Offset = new Vector(0, 0);
                        await Task.Delay(300);
                    }

                    SaveScreenshot(detailWindow, Path.Combine(output, "online-detail.png"));
                    pageShots.Add("online-detail.png");
                    report["onlineDetail"] = new
                    {
                        title = detailViewModel.Title,
                        tags = detailViewModel.Tags.Count,
                        status = detailViewModel.StatusText,
                        pages = detailViewModel.PagesText,
                        comments = detailViewModel.Comments.Count,
                        canInteract = detailViewModel.CanInteract,
                        hasTorrents = detailViewModel.HasTorrents,
                        hasArchive = detailViewModel.HasArchive,
                        translatedTags = detailViewModel.Tags.Take(3).Select(t => t.Display).ToArray(),
                    };

                    // Prefer the opened gallery; otherwise look a few cards ahead
                    // so the torrent window still exercises a populated list.
                    EhGalleryDetails? torrentDetails = detailViewModel.Details is { TorrentCount: > 0 } loaded ? loaded : null;
                    if (torrentDetails is null)
                    {
                        foreach (var candidate in shell.Online.Items.Skip(1).Take(3))
                        {
                            try
                            {
                                EhGalleryDetails candidateDetails = await AppServices.Current.Online
                                    .GetDetailsAsync(candidate.Summary.Gid, candidate.Summary.Token, CancellationToken.None);
                                if (candidateDetails.TorrentCount > 0)
                                {
                                    torrentDetails = candidateDetails;
                                    break;
                                }
                            }
                            catch (Exception ex) when (ex is EhException or HttpRequestException)
                            {
                                // Network hiccup: skip this candidate.
                            }
                        }
                    }

                    if (torrentDetails is not null)
                    {
                        var torrentViewModel = new TorrentViewModel(AppServices.Current, torrentDetails!);
                        var torrentWindow = new TorrentWindow(torrentViewModel)
                        {
                            WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        };
                        torrentWindow.Show();
                        await Task.Delay(3000);
                        SaveScreenshot(torrentWindow, Path.Combine(output, "online-torrents.png"));
                        pageShots.Add("online-torrents.png");
                        report["onlineTorrents"] = new
                        {
                            count = torrentViewModel.Items.Count,
                            status = torrentViewModel.StatusText,
                        };
                        torrentWindow.Close();
                        await Task.Delay(200);
                    }

                    if (detailViewModel.Details is { } archiveDetails && archiveDetails.ArchiveUrl.Length > 0)
                    {
                        var archiveViewModel = new ArchiveViewModel(AppServices.Current, shell, archiveDetails);
                        var archiveWindow = new ArchiveWindow(archiveViewModel)
                        {
                            WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        };
                        archiveWindow.Show();
                        await Task.Delay(2500);
                        SaveScreenshot(archiveWindow, Path.Combine(output, "online-archive.png"));
                        pageShots.Add("online-archive.png");
                        report["onlineArchive"] = new
                        {
                            options = archiveViewModel.Options.Count,
                            needsSignIn = archiveViewModel.NeedsSignIn,
                            status = archiveViewModel.StatusText,
                        };
                        archiveWindow.Close();
                        await Task.Delay(200);
                    }

                    detailWindow.Close();
                    await Task.Delay(200);
                }
                catch (Exception ex)
                {
                    report["onlineDetailError"] = ex.Message;
                }
            }

            // Browser login: goes through the shell command exactly like the
            // sidebar button, launches (and closes) a real browser with a
            // throwaway profile, so this also proves the lifecycle cleanup.
            try
            {
                shell.OpenWebLoginCommand.Execute(null);
                await Task.Delay(5000);

                if (window.LoginWindow is { } loginWindow &&
                    loginWindow.DataContext is WebLoginViewModel loginViewModel)
                {
                    SaveScreenshot(loginWindow, Path.Combine(output, "web-login.png"));
                    pageShots.Add("web-login.png");
                    report["webLogin"] = new Dictionary<string, object?>
                    {
                        ["browser"] = loginViewModel.BrowserText,
                        ["status"] = loginViewModel.StatusText,
                        ["waiting"] = loginViewModel.IsWaiting,
                    };
                    loginWindow.Close();
                    await Task.Delay(300);
                }
                else
                {
                    report["webLogin"] = "window did not open";
                }

                // Incomplete cookies must be rejected without touching the session.
                EhLoginResult bogus = await AppServices.Current.Online
                    .CompleteBrowserSignInAsync(new Dictionary<string, string>(), CancellationToken.None);
                report["webLoginRejectsEmpty"] = !bogus.Success && bogus.RequiresBrowserLogin;
            }
            catch (Exception ex)
            {
                report["webLoginError"] = ex.Message;
            }

            // Local copy: 800000 exists in the stress library (the current root
            // by now), so the detail window must offer local reading without
            // any network.
            try
            {
                var localSummary = new EhGallerySummary
                {
                    Gid = 800000,
                    Token = "local",
                    Title = "压力样本 00000（本地命中）",
                    Pages = 4,
                };
                localSummary.Tags.Add("language:chinese");

                var localCard = new OnlineGalleryItemViewModel(
                    localSummary,
                    AppServices.Current.OnlineImages.CreateThumbnailRequest(localSummary.ThumbUrl),
                    shell);
                report["localHitCard"] = new Dictionary<string, object?>
                {
                    ["inLocal"] = localCard.IsInLocalLibrary,
                    ["language"] = localCard.LanguageText,
                };

                var localViewModel = new OnlineGalleryViewModel(AppServices.Current, shell, localSummary);
                var localWindow = new OnlineGalleryWindow(localViewModel)
                {
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                };
                localWindow.Show(window);
                await Task.Delay(1500);
                SaveScreenshot(localWindow, Path.Combine(output, "online-local.png"));
                pageShots.Add("online-local.png");
                report["localHitDetail"] = new Dictionary<string, object?>
                {
                    ["inLocal"] = localViewModel.IsInLocalLibrary,
                    ["readButton"] = localViewModel.ReadButtonText,
                    ["quality"] = localViewModel.SelectedQuality.Label,
                };
                localWindow.Close();
                await Task.Delay(200);
            }
            catch (Exception ex)
            {
                report["localHitError"] = ex.Message;
            }

            shell.Navigate("library");
            await Task.Delay(400);
        }

        report["pageScreenshots"] = pageShots;
        report["elapsedSeconds"] = Math.Round(stopwatch.Elapsed.TotalSeconds, 2);

        // Restore the user's reading mode: the mode-persistence assertions in
        // this smoke run intentionally rewrite it.
        viewModel.Services.Settings.DefaultReadingMode = originalMode;
        viewModel.Services.TrySaveSettings(out _);
        report["screenshots"] = new[]
        {
            "library.png", "library-search.png", "details.png", "reader-single.png", "reader-animated.png",
            "reader-continuous.png", "reader-horizontal.png", "versions.png", "settings.png",
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
        // Warm-up render: the first pass can capture partially composed layers
        // (text runs added by an async refresh show up as doubled glyphs), so the
        // saved frame is always the second, settled pass.
        using (var warmup = new RenderTargetBitmap(pixelSize, new Vector(96 * scaling, 96 * scaling)))
        {
            warmup.Render(visual);
        }

        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scaling, 96 * scaling));
        bitmap.Render(visual);
        bitmap.Save(path, new PngBitmapEncoderOptions());
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

                if (i == 0 && page == 2)
                {
                    // Animated GIF regression page: the reader must play frames,
                    // not freeze on the first one. Sized so a per-frame full
                    // re-decode is expensive enough for the pacing check to see.
                    File.WriteAllBytes(
                        Path.Combine(folder, $"{page:00000000}.gif"),
                        RenderAnimatedGif(1280, 1800));
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

    private static byte[] RenderAnimatedGif(int width, int height) =>
        RenderAnimatedGif(width, height, frameCount: 30, delayCentiseconds: 6, dirtyRects: true);

    /// <summary>
    /// Multi-frame GIF89a written by hand so the smoke test does not depend on an
    /// encoder that SkiaSharp does not provide for multi-frame formats.
    ///
    /// <paramref name="dirtyRects"/> produces the shape real web GIFs have: a
    /// transparent frame that only paints a small moving square on top of the
    /// previous frame (disposal = "do not dispose"). Every frame is therefore
    /// dependent on its predecessor, which is what makes per-frame re-decoding
    /// from frame 0 expensive.
    /// </summary>
    private static byte[] RenderAnimatedGif(
        int width, int height, int frameCount, int delayCentiseconds, bool dirtyRects = false)
    {
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("GIF89a"));
            writer.Write((ushort)width);
            writer.Write((ushort)height);
            writer.Write((byte)0x81); // global color table, 2^(1+1) = 4 colors
            writer.Write((byte)0x00); // background color index
            writer.Write((byte)0x00); // pixel aspect ratio
            writer.Write(new byte[] { 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 0, 255 });
            writer.Write(new byte[] { 0x21, 0xFF, 0x0B }); // application extension
            writer.Write(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            writer.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 }); // loop forever

            if (dirtyRects)
            {
                // Opaque base frame, then transparent bands scrolling down.
                WriteGifFrame(writer, width, height, 1, delayCentiseconds);
                int band = Math.Max(8, height / 40);
                for (int i = 1; i < frameCount; i++)
                {
                    int top = (int)((long)i * band % Math.Max(1, height - band));
                    byte color = (byte)(2 + i % 2);
                    WriteGifBand(writer, width, top, band, color, delayCentiseconds);
                }
            }
            else
            {
                for (int i = 0; i < frameCount; i++)
                {
                    byte color = (byte)(2 + i % 2);
                    WriteGifFrame(writer, width, height, color, delayCentiseconds);
                }
            }

            writer.Write((byte)0x3B); // trailer
        }

        return ms.ToArray();
    }

    /// <summary>
    /// One transparent frame that only paints a band: the image descriptor covers
    /// a sub-rectangle, the rest of the pixels are the transparent index, and the
    /// graphic control extension leaves the previous frame in place.
    /// </summary>
    private static void WriteGifBand(
        BinaryWriter writer, int width, int top, int height, byte colorIndex, int delayCentiseconds)
    {
        writer.Write(new byte[] { 0x21, 0xF9, 0x04, 0x05 }); // GCE: disposal=1, transparency on
        writer.Write((ushort)delayCentiseconds);
        writer.Write(new byte[] { 0x00, 0x00 });             // transparent color index = 0
        writer.Write((byte)0x2C);                            // image descriptor
        writer.Write((ushort)0);
        writer.Write((ushort)top);
        writer.Write((ushort)width);
        writer.Write((ushort)height);
        writer.Write((byte)0x00);
        writer.Write((byte)2); // LZW min code size (4-color palette)

        var pixels = new byte[width * height];
        int idx = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Mostly transparent; a diagonal stripe carries the colour so the
                // frame is not a solid block (closer to real delta frames).
                pixels[idx++] = (x + y) % 5 < 3 ? colorIndex : (byte)0;
            }
        }

        byte[] lzw = GifLzwEncode(pixels);
        for (int offset = 0; offset < lzw.Length; offset += 255)
        {
            int chunk = Math.Min(255, lzw.Length - offset);
            writer.Write((byte)chunk);
            writer.Write(lzw, offset, chunk);
        }

        writer.Write((byte)0x00);
    }

    private static void WriteGifFrame(BinaryWriter writer, int width, int height, byte colorIndex, int delayCentiseconds)
    {
        writer.Write(new byte[] { 0x21, 0xF9, 0x04, 0x04 }); // GCE: do not dispose
        writer.Write((ushort)delayCentiseconds);
        writer.Write(new byte[] { 0x00, 0x00 });
        writer.Write((byte)0x2C); // image descriptor
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)width);
        writer.Write((ushort)height);
        writer.Write((byte)0x00);
        writer.Write((byte)2); // LZW min code size (4-color palette)
        var pixels = new byte[width * height];
        Array.Fill(pixels, colorIndex);
        byte[] lzw = GifLzwEncode(pixels);
        for (int offset = 0; offset < lzw.Length; offset += 255)
        {
            int chunk = Math.Min(255, lzw.Length - offset);
            writer.Write((byte)chunk);
            writer.Write(lzw, offset, chunk);
        }

        writer.Write((byte)0x00); // block terminator
    }

    private static byte[] GifLzwEncode(byte[] indices)
    {
        const int minCodeSize = 2;
        int clearCode = 1 << minCodeSize;
        int endCode = clearCode + 1;
        int nextCode = endCode + 1;
        int codeSize = minCodeSize + 1;
        var dict = new Dictionary<(int Prefix, int Pixel), int>();
        var output = new List<byte>();
        uint accumulator = 0;
        int bitCount = 0;

        void Emit(int code)
        {
            accumulator |= (uint)(code << bitCount);
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                output.Add((byte)(accumulator & 0xFF));
                accumulator >>= 8;
                bitCount -= 8;
            }
        }

        Emit(clearCode);
        int prefix = indices[0];
        for (int i = 1; i < indices.Length; i++)
        {
            int pixel = indices[i];
            if (dict.TryGetValue((prefix, pixel), out int code))
            {
                prefix = code;
                continue;
            }

            Emit(prefix);
            if (nextCode < 4096)
            {
                // GIF LZW: the decoder grows its code width one entry behind
                // the encoder, so the check uses the value being assigned.
                if (nextCode == (1 << codeSize) && codeSize < 12) codeSize++;
                dict[(prefix, pixel)] = nextCode++;
            }
            else
            {
                Emit(clearCode);
                dict.Clear();
                nextCode = endCode + 1;
                codeSize = minCodeSize + 1;
            }

            prefix = pixel;
        }

        Emit(prefix);
        Emit(endCode);
        if (bitCount > 0) output.Add((byte)(accumulator & 0xFF));
        return output.ToArray();
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
            IsAntialias = true,
        };
        float titleSize = Math.Max(28, width / 9f);
        using var titleFont = new SKFont { Size = titleSize };
        canvas.DrawText($"P{page}", width / 2f, height / 2f + titleSize / 3f, SKTextAlign.Center, titleFont, text);
        using var small = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 170),
            IsAntialias = true,
        };
        using var smallFont = new SKFont { Size = Math.Max(16, width / 18f) };
        canvas.DrawText($"{page} / {pageCount}", width / 2f, height * 0.78f, SKTextAlign.Center, smallFont, small);
        canvas.Flush();

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }
}

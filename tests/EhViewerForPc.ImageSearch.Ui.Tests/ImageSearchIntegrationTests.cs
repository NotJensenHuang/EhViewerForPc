using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.ImageSearch;
using EhViewerForPc.Services;
using EhViewerForPc.ViewModels;
using EhViewerForPc.Views;
using FluentAvalonia.UI.Controls;
using Microsoft.Data.Sqlite;

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public sealed class ImageSearchIntegrationTests
{
    [AvaloniaFact]
    public async Task CompletedPagesAreRecheckedBeforeBatchOrSingleRepairAndLeaveIncompleteFilter()
    {
        using var scope = new Fixture(); var physical = scope.CreateCopies(named: true);
        scope.Services.Works.Upsert(new() { WorkId = "42", Gid = 42, Token = "fixture", PageCount = 5 });
        using var shell = new ShellViewModel(scope.Services);
        await shell.Library.InitializeAsync();
        shell.Library.SelectedFilter = shell.Library.FilterOptions.Single(f => f.Value == LibraryFilter.Incomplete);
        Assert.Single(shell.Library.Galleries);
        foreach (var copy in physical)
        {
            foreach (int page in new[] { 2, 4 }) File.WriteAllBytes(Path.Combine(copy.FolderPath, $"{page:D8}.png"), SyntheticImages.Create(page, 200, 280).EncodePng());
            File.WriteAllBytes(Path.Combine(copy.FolderPath, "00000002.jpg"), []);
        }
        var stale = shell.Library.Galleries.Single().Info;
        await shell.Library.FillMissingPagesCommand.ExecuteAsync(null);
        Assert.Empty(scope.Services.Downloads.Tasks);
        Assert.Empty(shell.Library.Galleries);
        Assert.All(shell.Library.SnapshotAllWorks().Single().Versions, copy => Assert.False(copy.IsIncomplete));
        Assert.False(shell.Library.CanFillMissingPages);
        Assert.Null(await scope.Services.ContinueLocalDownloadAsync(stale, CancellationToken.None));
        Assert.Empty(scope.Services.Downloads.Tasks);
        await shell.Library.RefreshCommand.ExecuteAsync(null);
        Assert.Empty(shell.Library.Galleries);
    }

    [AvaloniaFact]
    public async Task QueriesReuseValidatedGenerationButReloadChangedFilesAndNewPublications()
    {
        using var scope = new Fixture(); var copies = scope.CreateCopies();
        scope.Services.Settings.ImageSearch.Enabled = true;
        var snapshot = copies.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
        await scope.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None, options: new IndexBuildOptions { UseGpu = false });
        byte[] query = File.ReadAllBytes(Path.Combine(copies[0].FolderPath, "00000001.png"));
        var messages = new List<string>();
        var progress = new QueryProgress(messages.Add);
        var first = await scope.Services.ImageSearch.SearchAsync(query, null, copies, progress, CancellationToken.None);
        Assert.NotEmpty(first);
        Assert.Contains(messages, m => m.Contains("校验"));
        messages.Clear();
        var repeat = await scope.Services.ImageSearch.SearchAsync(query, null, copies, progress, CancellationToken.None);
        Assert.Equal(first, repeat);
        Assert.DoesNotContain(messages, m => m.Contains("校验"));
        string current = Path.Combine(scope.Services.ImageSearch.DirectoryPath, "generations", first[0].GenerationId);
        string index = Path.Combine(current, "backend", "invlists.bin");
        File.SetLastWriteTimeUtc(index, File.GetLastWriteTimeUtc(index).AddSeconds(1));
        await scope.Services.ImageSearch.SearchAsync(query, null, copies, progress, CancellationToken.None);
        Assert.Contains(messages, m => m.Contains("校验"));
        messages.Clear();
        await scope.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None, rebuild: true, options: new IndexBuildOptions { UseGpu = false });
        var next = await scope.Services.ImageSearch.SearchAsync(query, null, copies, progress, CancellationToken.None);
        Assert.NotEqual(first[0].GenerationId, next[0].GenerationId);
        Assert.Contains(messages, m => m.Contains("校验"));
        // 热缓存不能掩盖被改写的索引校验错误。
        string nextIndex = Path.Combine(scope.Services.ImageSearch.DirectoryPath, "generations", next[0].GenerationId, "backend", "invlists.bin");
        using (var file = new FileStream(nextIndex, FileMode.Open, FileAccess.Write)) file.WriteByte(0);
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Services.ImageSearch.SearchAsync(query, null, copies, CancellationToken.None));
    }

    private sealed class QueryProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    [AvaloniaFact]
    public async Task BatchRepairUsesAllCopiesReusesQueuedTasksAndReportsMissingLinks()
    {
        using var scope = new Fixture(); scope.CreateCopies(named: true);
        string root = scope.Services.Settings.Sources[0].RootPath;
        string bad = Path.Combine(root, "43-缺少链接"); Directory.CreateDirectory(bad);
        File.WriteAllBytes(Path.Combine(bad, "00000003.png"), SyntheticImages.Create(77, 200, 280).EncodePng());
        scope.Services.Works.Upsert(new() { WorkId = "42", Gid = 42, Token = "fixture", Title = "合成测试" });
        using var shell = new ShellViewModel(scope.Services); await shell.Library.InitializeAsync();
        var copies = shell.Library.SnapshotAllWorks().Single(i => i.Gid == 42).Versions;
        var source = scope.Services.Settings.Sources.Single(s => s.Id == copies[0].SourceId);
        var original = scope.Services.Downloads.EnqueueRepair(new() { Gid = 42, Token = "fixture", Pages = 5 }, copies[0].FolderPath, source.RootPath, false);
        original.State = EhViewerForPc.Core.Download.DownloadState.Running;
        await shell.Library.FillMissingPagesCommand.ExecuteAsync(null);
        Assert.Equal(2, scope.Services.Downloads.Tasks.Count);
        Assert.Same(original, scope.Services.Downloads.Tasks.Single(t => t.FolderPath == original.FolderPath));
        Assert.Equal(2, scope.Services.Downloads.Tasks.Select(t => t.Id).Distinct().Count());
        Assert.True(shell.Library.HasRepairFailures);
        Assert.Contains("43-缺少链接", shell.Library.RepairFailureDetails);
        await shell.Library.FillMissingPagesCommand.ExecuteAsync(null);
        Assert.Equal(2, scope.Services.Downloads.Tasks.Count);
    }

    [AvaloniaFact]
    public async Task VersionChecksReadEachOriginalFolderAndDetailsOpensVersionManagement()
    {
        using var scope = new Fixture(); var physical = scope.CreateCopies(named: true);
        scope.Services.Works.Upsert(new() { WorkId = "42", Gid = 42, Token = "fixture", Title = "合成测试", PageCount = 5, Metadata = new() });
        using var shell = new ShellViewModel(scope.Services); await shell.Library.InitializeAsync();
        var item = shell.Library.SnapshotAllWorks().Single();
        File.WriteAllBytes(Path.Combine(physical[0].FolderPath, "00000002.png"), SyntheticImages.Create(44, 200, 280).EncodePng());
        File.WriteAllBytes(Path.Combine(physical[1].FolderPath, "00000004.png"), []);
        using var vm = new VersionsViewModel(item, scope.Services, shell.Library.ApplyScannedCopy);
        await vm.CheckPagesCommand.ExecuteAsync(null);
        var first = vm.Versions.Single(v => v.FolderPath == physical[0].FolderPath);
        var second = vm.Versions.Single(v => v.FolderPath == physical[1].FolderPath);
        Assert.Equal(new[] { 4 }, first.Info.MissingRanges.SelectMany(r => Enumerable.Range(r.Start, r.End - r.Start + 1)));
        Assert.Contains("缺页：4", first.DetectionText);
        Assert.Equal(1, second.Info.ZeroByteFiles); Assert.Contains("缺页：2", second.DetectionText);
        Assert.Contains("0 字节：1", second.DetectionText);
        Assert.Equal(4, item.Versions.Single(v => v.FolderPath == physical[0].FolderPath).PageCount);
        Directory.Move(physical[1].FolderPath, physical[1].FolderPath + "-offline");
        await vm.CheckPagesCommand.ExecuteAsync(null);
        Assert.DoesNotContain("检测失败", first.DetectionText);
        Assert.Contains("检测失败", second.DetectionText);
        StartupOptions.Current = StartupOptions.Parse(["--ui-smoke"]);
        var window = new MainWindow(shell) { Width = 920, Height = 600 };
        try
        {
            window.Show(); shell.Navigate("library");
            shell.Library.OpenDetailsCommand.Execute(item);
            await Task.Delay(100); window.UpdateLayout();
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "LocalDetails.Versions");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100); window.UpdateLayout();
            Assert.IsType<VersionsPage>(window.NavigationFrame.Content);
            Assert.Equal(2, window.GetVisualDescendants().OfType<SelectableTextBlock>().Count(t => AutomationProperties.GetAutomationId(t) == "Versions.Detection"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepairPageUpdatesKeepTheListCoverAndProgressRingAlive(bool secondary)
    {
        using var scope = new Fixture(); scope.CreateCopies(named: true);
        string unrelatedFolder = Path.Combine(scope.Services.Settings.Sources[0].RootPath, "99-unrelated");
        Directory.CreateDirectory(unrelatedFolder);
        File.WriteAllBytes(Path.Combine(unrelatedFolder, "00000001.png"), SyntheticImages.Create(99, 200, 280).EncodePng());
        using var shell = new ShellViewModel(scope.Services); await shell.Library.InitializeAsync();
        var item = shell.Library.SnapshotAllWorks().Single(i => i.Gid == 42);
        var unrelated = shell.Library.SnapshotAllWorks().Single(i => i.Gid == 99);
        var info = secondary ? item.Versions.Single(v => v.FolderPath != item.FolderPath) : item.Info;
        var source = scope.Services.Settings.Sources.Single(s => s.Id == info.SourceId);
        var task = scope.Services.Downloads.EnqueueRepair(new() { Gid = 42, Token = "fixture", Pages = 5 }, info.FolderPath, source.RootPath, false);
        task.State = EhViewerForPc.Core.Download.DownloadState.Running; task.DownloadedPages = 3;
        shell.Library.ApplyDownloadStates([task]);
        var page = new LibraryPage { DataContext = shell.Library };
        var window = new Window { Width = 1000, Height = 700, Content = page };
        try
        {
            window.Show(); await Task.Delay(100); window.UpdateLayout();
            var list = shell.Library.Galleries; var cover = item.CoverRequest; var otherCover = unrelated.CoverRequest;
            var order = list.ToArray();
            var ring = window.GetVisualDescendants().OfType<FAProgressRing>().Single(r => ReferenceEquals(r.DataContext, item));
            int detached = 0, listChanges = 0;
            double lastProgress = ring.Value;
            bool rewound = false;
            var trace = new List<string> { "initial=" + ring.Value };
            ((System.Collections.Specialized.INotifyCollectionChanged)list).CollectionChanged += (_, change) => trace.Add("collection=" + change.Action);
            ring.PropertyChanged += (_, change) =>
            {
                if (change.Property == Control.DataContextProperty) trace.Add("context=" + (ReferenceEquals(ring.DataContext, item) ? "item" : "other"));
                if (change.Property != FAProgressRing.ValueProperty) return;
                trace.Add("value=" + ring.Value);
                rewound |= ring.Value < lastProgress;
                lastProgress = ring.Value;
            };
            int unrelatedChanges = 0;
            unrelated.PropertyChanged += (_, _) => unrelatedChanges++;
            ring.DetachedFromVisualTree += (_, _) => detached++;
            shell.Library.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Galleries)) listChanges++; };
            File.WriteAllBytes(Path.Combine(info.FolderPath, "00000002.png"), SyntheticImages.Create(44, 200, 280).EncodePng());
            task.DownloadedPages = 4;
            await shell.Library.ImportDownloadAsync(task);
            await Task.Delay(100); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(detached == 0 && listChanges == 0, $"page update detached ring={detached}, replaced list={listChanges}");
            Assert.Same(list, shell.Library.Galleries);
            Assert.Equal(order, shell.Library.Galleries);
            Assert.Same(item, shell.Library.Galleries.Single(i => i.Gid == 42));
            if (!secondary) Assert.Same(cover, item.CoverRequest);
            Assert.Same(otherCover, unrelated.CoverRequest); Assert.Equal(0, unrelatedChanges);
            Assert.Same(ring, window.GetVisualDescendants().OfType<FAProgressRing>().Single(r => ReferenceEquals(r.DataContext, item)));
            Assert.Equal(.8, ring.Value, 3);
            Assert.False(rewound, "Ring updates: " + string.Join("; ", trace));
            Assert.Equal(4, item.PageCount);
            string newFolder = Path.Combine(source.RootPath, "100-new-download"); Directory.CreateDirectory(newFolder);
            File.WriteAllBytes(Path.Combine(newFolder, "00000001.png"), SyntheticImages.Create(100, 200, 280).EncodePng());
            var incoming = scope.Services.Downloads.EnqueueRepair(new() { Gid = 100, Token = "fixture", Pages = 2 }, newFolder, source.RootPath, false);
            incoming.State = EhViewerForPc.Core.Download.DownloadState.Running; incoming.DownloadedPages = 1;
            await shell.Library.ImportDownloadAsync(incoming);
            await Task.Delay(100); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(3, shell.Library.Galleries.Count);
            Assert.False(rewound, "Inserting a new download reset another progress ring: " + string.Join("; ", trace));
            Assert.Equal(.8, ring.Value, 3);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectionOnlyChangesHorizontalArrangementAndNeverReversesInputs(bool rightToLeft)
    {
        using var scope = new Fixture(); var gallery = scope.CreateCopies()[0];
        scope.Services.Settings.RightToLeft = rightToLeft;
        using var page = new ReaderPage(new ReaderViewModel(scope.Services, gallery, initialPageNumber: 3));
        var window = new Window { Width = 740, Height = 600, Content = page };
        try
        {
            window.Show();
            foreach (var mode in new[] { ReadingMode.Single, ReadingMode.Continuous, ReadingMode.Double })
            {
                await page.ViewModel.SetModeAsync(mode);
                if (mode == ReadingMode.Double) page.ViewModel.SetDirection(rightToLeft);
                else { page.ViewModel.ToggleDirection(); Assert.Equal(rightToLeft, page.ViewModel.RightToLeft); }
                page.ViewModel.TryNavigateToPageNumber(3);
                await Task.Delay(100); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                page.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left });
                Assert.Equal("2 / 5", page.ViewModel.PageLabel);
                page.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
                Assert.Equal("3 / 5", page.ViewModel.PageLabel);
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                if (mode == ReadingMode.Single)
                {
                    Assert.True(page.SimulateWheelForTest(-1));
                    Assert.Equal("4 / 5", page.ViewModel.PageLabel);
                }
                else
                {
                    var scroll = page.FindControl<ScrollViewer>(mode == ReadingMode.Double ? "HorizontalScroll" : "ContinuousScroll")!;
                    scroll.Offset = mode == ReadingMode.Double ? new Vector(200, 0) : new Vector(0, 200);
                    await Task.Delay(100); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                    double before = mode == ReadingMode.Double ? scroll.Offset.X : scroll.Offset.Y;
                    var point = scroll.TranslatePoint(new Point(100, 100), window)!.Value;
                    window.MouseWheel(point, new Vector(0, -1));
                    Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                    double after = mode == ReadingMode.Double ? scroll.Offset.X : scroll.Offset.Y;
                    Assert.True(after > before, $"{mode}, RTL={rightToLeft}: wheel {before} -> {after}");
                }
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ReopenedSearchPageBindsRealBuildStageCounts()
    {
        using var scope = new Fixture(); var copies = scope.CreateCopies();
        scope.Services.Settings.ImageSearch.Enabled = true;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        Task build = scope.Services.ImageSearch.BuildAsync(copies.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray(),
            new InlineIndexProgress(p =>
            {
                if (p.IndexProgress is not { Stage: IndexBuildStage.Assigning, Total: > 0 } value || value.Completed != value.Total) return;
                reached.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Progress observer release");
            }), CancellationToken.None);
        Window? window = null;
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using var vm = new ImageSearchViewModel(scope.Services, () => copies);
            using var page = new ImageSearchPage(vm);
            window = new Window { Width = 540, Height = 420, Content = page }; window.Show();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var bar = window.GetVisualDescendants().OfType<ProgressBar>().Single(b => AutomationProperties.GetAutomationId(b) == "ImageSearch.Progress");
            Assert.True(bar.IsEffectivelyVisible); Assert.False(bar.IsIndeterminate); Assert.Equal(1, bar.Value);
            Assert.Contains("个特征", vm.StatusText); Assert.Equal(6, vm.ProcessedPages);
        }
        finally { release.Set(); window?.Close(); await build; }
    }

    [AvaloniaFact]
    public async Task BuildReportsItsWorkAfterPageExtractionFinishes()
    {
        using var scope = new Fixture(); var copies = scope.CreateCopies();
        scope.Services.Settings.ImageSearch.Enabled = true;
        var statuses = new List<LocalImageSearchStatus>();
        await scope.Services.ImageSearch.BuildAsync(copies.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray(),
            new InlineIndexProgress(statuses.Add), CancellationToken.None);
        var messages = statuses.Select(p => p.Message).ToArray();
        Assert.Contains(messages, message => message.StartsWith("正在分配图片特征", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.StartsWith("正在合并图片索引", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.StartsWith("正在校验图片索引", StringComparison.Ordinal));
        Assert.Contains(statuses, p => p.IndexProgress is { Stage: IndexBuildStage.Assigning, Total: > 0 });
        Assert.Contains(statuses, p => p.IndexProgress is { Stage: IndexBuildStage.ValidatingIndex, Total: > 0 });
        Assert.All(statuses.Where(p => p.IndexProgress is not null), p => Assert.Equal(6, p.Processed));
    }

    [AvaloniaFact]
    public async Task CpuAndAutomaticGpuPublishIdenticalIndexesAndReleaseMemory()
    {
        using var scope = new Fixture(); var copies = scope.CreateCopies();
        scope.Services.Settings.ImageSearch.Enabled = true;
        var galleries = copies.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
        await scope.Services.ImageSearch.BuildAsync(galleries, null, CancellationToken.None, options: new IndexBuildOptions { UseGpu = false });
        byte[][] before = IndexFiles();
        long memory = ImageMemoryBudget.UsedBytes;
        await scope.Services.ImageSearch.BuildAsync(galleries, null, CancellationToken.None, rebuild: true);
        var after = IndexFiles();
        Assert.Equal(before[0], after[0]); Assert.Equal(before[1], after[1]);
        Assert.Equal(memory, ImageMemoryBudget.UsedBytes);
        var hits = await scope.Services.ImageSearch.SearchAsync(File.ReadAllBytes(Path.Combine(copies[0].FolderPath, "00000001.png")), null, copies, CancellationToken.None);
        Assert.NotEmpty(hits);
        byte[][] IndexFiles()
        {
            string generation = Assert.Single(Directory.GetDirectories(Path.Combine(scope.Services.ImageSearch.DirectoryPath, "generations")));
            return new[] { "quantizer.bin", "invlists.bin" }.Select(name => File.ReadAllBytes(Path.Combine(generation, "backend", name))).ToArray();
        }
    }

    [AvaloniaFact]
    public async Task RecoveryReportsCommittedPagesBeforeStartingOnlyTheRemainingQueue()
    {
        using var scope = new Fixture(); var copies = scope.CreateCopies();
        foreach (int page in new[] { 2, 4, 6, 7, 8 })
            File.WriteAllBytes(Path.Combine(copies[0].FolderPath, $"{page:D8}.png"), SyntheticImages.Create(42 + page, 200, 280).EncodePng());
        scope.Services.Settings.ImageSearch.Enabled = true;
        var snapshot = copies.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
        using var interrupted = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Services.ImageSearch.BuildAsync(snapshot,
            new InlineIndexProgress(p => { if (p.Processed == 10) interrupted.Cancel(); }), interrupted.Token));
        using var canceled = new CancellationTokenSource();
        LocalImageSearchStatus? first = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Services.ImageSearch.BuildAsync(snapshot,
            new InlineIndexProgress(p =>
            {
                if (!p.Message.StartsWith("正在提取页图…", StringComparison.Ordinal)) return;
                Assert.Equal(0, scope.Services.ImageSearch.PlanStatistics!.EnumeratedGalleries);
                first = p; canceled.Cancel();
            }), canceled.Token));
        Assert.NotNull(first);
        Assert.Equal(10, first.Processed);
        Assert.Equal(10, first.Reused);
    }

    [AvaloniaFact]
    public async Task ResumeRetriesFailedPagesEvenWhenTheirMetadataDidNotChange()
    {
        using var scope = new Fixture(); var copies = scope.CreateCopies();
        foreach (int page in new[] { 2, 4, 6, 7, 8 })
            File.WriteAllBytes(Path.Combine(copies[0].FolderPath, $"{page:D8}.png"), SyntheticImages.Create(42 + page, 200, 280).EncodePng());
        scope.Services.Settings.ImageSearch.Enabled = true;
        string path = Path.Combine(copies[0].FolderPath, "00000001.png");
        byte[] valid = File.ReadAllBytes(path);
        DateTime mtime = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, new byte[valid.Length]);
        File.SetLastWriteTimeUtc(path, mtime);
        var galleries = copies.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
        using var interrupted = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Services.ImageSearch.BuildAsync(galleries,
            new InlineIndexProgress(p => { if (p.Processed == 10) interrupted.Cancel(); }), interrupted.Token));
        File.WriteAllBytes(path, valid);
        File.SetLastWriteTimeUtc(path, mtime);
        var result = await scope.Services.ImageSearch.BuildAsync(galleries, null, CancellationToken.None);
        Assert.Equal(11, result.Indexed);
        Assert.Equal(0, result.Failed);
    }

    [AvaloniaFact]
    public async Task ManualContinueReconcilesGalleryMembershipWithoutScanningRetainedGalleries()
    {
        using var scope = new Fixture(); var copies = scope.CreateCopies();
        scope.Services.Settings.ImageSearch.Enabled = true;
        var galleries = copies.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
        using var interrupted = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Services.ImageSearch.BuildAsync(galleries,
            new InlineIndexProgress(p => { if (p.Message == "正在训练图片索引…") interrupted.Cancel(); }), interrupted.Token));
        var result = await scope.Services.ImageSearch.BuildAsync(galleries.Take(1).ToArray(), new InlineIndexProgress(p =>
        {
            if (p.Message == "正在训练图片索引…") Assert.Equal(0, scope.Services.ImageSearch.PlanStatistics!.EnumeratedGalleries);
        }), CancellationToken.None);
        Assert.Equal(3, result.Total);
        Assert.Equal(3, result.Indexed);
    }

    [AvaloniaFact]
    public async Task EmptyPlanCanContinueAfterPagesBecomeAvailable()
    {
        using var scope = new Fixture(); var copies = scope.CreateCopies();
        scope.Services.Settings.ImageSearch.Enabled = true;
        var gallery = copies[0];
        foreach (string file in Directory.GetFiles(gallery.FolderPath)) File.Delete(file);
        var snapshot = new[] { new ImageSearchGallery(gallery.SourceId, gallery.StableId, gallery.WorkId, gallery.FolderPath) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None));
        File.WriteAllBytes(Path.Combine(gallery.FolderPath, "00000001.png"), SyntheticImages.Create(42, 200, 280).EncodePng());
        var result = await scope.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None);
        Assert.Equal(1, result.Indexed);
        Assert.False(scope.Services.ImageSearch.IsBuilding);
    }

    [AvaloniaFact]
    public async Task TrainingResumeDoesNotReturnToPageExtraction()
    {
        using var scope = new Fixture(); var copies = scope.CreateCopies();
        scope.Services.Settings.ImageSearch.Enabled = true;
        var snapshot = copies.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
        using var interrupted = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Services.ImageSearch.BuildAsync(snapshot,
            new InlineIndexProgress(p => { if (p.Message == "正在训练图片索引…") interrupted.Cancel(); }), interrupted.Token));
        bool extracted = false;
        var result = await scope.Services.ImageSearch.BuildAsync(snapshot, new InlineIndexProgress(p =>
            extracted |= p.Message.StartsWith("正在提取页图", StringComparison.Ordinal)), CancellationToken.None);
        Assert.False(extracted);
        Assert.Equal(6, result.Indexed);
    }

    [AvaloniaFact]
    public async Task InterruptedSourceVerificationContinuesAfterItsCommittedGalleryChunk()
    {
        using var scope = new Fixture(); var copies = scope.CreateCopies();
        scope.Services.Settings.ImageSearch.Enabled = true;
        var first = copies[0];
        var galleries = Enumerable.Range(0, 35).Select(i => new ImageSearchGallery(first.SourceId, "chunk-" + i, "work-" + i, first.FolderPath)).ToArray();
        using var interrupted = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Services.ImageSearch.BuildAsync(galleries,
            new InlineIndexProgress(p => { if (p.Message == "正在核对源目录变更 16 / 35") interrupted.Cancel(); }), interrupted.Token));
        Assert.Equal(ImageIndexBuildPhase.CheckingSources, ReadCheckpoint(scope).Phase);
        var result = await scope.Services.ImageSearch.BuildAsync(galleries, new InlineIndexProgress(p =>
        {
            Assert.False(p.Message.StartsWith("正在提取页图", StringComparison.Ordinal));
            Assert.NotEqual("正在训练图片索引…", p.Message);
            Assert.NotEqual("正在构建图片索引…", p.Message);
        }), CancellationToken.None);
        Assert.Equal(19, scope.Services.ImageSearch.PlanStatistics!.EnumeratedGalleries);
        Assert.Equal(105, result.Indexed);
    }

    private static ImageIndexBuildCheckpoint ReadCheckpoint(Fixture scope) => JsonFileStore.LoadOrDefault(
        Path.Combine(scope.Services.ImageSearch.DirectoryPath, "build-job.json"), StorageJsonContext.Default.ImageIndexBuildCheckpoint,
        () => new ImageIndexBuildCheckpoint(), out _);

    [AvaloniaFact]
    public async Task BuildingResumeKeepsItsQuantizerAndStartsBeforeSourceVerification()
    {
        using var scope = new Fixture(); var copies = scope.CreateCopies();
        scope.Services.Settings.ImageSearch.Enabled = true;
        var galleries = copies.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
        using var interrupted = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Services.ImageSearch.BuildAsync(galleries,
            new InlineIndexProgress(p => { if (p.Message == "正在构建图片索引…") interrupted.Cancel(); }), interrupted.Token));
        string root = scope.Services.ImageSearch.DirectoryPath;
        string generation = Assert.Single(Directory.GetDirectories(Path.Combine(root, "generations")));
        byte[] quantizer = File.ReadAllBytes(Path.Combine(generation, "backend", "quantizer.bin"));
        bool built = false, verified = false;
        var result = await scope.Services.ImageSearch.BuildAsync(galleries, new InlineIndexProgress(p =>
        {
            Assert.False(p.Message.StartsWith("正在提取页图", StringComparison.Ordinal));
            Assert.NotEqual("正在训练图片索引…", p.Message);
            if (p.Message == "正在构建图片索引…")
            {
                Assert.False(verified);
                Assert.Equal(0, scope.Services.ImageSearch.PlanStatistics!.EnumeratedGalleries);
                built = true;
            }
            verified |= p.Message == "正在核对源目录变更…";
        }), CancellationToken.None);
        Assert.True(built); Assert.True(verified);
        Assert.Equal(ImageIndexBuildPhase.Building, scope.Services.ImageSearch.PlanStatistics!.ResumedPhase);
        Assert.Equal(quantizer, File.ReadAllBytes(Path.Combine(generation, "backend", "quantizer.bin")));
        Assert.Equal(6, result.Indexed);
    }

    [AvaloniaFact]
    public async Task ParallelBuildReportsCorruptPageAndCanContinueWithRemainingPages()
    {
        using var scope = new Fixture(); var copies = scope.CreateCopies();
        scope.Services.Settings.ImageSearch.Enabled = true;
        File.WriteAllBytes(Path.Combine(copies[0].FolderPath, "00000003.png"), [1, 2, 3]);
        var snapshot = copies.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
        var result = await scope.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None,
            options: new IndexBuildOptions { Workers = 4 });
        Assert.Equal(1, result.Failed); Assert.Equal(5, result.Indexed);
        File.WriteAllBytes(Path.Combine(copies[0].FolderPath, "00000003.png"), SyntheticImages.Create(45, 200, 280).EncodePng());
        var retry = await scope.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None,
            options: new IndexBuildOptions { Workers = 2 });
        Assert.Equal(0, retry.Failed); Assert.Equal(6, retry.Indexed);
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(12)]
    public async Task ParallelBatchesRemainSearchableAndUnchangedUpdatesKeepPublishedSnapshot(int workers)
    {
        using var scope = new Fixture(); var copies = scope.CreateCopies();
        scope.Services.Settings.ImageSearch.Enabled = true;
        var snapshot = copies.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
        long before = ImageMemoryBudget.UsedBytes;
        var built = await scope.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None,
            options: new IndexBuildOptions { Workers = workers, BatchSize = 5, WorkingSetBytes = 64L * 1024 * 1024, UseGpu = false });
        Assert.Equal(6, built.Indexed);
        var statistics = scope.Services.ImageSearch.BuildStatistics!;
        Assert.Equal(workers, statistics.Workers);
        Assert.InRange(statistics.PeakWorkspaceBytes, 1, statistics.WorkingSetLimit);
        Assert.Equal(2, statistics.CommittedBatches);
        Assert.Equal(before, ImageMemoryBudget.UsedBytes);
        string pointer = Path.Combine(scope.Services.ImageSearch.DirectoryPath, "current.json");
        byte[] published = File.ReadAllBytes(pointer);
        var updated = await scope.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None);
        Assert.Equal(6, updated.Reused);
        Assert.Equal(published, File.ReadAllBytes(pointer));
        Assert.True(scope.Services.ImageSearch.BuildStatistics!.Unchanged);
        var hits = await scope.Services.ImageSearch.SearchAsync(File.ReadAllBytes(Path.Combine(copies[0].FolderPath, "00000001.png")), null, copies, CancellationToken.None);
        Assert.Contains(hits, h => h.Gallery.SourceId == copies[1].SourceId && h.PageNumber == 1);
    }

    [AvaloniaFact]
    public async Task ShutdownCancelsAnIndexWaitingForSharedImageMemory()
    {
        using var scope = new Fixture(); var copies = scope.CreateCopies();
        scope.Services.Settings.ImageSearch.Enabled = true;
        long before = ImageMemoryBudget.UsedBytes;
        Assert.True(ImageMemoryBudget.TryReserveIndex(ImageMemoryBudget.IndexLimitBytes, out var occupied));
        using var reservation = occupied;
        var snapshot = copies.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
        Task build = scope.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None);
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (scope.Services.ImageSearch.BuildStatus?.Message != "正在等待可用图像内存…" && DateTime.UtcNow < timeout) await Task.Delay(10);
        Assert.Equal("正在等待可用图像内存…", scope.Services.ImageSearch.BuildStatus?.Message);
        scope.Services.ImageSearch.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);
        Assert.Equal(before + ImageMemoryBudget.IndexLimitBytes, ImageMemoryBudget.UsedBytes);
        occupied!.Dispose();
        Assert.Equal(before, ImageMemoryBudget.UsedBytes);
    }

    [AvaloniaFact]
    public async Task IndexBuildKeepsReaderMemoryAvailableWhenBothBudgetsAreOccupied()
    {
        using var scope = new Fixture(); var copies = scope.CreateCopies();
        scope.Services.Settings.ImageSearch.Enabled = true;
        long before = ImageMemoryBudget.UsedBytes;
        // 占满前台显示额度，后台提取仍应正常完成；反向占满索引额度也不妨碍阅读器分配。
        using (ImageMemoryBudget.Reserve(ImageMemoryBudget.LimitBytes - before))
        {
            var snapshot = copies.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
            var status = await scope.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None,
                options: new IndexBuildOptions { Workers = 12, WorkingSetBytes = 256L * 1024 * 1024 }).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(6, status.Indexed);
            Assert.Equal(0, ImageMemoryBudget.IndexUsedBytes);
        }
        Assert.True(ImageMemoryBudget.TryReserveIndex(ImageMemoryBudget.IndexLimitBytes, out var index));
        using (index)
        using (ImageMemoryBudget.Reserve(1024 * 1024))
        {
            Assert.False(ImageMemoryBudget.TryReserveIndex(1, out _));
            Assert.Equal(before + ImageMemoryBudget.IndexLimitBytes + 1024 * 1024, ImageMemoryBudget.UsedBytes);
        }
        Assert.Equal(before, ImageMemoryBudget.UsedBytes);
    }

    [AvaloniaFact]
    public async Task ServiceDisposalDrainsStatusAndSearchFileHandlesBeforeReturning()
    {
        using var scope = new Fixture(); var copies = scope.CreateCopies();
        scope.Services.Settings.ImageSearch.Enabled = true;
        var snapshot = copies.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
        await scope.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None);
        string root = scope.Services.ImageSearch.DirectoryPath;
        byte[] image = File.ReadAllBytes(Path.Combine(copies[0].FolderPath, "00000001.png"));
        var reads = Enumerable.Range(0, 24).Select(_ => (Task)scope.Services.ImageSearch.GetStatusAsync()).ToList();
        reads.AddRange(Enumerable.Range(0, 3).Select(_ => (Task)scope.Services.ImageSearch.SearchAsync(image, null, copies, CancellationToken.None)));
        scope.Services.ImageSearch.Dispose();
        // 不等待调用方 continuation，文件句柄此时就必须全部释放。
        Directory.Move(root, root + "-retired");
        foreach (var read in reads)
        {
            try { await read; }
            catch (OperationCanceledException) { }
        }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => scope.Services.ImageSearch.SearchAsync(image, null, copies, CancellationToken.None));
    }

    [AvaloniaFact]
    public async Task FailedTrainingRetainsContinueActionAndRetriesChangedPages()
    {
        using var scope = new Fixture(); var copies = scope.CreateCopies();
        byte[] blank = new GrayImage(200, 280, new byte[200 * 280]).EncodePng();
        foreach (var copy in copies)
        foreach (string file in Directory.GetFiles(copy.FolderPath, "*.png")) File.WriteAllBytes(file, blank);
        scope.Services.Settings.ImageSearch.Enabled = true;
        using var vm = new ImageSearchViewModel(scope.Services, () => copies);
        await vm.BuildIndexCommand.ExecuteAsync(null);
        Assert.True(vm.HasPendingBuild);
        Assert.Equal("继续建立索引", vm.IndexActionLabel);
        Assert.False(vm.IsBuilding);
        Assert.Contains("有效特征不足", vm.StatusText);
        File.WriteAllBytes(Path.Combine(copies[0].FolderPath, "00000001.png"), SyntheticImages.Create(43, 200, 280).EncodePng());
        await vm.BuildIndexCommand.ExecuteAsync(null);
        Assert.True(vm.HasIndex); Assert.Equal(1, vm.IndexedPages);
        Assert.False(vm.HasPendingBuild);
    }

    [AvaloniaFact]
    public async Task IncrementalDeletionPrunesSharedDescriptorsAndRestoredPagesBecomeSearchable()
    {
        using var scope = new Fixture(); var copies = scope.CreateCopies();
        scope.Services.Settings.ImageSearch.Enabled = true;
        var snapshot = copies.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
        await scope.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None);
        byte[] removed = File.ReadAllBytes(Path.Combine(copies[0].FolderPath, "00000005.png"));
        foreach (var copy in copies) File.Delete(Path.Combine(copy.FolderPath, "00000005.png"));
        Assert.Equal(4, (await scope.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None)).Indexed);
        string root = scope.Services.ImageSearch.DirectoryPath;
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "features.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            database.Open(); using var command = database.CreateCommand(); command.CommandText = "SELECT count(*) FROM feature";
            Assert.Equal(2L, command.ExecuteScalar());
        }
        File.WriteAllBytes(Path.Combine(copies[0].FolderPath, "00000005.png"), removed);
        Assert.Equal(5, (await scope.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None)).Indexed);
        var hits = await scope.Services.ImageSearch.SearchAsync(removed, null, copies, CancellationToken.None);
        Assert.Contains(hits, hit => hit.PageNumber == 5 && hit.Gallery.SourceId == copies[0].SourceId);
        Assert.Single(Directory.GetDirectories(Path.Combine(root, "generations")));
    }

    [AvaloniaFact]
    public async Task InterruptedBuildResumesAfterServiceRestartWithoutRepeatingCompletedPages()
    {
        using var scope = new Fixture();
        var copies = scope.CreateCopies();
        foreach (int page in new[] { 2, 4, 6, 7, 8 }) File.WriteAllBytes(Path.Combine(copies[0].FolderPath, $"{page:D8}.png"), SyntheticImages.Create(42 + page, 200, 280).EncodePng());
        scope.Services.Settings.ImageSearch.Enabled = true;
        scope.Services.TrySaveSettings(out _);
        var snapshot = copies.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
        using var interrupted = new CancellationTokenSource();
        Task build = scope.Services.ImageSearch.BuildAsync(snapshot,
            new InlineIndexProgress(p => { if (p.Processed == 10) interrupted.Cancel(); }), interrupted.Token);
        async Task ReadWhileBuilding()
        {
            while (!build.IsCompleted)
            {
                await scope.Services.ImageSearch.GetStatusAsync();
                await Task.Delay(1);
            }
        }
        Task[] readers = [ReadWhileBuilding(), ReadWhileBuilding()];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);
        await Task.WhenAll(readers);
        string root = scope.Services.ImageSearch.DirectoryPath;
        Assert.Empty(Directory.GetFiles(root, "*.corrupt-*"));
        var checkpoint = JsonFileStore.LoadOrDefault(Path.Combine(root, "build-job.json"), StorageJsonContext.Default.ImageIndexBuildCheckpoint,
            () => new ImageIndexBuildCheckpoint(), out _);
        Assert.True(checkpoint.ResumeOnStartup);
        Assert.Equal(11, checkpoint.TotalPages);
        var paths = scope.Services.Paths;
        scope.Services.Dispose();
        using var restarted = AppServices.Initialize(paths);
        Assert.True((await restarted.ImageSearch.GetStatusAsync()).CanResume);
        Assert.True(await restarted.ImageSearch.ResumePendingBuildAsync());
        Assert.True(restarted.ImageSearch.BuildStatus!.Reused >= 10);
        Assert.Equal(11, (await restarted.ImageSearch.GetStatusAsync()).Indexed);
        Assert.False(File.Exists(Path.Combine(root, "build-job.json")));
        Assert.Single(Directory.GetDirectories(Path.Combine(root, "generations")));
    }

    [AvaloniaFact]
    public async Task ExplicitPauseSurvivesRestartAndRequiresManualContinue()
    {
        using var scope = new Fixture(); var copies = scope.CreateCopies();
        scope.Services.Settings.ImageSearch.Enabled = true; scope.Services.TrySaveSettings(out _);
        var snapshot = copies.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Services.ImageSearch.BuildAsync(snapshot,
            new InlineIndexProgress(p => { if (p.Processed == 6) scope.Services.ImageSearch.Cancel(); }), CancellationToken.None));
        var paths = scope.Services.Paths; scope.Services.Dispose();
        using var restarted = AppServices.Initialize(paths);
        Assert.False(await restarted.ImageSearch.ResumePendingBuildAsync());
        Assert.True((await restarted.ImageSearch.GetStatusAsync()).CanResume);
        string checkpoint = Path.Combine(restarted.ImageSearch.DirectoryPath, "build-job.json");
        byte[] paused = File.ReadAllBytes(checkpoint);
        Assert.True((await restarted.ImageSearch.UpdateAutomaticallyAsync(snapshot, CancellationToken.None)).CanResume);
        Assert.Equal(paused, File.ReadAllBytes(checkpoint));
        var result = await restarted.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None);
        Assert.Equal(6, result.Indexed); Assert.Equal(6, result.Reused);
    }

    [AvaloniaFact]
    public async Task LegacyInterruptedDirectoriesAreAdoptedAndDuplicateFeatureBlobsBecomeOneStore()
    {
        using var scope = new Fixture(); var copies = scope.CreateCopies();
        scope.Services.Settings.ImageSearch.Enabled = true;
        string root = scope.Services.ImageSearch.DirectoryPath;
        long beforeBytes = 0;
        for (int copy = 0; copy < 2; copy++)
        {
            string generation = Path.Combine(root, "generations", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(generation);
            File.WriteAllText(Path.Combine(generation, ".ehreader-image-search-generation"), "ehreader-image-search/1");
            using var database = new ImageDatabase(Path.Combine(generation, "backend"), writable: true);
            using var manifest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(generation, "manifest.db"), Pooling = false }.ToString());
            manifest.Open();
            using var command = manifest.CreateCommand();
            command.CommandText = "CREATE TABLE member(alias TEXT,source_id TEXT,stable_id TEXT,page INTEGER,path TEXT,length INTEGER,mtime INTEGER,state TEXT,PRIMARY KEY(source_id,stable_id,page))";
            command.ExecuteNonQuery();
            foreach (int page in copy == 0 ? new[] { 1, 3, 5 } : new[] { 1, 3 })
            {
                string file = Path.Combine(copies[copy].FolderPath, $"{page:D8}.png");
                var image = GrayImage.ReadFile(file); byte[] canonical = image.EncodePng();
                var descriptors = new OrbExtractor(database.Profile).Extract(image).Select(f => f.Descriptor).ToArray();
                string alias = "pages/legacy-" + page + ".png";
                database.Add(alias, Blake3.Hash(canonical), descriptors); beforeBytes += descriptors.Length * 32L;
                var info = new FileInfo(file);
                command.CommandText = "INSERT INTO member VALUES($alias,$source,$stable,$page,$path,$length,$mtime,'indexed')";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$alias", alias); command.Parameters.AddWithValue("$source", copies[copy].SourceId);
                command.Parameters.AddWithValue("$stable", copies[copy].StableId); command.Parameters.AddWithValue("$page", page);
                command.Parameters.AddWithValue("$path", Path.GetRelativePath(scope.Services.Settings.Sources[copy].RootPath, file));
                command.Parameters.AddWithValue("$length", info.Length); command.Parameters.AddWithValue("$mtime", info.LastWriteTimeUtc.Ticks);
                command.ExecuteNonQuery();
            }
        }
        var snapshot = copies.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
        Assert.True(await scope.Services.ImageSearch.ResumePendingBuildAsync(snapshot));
        Assert.Equal(6, (await scope.Services.ImageSearch.GetStatusAsync()).Indexed);
        string current = Assert.Single(Directory.GetDirectories(Path.Combine(root, "generations")));
        using (var metadata = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(current, "backend", "images.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            metadata.Open(); using var command = metadata.CreateCommand(); command.CommandText = "SELECT count(*) FROM vector";
            Assert.Equal(0L, command.ExecuteScalar());
        }
        using (var shared = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "features.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            shared.Open(); using var command = shared.CreateCommand(); command.CommandText = "SELECT count(*) FROM feature";
            Assert.Equal(3L, command.ExecuteScalar());
            command.CommandText = "SELECT sum(length(descriptors)) FROM feature";
            Assert.True((long)command.ExecuteScalar()! < beforeBytes);
        }
        var hits = await scope.Services.ImageSearch.SearchAsync(File.ReadAllBytes(Path.Combine(copies[0].FolderPath, "00000005.png")), null, copies, CancellationToken.None);
        Assert.Contains(hits, h => h.Gallery.SourceId == copies[1].SourceId && h.PageNumber == 5);
    }

    private sealed class InlineIndexProgress(Action<LocalImageSearchStatus> report) : IProgress<LocalImageSearchStatus>
    {
        public void Report(LocalImageSearchStatus value) => report(value);
    }

    [AvaloniaTheory]
    [InlineData(1200, false)]
    [InlineData(740, false)]
    [InlineData(540, false)]
    [InlineData(1200, true)]
    [InlineData(740, true)]
    [InlineData(540, true)]
    public async Task CardsHaveIndependentRatingsOpaqueLocalBadgesAndReadableReadButtons(int width, bool light)
    {
        using var scope = new Fixture();
        scope.CreateCopies(named: true);
        using var shell = new ShellViewModel(scope.Services);
        Application.Current!.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        StartupOptions.Current = StartupOptions.Parse(["--ui-smoke"]);
        await shell.Library.InitializeAsync();
        await shell.Online.LoadAsync(reset: true);
        var window = new MainWindow(shell) { Width = width, Height = 780 };
        string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.qa/cards-navigation-20261113"));
        Directory.CreateDirectory(output);
        try
        {
            window.Show();
            var list = new EhViewerForPc.Core.Online.EhGalleryListPage();
            list.Items.Add(new() { Gid = 42, Token = "fixture", Title = "合成测试 · 长标题在两行内显示 · 本地副本", Rating = 4.5, Pages = 80, Posted = "2026-11-13 10:04" });
            shell.Online.ApplyPage(list, "https://e-hentai.org/", reset: true);
            await Task.Delay(120); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var rating = window.GetVisualDescendants().OfType<TextBlock>().Single(t => AutomationProperties.GetAutomationId(t) == "Online.Rating");
            Assert.Equal("评分 4.50", rating.Text);
            var badge = window.GetVisualDescendants().OfType<Border>().Single(b => AutomationProperties.GetAutomationId(b) == "Online.LocalBadge");
            var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(Assert.IsType<TextBlock>(badge.Child).Foreground).Color;
            var background = Assert.IsAssignableFrom<ISolidColorBrush>(badge.Background).Color;
            Assert.Equal(255, background.A);
            Assert.True(Contrast(foreground, background) >= 4.5);
            Assert.True(badge.TranslatePoint(default, window)!.Value.Y >= rating.TranslatePoint(default, window)!.Value.Y - 4);
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, $"online-{width}-{light}.png"), PngBitmapEncoderOptions.Default);
            shell.Navigate("library");
            await Task.Delay(120); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var read = window.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetAutomationId(b) == "Library.Read");
            Assert.True(read.IsEnabled);
            var readBg = Assert.IsAssignableFrom<ISolidColorBrush>(read.Background).Color;
            var readFg = Assert.IsAssignableFrom<ISolidColorBrush>(read.Foreground).Color;
            var surface = read.GetVisualAncestors().OfType<Border>().First(b => AutomationProperties.GetAutomationId(b) == "Library.ReadSurface");
            var opaque = Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background).Color;
            Assert.Equal(255, opaque.A);
            double alpha = readBg.A / 255d;
            var rendered = Color.FromRgb((byte)(readBg.R * alpha + opaque.R * (1 - alpha)), (byte)(readBg.G * alpha + opaque.G * (1 - alpha)), (byte)(readBg.B * alpha + opaque.B * (1 - alpha)));
            Assert.True(Contrast(readFg, rendered) >= 4.5, $"Read contrast {Contrast(readFg, rendered):F2}: {readFg} / {rendered}");
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, $"library-{width}-{light}.png"), PngBitmapEncoderOptions.Default);
        }
        finally { window.Close(); }
    }

    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte v) { double x = v / 255d; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }
        static double Luminance(Color c) => .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    [AvaloniaFact]
    public async Task ClipboardPasteAndPointerCropUseTheHeadlessPlatformOnly()
    {
        using var scope = new Fixture(); using var vm = new ImageSearchViewModel(scope.Services, () => []); using var page = new ImageSearchPage(vm);
        var window = new Window { Width = 1200, Height = 800, Content = page };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            using var bitmap = new Bitmap(new MemoryStream(SyntheticImages.Create(42, 200, 280).EncodePng()));
            await window.Clipboard!.SetBitmapAsync(bitmap);
            var paste = page.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetAutomationId(b) == "ImageSearch.Paste");
            paste.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => vm.HasInput);
            Assert.Equal("剪贴板图片", vm.InputName); Assert.Equal(200, vm.ImageWidth); Assert.Equal(280, vm.ImageHeight);
            var surface = page.FindControl<Grid>("PreviewSurface")!;
            Point origin = surface.TranslatePoint(default, window)!.Value;
            double left = origin.X + (surface.Bounds.Width - 200) / 2;
            window.MouseDown(new Point(left + 25, origin.Y + 35), MouseButton.Left);
            window.MouseMove(new Point(left + 125, origin.Y + 175));
            window.MouseUp(new Point(left + 125, origin.Y + 175), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.UseCrop); Assert.Equal(25, vm.CropX); Assert.Equal(35, vm.CropY);
            Assert.Equal(100, vm.CropWidth); Assert.Equal(140, vm.CropHeight);
            var rectangle = page.FindControl<Border>("CropRectangle")!;
            Assert.True(rectangle.IsVisible); Assert.Equal(100, rectangle.Width, precision: 0); Assert.Equal(140, rectangle.Height, precision: 0);
            vm.ResetCropCommand.Execute(null); Assert.False(rectangle.IsVisible);
        }
        finally { window.Close(); }
    }
    [AvaloniaTheory]
    [InlineData(ReadingMode.Single)]
    [InlineData(ReadingMode.Continuous)]
    [InlineData(ReadingMode.Double)]
    public async Task FrameReadsExactHitReusesReaderSwitchesCopyAndReturnsToResults(ReadingMode mode)
    {
        using var scope = new Fixture(); scope.CreateCopies(named: true); scope.Services.Settings.ImageSearch.Enabled = true;
        scope.Services.Settings.DefaultReadingMode = mode;
        StartupOptions.Current = StartupOptions.Parse(["--ui-smoke"]);
        using var shell = new ShellViewModel(scope.Services);
        await shell.Library.InitializeAsync();
        shell.Navigate("library");
        var window = new MainWindow(shell);
        try
        {
            window.Show(); window.OpenImageSearch(); Dispatcher.UIThread.RunJobs();
            var page = Assert.IsType<ImageSearchPage>(window.NavigationFrame.Content);
            var vm = Assert.IsType<ImageSearchViewModel>(page.DataContext);
            await vm.BuildIndexCommand.ExecuteAsync(null); Assert.True(vm.HasIndex, vm.StatusText);
            var copies = shell.Library.SnapshotAllWorks().SelectMany(item => item.Versions).ToArray();
            Assert.Equal(2, copies.Length);
            await vm.LoadFileAsync(Path.Combine(copies[0].FolderPath, "00000005.png")); await vm.SearchCommand.ExecuteAsync(null);
            var first = vm.Results.First(r => r.Hit.Gallery.SourceId == copies[0].SourceId && r.Hit.PageNumber == 5);
            vm.ReadHitCommand.Execute(first);
            await WaitForAsync(() => window.ActiveReader?.ViewModel.Source.StableId == first.Hit.Gallery.StableId);
            var reader = Assert.IsType<ReaderPage>(window.ActiveReader);
            await Task.Delay(150); Dispatcher.UIThread.RunJobs();
            Assert.Equal("5 / 5", reader.ViewModel.PageLabel);
            window.NavigationFrame.GoBack(); Assert.Same(page, window.NavigationFrame.Content);
            Assert.True(vm.HasResults);
            vm.ReadHitCommand.Execute(first);
            Assert.Same(reader, window.ActiveReader);
            window.NavigationFrame.GoBack();
            var other = vm.Results.First(r => r.Hit.Gallery.SourceId == copies[1].SourceId && r.Hit.PageNumber == 5);
            vm.ReadHitCommand.Execute(other);
            await WaitForAsync(() => window.ActiveReader?.ViewModel.Source.StableId == other.Hit.Gallery.StableId);
            Assert.NotSame(reader, window.ActiveReader);
            Assert.Equal(other.Hit.Gallery.SourceId, Assert.IsType<LocalReaderSource>(window.ActiveReader!.ViewModel.Source).Gallery.SourceId);
            Assert.Empty(scope.Services.Settings.PreferredSourceByWork);
            await Task.Delay(150); Dispatcher.UIThread.RunJobs();
            Assert.Equal("5 / 5", window.ActiveReader.ViewModel.PageLabel);
            window.NavigationFrame.GoBack(); Assert.Same(page, window.NavigationFrame.Content);
            Assert.True(vm.HasResults);
        }
        finally { window.Close(); }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        Assert.True(condition(), "The headless navigation did not finish.");
    }
    [AvaloniaFact]
    public async Task DisabledFeatureAndDraftSettingsNeverCreateAnIndex()
    {
        using var scope = new Fixture();
        Assert.False(scope.Services.Settings.ImageSearch.Enabled);
        var state = await scope.Services.ImageSearch.GetStatusAsync();
        Assert.Contains("尚未启用", state.Message);
        Assert.False(Directory.Exists(scope.Services.ImageSearch.DirectoryPath));
        using var settings = new SettingsViewModel(scope.Services);
        settings.ImageSearchEnabled = true; settings.ImageSearchProbeIndex = 2;
        Assert.False(scope.Services.Settings.ImageSearch.Enabled);
        Assert.Equal(16, scope.Services.Settings.ImageSearch.NProbe);
        settings.Apply();
        Assert.True(scope.Services.Settings.ImageSearch.Enabled); Assert.Equal(64, scope.Services.Settings.ImageSearch.NProbe);
        Assert.False(Directory.Exists(scope.Services.ImageSearch.DirectoryPath));
    }

    [AvaloniaFact]
    public async Task RealServiceBuildMapsDuplicateCopiesAndFiltersDisabledOrChangedSources()
    {
        using var scope = new Fixture();
        GalleryInfo[] copies = scope.CreateCopies(); scope.Services.Settings.ImageSearch.Enabled = true;
        var snapshot = copies.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
        var result = await scope.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None);
        Assert.Equal(6, result.Indexed);
        using (var lockedPage = new FileStream(Path.Combine(copies[0].FolderPath, "00000001.png"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var update = await scope.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None);
            Assert.Equal(6, update.Indexed); Assert.Equal(6, update.Reused);
        }
        await scope.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None);
        Assert.Single(Directory.GetDirectories(Path.Combine(scope.Services.ImageSearch.DirectoryPath, "generations")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Services.ImageSearch.BuildAsync([], null, CancellationToken.None));
        Assert.Equal(6, (await scope.Services.ImageSearch.GetStatusAsync()).Indexed);
        await File.WriteAllTextAsync(Path.Combine(scope.Services.ImageSearch.DirectoryPath, "current.json"), "{broken");
        Assert.Equal(6, (await scope.Services.ImageSearch.GetStatusAsync()).Indexed); // 指针备份复用当前数据，不保留整份旧索引。
        byte[] query = File.ReadAllBytes(Path.Combine(copies[0].FolderPath, "00000005.png"));
        var hits = await scope.Services.ImageSearch.SearchAsync(query, null, copies, CancellationToken.None);
        Assert.Contains(hits, h => h.Gallery.SourceId == copies[0].SourceId && h.PageNumber == 5);
        Assert.Contains(hits, h => h.Gallery.SourceId == copies[1].SourceId && h.PageNumber == 5);
        var saved = hits.First(h => h.Gallery.SourceId == copies[0].SourceId && h.PageNumber == 5);
        scope.Services.Settings.Sources[1].Enabled = false;
        hits = await scope.Services.ImageSearch.SearchAsync(query, null, copies, CancellationToken.None);
        Assert.DoesNotContain(hits, h => h.Gallery.SourceId == copies[1].SourceId);
        File.WriteAllBytes(saved.FilePath, SyntheticImages.Create(999).EncodePng());
        Assert.False(scope.Services.ImageSearch.CanRead(saved));
        hits = await scope.Services.ImageSearch.SearchAsync(query, null, copies, CancellationToken.None);
        Assert.DoesNotContain(hits, h => h.Gallery.SourceId == copies[0].SourceId && h.PageNumber == 5);
    }

    [AvaloniaFact]
    public void ExplicitPageOverridesCompletedHistoryAndRepeatedPageResetsOffsets()
    {
        using var scope = new Fixture(); var gallery = scope.CreateCopies()[0];
        scope.Services.Progress.Set(gallery.WorkId, new ReadingProgress { PageNumber = 3, PageIndex = 1, PageOffset = .7, Completed = true, PageCountAtSave = 5 });
        using var reader = new ReaderViewModel(scope.Services, gallery, initialPageNumber: 5);
        Assert.Equal("5 / 5", reader.PageLabel);
        Assert.False(reader.ShowRestoreHint);
        Assert.Equal(0, reader.PageOffsetForTest);
        Assert.True(reader.TryNavigateToPageNumber(3));
        Assert.True(reader.TryNavigateToPageNumber(2));
        Assert.Equal("2 / 5", reader.PageLabel);
        Assert.Empty(reader.Source.Pages[1].Candidates);
        Assert.True(reader.TryNavigateToPageNumber(3));
        Assert.Equal("3 / 5", reader.PageLabel);
        Assert.True(reader.TryNavigateToPageNumber(3)); Assert.Equal(0, reader.PageOffsetForTest);
        Assert.Throws<FileNotFoundException>(() => new ReaderViewModel(scope.Services, gallery, initialPageNumber: 6));
    }

    [AvaloniaFact]
    public async Task OnlineCardsShowIncompleteLocalCopyAndRefreshAfterRepair()
    {
        using var scope = new Fixture();
        var galleries = scope.CreateCopies(named: true);
        using var shell = new ShellViewModel(scope.Services);
        await shell.Library.InitializeAsync();
        var listing = new EhViewerForPc.Core.Online.EhGalleryListPage();
        listing.Items.Add(new() { Gid = 42, Token = "fixture", Title = "合成测试 · 本地缺页" });
        shell.Online.ApplyPage(listing, "https://e-hentai.org/", reset: true);
        var item = Assert.Single(shell.Online.Items);
        Assert.True(item.IsInLocalLibrary);
        Assert.Equal("本地缺页", item.LocalLibraryLabel);
        var window = new Window { Width = 1000, Height = 700, Content = new OnlineFeedPage { DataContext = shell.Online } };
        try
        {
            window.Show(); await Task.Delay(100); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var badge = window.GetVisualDescendants().OfType<Border>().Single(b => AutomationProperties.GetAutomationId(b) == "Online.LocalBadge");
            Assert.True(badge.IsEffectivelyVisible);
            Assert.Equal("本地缺页", Assert.IsType<TextBlock>(badge.Child).Text);
            foreach (int page in new[] { 2, 4 }) File.WriteAllBytes(Path.Combine(galleries[0].FolderPath, $"{page:D8}.png"), SyntheticImages.Create(42 + page).EncodePng());
            await shell.Library.RescanAllAsync();
            Assert.Equal("已在本地", item.LocalLibraryLabel);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1200, 800, false)]
    [InlineData(920, 600, false)]
    [InlineData(740, 600, false)]
    [InlineData(640, 480, false)]
    [InlineData(540, 420, false)]
    [InlineData(1200, 800, true)]
    [InlineData(920, 600, true)]
    [InlineData(740, 600, true)]
    [InlineData(640, 480, true)]
    [InlineData(540, 420, true)]
    public async Task ReaderToolbarMissingPageAndBookmarkDialogRenderWithoutDesktopWindows(int width, int height, bool light)
    {
        using var scope = new Fixture();
        var gallery = scope.CreateCopies()[0];
        Application.Current!.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        StartupOptions.Current = StartupOptions.Parse(["--ui-smoke"]);
        using var shell = new ShellViewModel(scope.Services);
        var window = new MainWindow(shell) { Width = width, Height = height };
        using var reader = new ReaderPage(new ReaderViewModel(scope.Services, gallery, initialPageNumber: 2));
        try
        {
            window.Show(); window.ShowDiagnosticPage(reader);
            await Task.Delay(150); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var fit = window.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetAutomationId(c) == "Reader.FitMode");
            Assert.Equal(13, fit.FontSize);
            Assert.True(fit.Bounds.Height <= 40, $"Fit height: {fit.Bounds.Height}");
            var more = window.FindControl<Button>("ReaderMoreButton")!;
            var position = more.TranslatePoint(default, window)!.Value;
            Assert.True(position.X >= 0 && position.X + more.Bounds.Width <= width, $"More position: {position}");
            var menu = Assert.IsType<MenuFlyout>(more.Flyout);
            Assert.Equal(PlacementMode.BottomEdgeAlignedRight, menu.Placement);
            menu.ShowAt(more);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(more, menu.Target);
            Assert.True(menu.IsOpen);
            var directions = menu.Items.OfType<MenuItem>().Take(2).ToArray();
            Assert.Equal(new[] { "左→右", "右→左" }, directions.Select(m => m.Header));
            Assert.All(directions, item => Assert.False(item.IsEnabled));
            Assert.Equal(reader.ViewModel.RightToLeft, directions[1].IsChecked);
            menu.Hide();
            var missing = reader.GetVisualDescendants().OfType<SelectableTextBlock>().Single(c => AutomationProperties.GetAutomationId(c) == "Reader.MissingPage");
            Assert.True(missing.IsEffectivelyVisible);
            Assert.Equal("第 2 页缺失", missing.Text);
            string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.qa/ui-fixes-20261113"));
            Directory.CreateDirectory(output);
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, $"reader-{width}-{(light ? "light" : "dark")}.png"), PngBitmapEncoderOptions.Default);
            shell.Bookmarks.AddCurrentCommand.Execute(null);
            shell.Online.OpenBookmarksCommand.Execute(null);
            Assert.NotNull(window.BookmarksDialog);
            await Task.Delay(100); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Same(reader, window.NavigationFrame.Content);
            var dialog = window.BookmarksDialog!;
            Assert.True(dialog.Bounds.Width <= width);
            Assert.True(dialog.Bounds.Height <= height);
            shell.Bookmarks.Selected = null;
            window.UpdateLayout();
            var editor = dialog.GetVisualDescendants().OfType<ScrollViewer>().Single(c => AutomationProperties.GetAutomationId(c) == "Bookmarks.Editor");
            var nameInput = editor.GetVisualDescendants().OfType<TextBox>().First();
            Assert.True(editor.IsEffectivelyVisible);
            Assert.False(nameInput.IsEffectivelyEnabled);
            shell.Bookmarks.Selected = shell.Bookmarks.Items[0];
            Assert.True(nameInput.IsEffectivelyEnabled);
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, $"bookmarks-{width}-{(light ? "light" : "dark")}.png"), PngBitmapEncoderOptions.Default);
            dialog.Hide();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ExplicitIndexCancellationAndShutdownPreservePublishedGeneration()
    {
        using var scope = new Fixture();
        var galleries = scope.CreateCopies();
        scope.Services.Settings.ImageSearch.Enabled = true;
        using var vm = new ImageSearchViewModel(scope.Services, () => galleries);
        await vm.BuildIndexCommand.ExecuteAsync(null);
        string pointer = Path.Combine(scope.Services.ImageSearch.DirectoryPath, "current.json");
        byte[] published = File.ReadAllBytes(pointer);
        Task canceled = vm.RebuildIndexCommand.ExecuteAsync(null);
        vm.CancelCommand.Execute(null);
        await canceled;
        Assert.False(scope.Services.ImageSearch.IsBuilding);
        Assert.Contains("已取消", vm.StatusText);
        Assert.Equal(published, File.ReadAllBytes(pointer));
        Task shutdown = vm.RebuildIndexCommand.ExecuteAsync(null);
        scope.Services.ImageSearch.Dispose();
        await shutdown;
        Assert.False(scope.Services.ImageSearch.IsBuilding);
        Assert.Equal(published, File.ReadAllBytes(pointer));
    }

    [AvaloniaFact]
    public async Task IndexBuildSurvivesDisposedPageAndNewPageReceivesProgress()
    {
        using var scope = new Fixture();
        var galleries = scope.CreateCopies();
        scope.Services.Settings.ImageSearch.Enabled = true;
        var first = new ImageSearchViewModel(scope.Services, () => galleries);
        var build = first.BuildIndexCommand.ExecuteAsync(null);
        Assert.True(scope.Services.ImageSearch.IsBuilding);
        first.Dispose();
        using var replacement = new ImageSearchViewModel(scope.Services, () => galleries);
        Assert.True(replacement.IsBuilding);
        Assert.False(replacement.CanBuild);
        await build;
        await replacement.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.False(scope.Services.ImageSearch.IsBuilding);
        Assert.True(replacement.HasIndex, replacement.StatusText);
        Assert.Equal(6, replacement.IndexedPages);
        Assert.True(replacement.CanBuild);
    }

    [AvaloniaFact]
    public void MissingLeadingAndTrailingPagesRetainExpectedCountAndNeverCompleteCopy()
    {
        using var scope = new Fixture();
        var gallery = scope.CreateCopies()[0];
        File.Delete(Path.Combine(gallery.FolderPath, "00000001.png"));
        gallery.ExpectedPages = 8;
        using var reader = new ReaderViewModel(scope.Services, gallery, initialPageNumber: 1);
        Assert.Equal(8, reader.PageCount);
        Assert.True(reader.IsCurrentPageMissing);
        reader.GoToIndex(7, scrollContinuous: false);
        Assert.Equal("8 / 8", reader.PageLabel);
        Assert.True(reader.IsCurrentPageMissing);
        reader.SaveProgress();
        Assert.False(scope.Services.Progress.Get(gallery.WorkId)!.Completed);
    }

    [AvaloniaFact]
    public async Task MissingPageSlotsSurviveModeChangesAndProgressRestore()
    {
        using var scope = new Fixture();
        var gallery = scope.CreateCopies()[0];
        using (var reader = new ReaderViewModel(scope.Services, gallery, initialPageNumber: 2))
        {
            Assert.Equal(5, reader.PageCount);
            Assert.True(reader.ShowSingleMissingPage);
            foreach (var mode in new[] { ReadingMode.Continuous, ReadingMode.Double, ReadingMode.Single })
            {
                await reader.SetModeAsync(mode);
                Assert.Equal("2 / 5", reader.PageLabel);
                Assert.True(reader.IsCurrentPageMissing);
                if (mode == ReadingMode.Continuous) Assert.True(reader.ContinuousItems[1].IsMissing);
                reader.Step(1);
                Assert.Equal("3 / 5", reader.PageLabel);
                Assert.False(reader.IsCurrentPageMissing);
                reader.Step(-1);
            }
        }
        using var restored = new ReaderViewModel(scope.Services, gallery);
        Assert.Equal("2 / 5", restored.PageLabel);
        Assert.True(restored.IsCurrentPageMissing);
    }

    [AvaloniaFact]
    public async Task SearchPageUsesRealIndexAndRaisesExactCopyReadRequest()
    {
        using var scope = new Fixture(); var galleries = scope.CreateCopies(); scope.Services.Settings.ImageSearch.Enabled = true;
        using var vm = new ImageSearchViewModel(scope.Services, () => galleries);
        await vm.BuildIndexCommand.ExecuteAsync(null);
        Assert.True(vm.HasIndex, vm.StatusText);
        await vm.LoadFileAsync(Path.Combine(galleries[0].FolderPath, "00000005.png"));
        Assert.True(vm.CanSearch);
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.True(vm.HasResults, vm.StatusText);
        using (var page = new ImageSearchPage(vm))
        {
            var window = new Window { Width = 1200, Height = 800, Content = page };
            try
            {
                window.Show(); Dispatcher.UIThread.RunJobs(); await Task.Delay(150); Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.qa/image-search/ui")); Directory.CreateDirectory(output);
                frame.Save(Path.Combine(output, "search-results-1200.png"), PngBitmapEncoderOptions.Default);
            }
            finally { window.Close(); }
        }
        LocalPageReadRequest? requested = null; vm.ReadRequested += (_, request) => requested = request;
        var hit = vm.Results.First(r => r.Hit.PageNumber == 5 && r.Hit.Gallery.SourceId == galleries[1].SourceId);
        vm.ReadHitCommand.Execute(hit);
        Assert.NotNull(requested); Assert.Same(galleries[1], requested.Gallery); Assert.Equal(5, requested.PageNumber);
    }

    [AvaloniaTheory]
    [InlineData(1200, 800, false)]
    [InlineData(540, 700, false)]
    [InlineData(1200, 800, true)]
    [InlineData(540, 700, true)]
    public async Task SearchDocumentRendersOffscreenWithReachableControls(int width, int height, bool light)
    {
        using var scope = new Fixture();
        Application.Current!.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        using var vm = new ImageSearchViewModel(scope.Services, () => []);
        using var page = new ImageSearchPage(vm);
        var window = new Window { Width = width, Height = height, Content = page };
        try
        {
            window.Show(); // Headless implementation: no native window or focus.
            await vm.RefreshAsync(); Dispatcher.UIThread.RunJobs();
            Assert.NotNull(page.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "ImageSearch.OpenFile"));
            Assert.Contains("尚未启用", vm.StatusText);
            Assert.False(vm.CanBuild); Assert.False(vm.CanSearch);
            window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.qa/image-search/ui"));
            Directory.CreateDirectory(output);
            frame.Save(Path.Combine(output, $"search-{width}-{(light ? "light" : "dark")}.png"), PngBitmapEncoderOptions.Default);
        }
        finally { window.Close(); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.qa/progress-repair-20261114/ui-" + Guid.NewGuid().ToString("N")));
        internal AppServices Services { get; }
        internal Fixture()
        {
            var paths = new AppPaths(Path.Combine(_root, "data")); paths.EnsureCreated();
            var settings = new AppSettings(); settings.Download.RootPath = Path.Combine(_root, "downloads"); settings.Download.AddAsSource = false; settings.Download.AutoStart = false;
            settings.Network.ProxyMode = ProxyMode.None;
            settings.Network.HostsOverride = "127.0.0.1 e-hentai.org\n127.0.0.1 exhentai.org";
            new SettingsStore(paths).Save(settings, out _); Services = AppServices.Initialize(paths);
        }
        internal GalleryInfo[] CreateCopies(bool named = false)
        {
            var copies = new List<GalleryInfo>();
            for (int i = 0; i < 2; i++)
            {
                string root = Path.Combine(_root, "source-" + i), folder = Path.Combine(root, named ? "42-合成测试" : "gallery"); Directory.CreateDirectory(folder);
                var source = Services.Settings.AddSource(root, "来源 " + i);
                foreach (int page in new[] { 1, 3, 5 }) File.WriteAllBytes(Path.Combine(folder, $"{page:D8}.png"), SyntheticImages.Create(42 + page, 200, 280).EncodePng());
                copies.Add(new GalleryInfo { StableId = "copy-" + i, SourceId = source.Id, SourceName = source.DisplayName, WorkId = "no-gid-work", Title = "合成测试页图", FolderName = "gallery", FolderPath = folder, PageCount = 3, ExpectedPages = 5 });
            }
            return copies.ToArray();
        }
        public void Dispose() { Services.Dispose(); Directory.Delete(_root, true); }
    }
}

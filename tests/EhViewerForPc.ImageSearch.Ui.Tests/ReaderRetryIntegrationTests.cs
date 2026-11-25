using System.Net;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EhViewerForPc.Core.Download;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.ImageSearch;
using EhViewerForPc.Services;
using EhViewerForPc.ViewModels;
using EhViewerForPc.Views;
using SkiaSharp;

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public sealed class ReaderRetryIntegrationTests
{
    [AvaloniaFact]
    public async Task LibraryJapaneseFilterUsesKnownMetadataForUntaggedCopies()
    {
        using var fixture = new Fixture();
        var services = fixture.Services;
        string root = services.Settings.Sources.Single().RootPath;
        foreach (string name in new[] { "43-[Japanese] tagged", "44-metadata", "45-[Chinese] translated", "46-unknown" })
        {
            string folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            string gid = name[..2];
            File.WriteAllText(Path.Combine(folder, ".ehviewer"), $"VERSION2\n0\n{gid}\n0123456789\n0\n0\n0\n1\n");
            File.WriteAllBytes(Path.Combine(folder, "00000001.png"), fixture.Handler.Image);
        }

        using var library = new MainViewModel(services);
        var japanese = Assert.Single(library.LanguageOptions, option => option.Value == "Japanese");
        Assert.Equal("日文", japanese.Label);
        library.SelectedLanguage = japanese;
        await library.RescanAllAsync();
        Assert.Equal(43, Assert.Single(library.Galleries).Gid);

        services.Works.Upsert(WorkRecord.FromDetails(new() { Gid = 44, Language = "Japanese" }));
        services.Works.Upsert(WorkRecord.FromDetails(new() { Gid = 45, Language = "Japanese" }));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(japanese, library.SelectedLanguage);
        Assert.Equal(new long?[] { 43, 44 }, library.Galleries.Select(item => item.Gid).Order().ToArray());
        Assert.Empty(fixture.Handler.Requests);
    }

    [AvaloniaFact]
    public async Task CompleteWebpCopyOpensLocallyWithItsTranslationSource()
    {
        using var fixture = new Fixture();
        string root = fixture.Services.Settings.Sources.Single().RootPath;
        string folder = Directory.CreateDirectory(Path.Combine(root, "43-webp")).FullName;
        File.WriteAllText(Path.Combine(folder, ".ehviewer"), "VERSION2\n0\n43\n0123456789\n0\n0\n0\n8\n");
        using var bitmap = new SKBitmap(32, 48);
        bitmap.Erase(SKColors.Orange);
        using var webp = bitmap.Encode(SKEncodedImageFormat.Webp, 90);
        byte[] pageBytes = webp.ToArray();
        for (int number = 1; number <= 8; number++)
            File.WriteAllBytes(Path.Combine(folder, $"{number:D8}.webp"), pageBytes);

        using var library = new MainViewModel(fixture.Services);
        await library.RescanAllAsync();
        var copy = library.SnapshotAllWorks().Single(item => item.Gid == 43).Info;
        Assert.Empty(copy.MissingRanges);
        Assert.Equal(8, copy.PageCount);
        using var reader = new ReaderViewModel(fixture.Services, copy);
        await reader.InitializeAsync();
        Assert.Equal("1 / 8", reader.PageLabel);
        Assert.False(reader.IsCurrentPageMissing);

        var firstPage = Assert.IsType<LocalReaderSource>(reader.Source).Pages[0].Primary!;
        using var control = await fixture.Services.Images.LoadFileAsync(firstPage.FilePath, 32, 48, CancellationToken.None);
        Assert.NotNull(control);

        var request = Assert.IsType<PageImageRequest>(reader.SingleRequest);
        using var decoded = await request.LoadAsync(32, 48, CancellationToken.None);
        Assert.NotNull(decoded);
        var translation = Assert.IsType<ReaderTranslationImage>(decoded.TranslationImage);
        Assert.Equal(firstPage.FilePath, translation.Path);
        Assert.Equal(firstPage.Length, translation.Length);
        Assert.Equal(firstPage.MtimeTicks, translation.ModifiedUtc.Ticks);
        Assert.Empty(fixture.Handler.Requests);
    }

    [AvaloniaFact]
    public async Task OnlineListDownloadAppearsBeforeGalleryDetailsReturnAndIsRemovedWithAnEmptyTask()
    {
        using var fixture = new Fixture();
        var services = fixture.Services;
        services.Settings.Download.AddAsSource = true;
        services.Settings.Sources.Clear();
        fixture.Handler.BlockGallery = true;
        using var shell = new ShellViewModel(services);
        await shell.Library.InitializeAsync();
        string? scan = services.Settings.LastScanCompletedUtc;
        Task enqueue = shell.DownloadAsync(new() { Gid = 42, Token = "0123456789", Title = "fixture" });
        try
        {
            await enqueue.WaitAsync(TimeSpan.FromSeconds(2));
            await fixture.Handler.GalleryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var download = Assert.Single(shell.Downloads.Tasks);
            var card = Assert.Single(shell.Library.Galleries);
            Assert.Equal(42, card.Gid);
            Assert.False(card.CanRead);
            Assert.True(card.HasActiveDownload);
            Assert.Empty(Directory.EnumerateFiles(download.Task.FolderPath));
            await shell.Downloads.RemoveTask(download);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(shell.Library.Galleries);
            Assert.Equal(scan, services.Settings.LastScanCompletedUtc);
        }
        finally
        {
            fixture.Handler.Release.TrySetResult();
            await enqueue;
        }
    }

    [AvaloniaFact]
    public async Task CachedSourcesStayIntactWhileThreeSavedDownloadsAppearAndANewSourceIsAdded()
    {
        using var fixture = new Fixture();
        var services = fixture.Services;
        string source = Assert.Single(services.Settings.Sources).RootPath;
        string downloadRoot = Directory.CreateDirectory(services.Downloads.RootPath).FullName;
        services.Settings.Download.AddAsSource = true;
        services.Settings.AddSource(downloadRoot);
        using (var initial = new MainViewModel(services)) await initial.RescanAllAsync();
        string? firstScan = services.Settings.LastScanCompletedUtc;
        string? firstSourceScan = services.Settings.Sources[0].LastScanUtc;

        string external = Directory.CreateDirectory(Path.Combine(source, "80-external-change")).FullName;
        File.WriteAllBytes(Path.Combine(external, "00000001.png"), fixture.Handler.Image);
        foreach (int gid in new[] { 81, 82, 83 })
        {
            var task = services.Downloads.Enqueue(new() { Gid = gid, Token = "fixture", Title = $"下载 {gid}", Pages = 4 }, startImmediately: false);
            Directory.CreateDirectory(task.FolderPath);
            File.WriteAllBytes(Path.Combine(task.FolderPath, "00000001.png"), fixture.Handler.Image);
            task.DownloadedPages = 1;
            services.Downloads.Pause(task);
        }

        using var shell = new ShellViewModel(services);
        int scanChanges = 0;
        shell.Library.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(MainViewModel.IsScanning)) scanChanges++; };
        await shell.Library.InitializeAsync();
        Assert.Equal(0, scanChanges);
        Assert.Equal(firstScan, services.Settings.LastScanCompletedUtc);
        Assert.Equal(firstSourceScan, services.Settings.Sources[0].LastScanUtc);
        for (int attempt = 0; attempt < 150 && shell.Library.Galleries.Count(card => card.CanRead) != 4; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
        Assert.Equal(new long?[] { 42, 81, 82, 83 }, shell.Library.Galleries.Select(card => card.Gid).Order());

        var originalCard = shell.Library.Galleries.Single(card => card.Gid == 42);
        string newSource = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(source)!, "extra-source")).FullName;
        string newCopy = Directory.CreateDirectory(Path.Combine(newSource, "84-new-source")).FullName;
        File.WriteAllBytes(Path.Combine(newCopy, "00000001.png"), fixture.Handler.Image);
        Assert.Null(await shell.Library.AddSourceAsync(newSource));
        Assert.Equal(firstScan, services.Settings.LastScanCompletedUtc);
        Assert.Equal(firstSourceScan, services.Settings.Sources[0].LastScanUtc);
        Assert.Same(originalCard, shell.Library.Galleries.Single(card => card.Gid == 42));
        Assert.Equal(new long?[] { 42, 81, 82, 83, 84 }, shell.Library.Galleries.Select(card => card.Gid).Order());

        foreach (var task in services.Downloads.Tasks) services.Downloads.Remove(task, deleteFiles: false);
        shell.Dispose();
        services.Dispose();
        using var restarted = AppServices.Initialize(services.Paths);
        using var restored = new MainViewModel(restarted);
        await restored.InitializeAsync();
        Assert.Empty(restarted.Downloads.Tasks);
        Assert.Equal(firstScan, restarted.Settings.LastScanCompletedUtc);
        Assert.Equal(new long?[] { 42, 81, 82, 83, 84 }, restored.Galleries.Select(card => card.Gid).Order());
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadAppearsAfterFirstWrittenPageAndReadingKeepsTheLibraryCopy(bool anotherCopyExists)
    {
        using var fixture = new Fixture();
        var settings = fixture.Services.Settings.Download;
        settings.AddAsSource = true;
        settings.MaxConcurrentPages = 1;
        settings.WriteThumb = false;
        fixture.Handler.PauseAfterFirstPage = true;
        if (!anotherCopyExists) fixture.Services.Settings.Sources.Clear();
        using var shell = new ShellViewModel(fixture.Services);
        await shell.Library.InitializeAsync();
        string? scanAt = fixture.Services.Settings.LastScanCompletedUtc;

        await shell.DownloadDetailsAsync(new() { Gid = 42, Token = "0123456789", Title = "downloaded", Pages = 4 }, ImageQuality.Resampled);
        var task = Assert.Single(fixture.Services.Downloads.Tasks);
        string folder = AppSettings.NormalizeRoot(task.FolderPath);
        var queuedCard = Assert.Single(shell.Library.Galleries);
        await fixture.Handler.SecondPageStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, task.DownloadedPages);
        GalleryInfo? copy = null;
        for (int attempt = 0; attempt < 100 && copy is not { PageCount: > 0 }; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            copy = shell.Library.SnapshotAllWorks().SelectMany(item => item.Versions)
                .SingleOrDefault(version => version.FolderPath == folder);
            if (copy is not { PageCount: > 0 }) await Task.Delay(30);
        }
        Assert.NotNull(copy);
        Assert.Equal(1, copy.PageCount);
        Assert.True(copy.IsIncomplete);
        Assert.Same(queuedCard, Assert.Single(shell.Library.Galleries));
        Assert.Equal(scanAt, fixture.Services.Settings.LastScanCompletedUtc);

        DownloadReadRequest? requested = null;
        shell.Downloads.ReadRequested += (_, request) => requested = request;
        await shell.Downloads.ReadTaskCommand.ExecuteAsync(Assert.Single(shell.Downloads.Tasks));
        Assert.Same(copy, requested?.Info);
        using var reader = new ReaderViewModel(fixture.Services, new LocalReaderSource(fixture.Services, requested!.Info));
        int changes = 0;
        shell.Library.LocalIndexChanged += (_, _) => changes++;
        await reader.InitializeAsync();
        Assert.Equal(0, changes);
        Assert.Same(copy, shell.Library.FindLocalCopy(folder));
        Assert.Equal(scanAt, fixture.Services.Settings.LastScanCompletedUtc);

        fixture.Handler.Release.TrySetResult();
        for (int attempt = 0; attempt < 150 && shell.Library.FindLocalCopy(folder)?.PageCount != 4; attempt++)
            await Task.Delay(30);
        Assert.Equal(4, shell.Library.FindLocalCopy(folder)?.PageCount);
        Assert.Single(shell.Library.SnapshotAllWorks().SelectMany(item => item.Versions), version => version.FolderPath == folder);
        Assert.Equal(scanAt, fixture.Services.Settings.LastScanCompletedUtc);
    }

    [AvaloniaFact]
    public async Task RetryMenuBindsTheAsyncCommandAndDisablesItDuringDownload()
    {
        using var fixture = new Fixture(); fixture.Handler.BlockImage = true;
        StartupOptions.Current = StartupOptions.Parse(["--ui-smoke"]);
        using var shell = new ShellViewModel(fixture.Services);
        using var page = new ReaderPage(new ReaderViewModel(fixture.Services, fixture.Info, 2));
        var window = new MainWindow(shell);
        try
        {
            window.Show(); window.ShowDiagnosticPage(page);
            await Task.Delay(100); Dispatcher.UIThread.RunJobs();
            var more = window.FindControl<Button>("ReaderMoreButton")!;
            var menu = (MenuFlyout)more.Flyout!;
            menu.ShowAt(more); Dispatcher.UIThread.RunJobs();
            var retry = menu.Items.OfType<MenuItem>().Single(m => AutomationProperties.GetAutomationId(m) == "Reader.Retry");
            Assert.Same(page.ViewModel.RetryCurrentCommand, retry.Command);
            retry.Command!.Execute(null);
            await fixture.Handler.ImageStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(retry.IsEffectivelyEnabled);
            fixture.Handler.Release.TrySetResult();
            await page.ViewModel.RetryCurrentCommand.ExecutionTask!;
            Assert.True(retry.IsEffectivelyEnabled);
            Assert.False(page.ViewModel.IsCurrentPageMissing, page.ViewModel.RestoreHint);
            menu.Hide();
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(ReadingMode.Single)]
    [InlineData(ReadingMode.Continuous)]
    [InlineData(ReadingMode.Double)]
    public async Task MissingPageRetryDownloadsOnlyThatPageAndRefreshesTheCurrentSlot(ReadingMode mode)
    {
        using var fixture = new Fixture();
        using var library = new MainViewModel(fixture.Services);
        await library.InitializeAsync();
        var item = library.SnapshotAllWorks().Single();
        using var reader = new ReaderViewModel(fixture.Services, item.Info, initialPageNumber: 2);
        await reader.SetModeAsync(mode);
        var before = fixture.Snapshot();
        Assert.True(reader.IsCurrentPageMissing);
        await reader.RetryCurrentCommand.ExecuteAsync(null);
        Assert.False(reader.IsCurrentPageMissing, reader.RestoreHint);
        Assert.Equal("2 / 4", reader.PageLabel);
        Assert.False(File.Exists(Path.Combine(fixture.Folder, "00000003.png")));
        Assert.Equal(new[] { "/s/2222222222/42-2", "/2.png" }, fixture.Handler.Requests.Where(p => p.StartsWith("/s/") || p.EndsWith(".png")));
        Assert.Equal(before, fixture.Snapshot(exclude: "00000002.png"));
        Assert.Equal(new[] { new PageRange(3, 3) }, item.Info.MissingRanges);
        Assert.Equal("缺页 3", reader.MissingSummary);
        if (mode == ReadingMode.Continuous)
        {
            Assert.False(reader.ContinuousItems[1].IsMissing);
            Assert.Equal(200, reader.ContinuousItems[1].OriginalWidth);
            Assert.Equal(280, reader.ContinuousItems[1].OriginalHeight);
        }
        if (mode == ReadingMode.Double) Assert.False(reader.HorizontalDisplayItems.Single(p => p.Number == 2).IsMissing);
        using var decoded = await reader.Source.CreateRequest(1).LoadAsync(200, 280, CancellationToken.None);
        Assert.NotNull(decoded);
    }

    [AvaloniaTheory]
    [InlineData(ReadingMode.Single)]
    [InlineData(ReadingMode.Continuous)]
    [InlineData(ReadingMode.Double)]
    public async Task RetryKeepsNumberedTailAndMissingSlotsWhenALooseCoverExists(ReadingMode mode)
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(Path.Combine(fixture.Folder, "cover.png"), fixture.Handler.Image);
        using var reader = new ReaderViewModel(fixture.Services, fixture.Info, initialPageNumber: 4);
        await reader.SetModeAsync(mode);
        var unchanged = fixture.Snapshot(exclude: "00000004.png");
        Assert.False(reader.IsCurrentPageMissing);

        await reader.RetryCurrentCommand.ExecuteAsync(null);

        Assert.False(reader.IsCurrentPageMissing, reader.RestoreHint);
        Assert.Equal("00000004.png", reader.Source.Pages[3].Primary!.FileName);
        Assert.Equal("缺页 2-3", reader.MissingSummary);
        Assert.Equal("4 / 4", reader.PageLabel);
        Assert.Equal(unchanged, fixture.Snapshot(exclude: "00000004.png"));
        if (mode == ReadingMode.Continuous) Assert.False(reader.ContinuousItems[3].IsMissing);
        if (mode == ReadingMode.Double) Assert.False(reader.HorizontalDisplayItems.Single(page => page.Number == 4).IsMissing);
    }

    [AvaloniaFact]
    public async Task RetryReplacesOnlyTheSelectedExistingPageAndBypassesLocalCache()
    {
        using var fixture = new Fixture();
        string path = Path.Combine(fixture.Folder, "00000001.png");
        var before = fixture.Snapshot(exclude: "00000001.png");
        using var reader = new ReaderViewModel(fixture.Services, fixture.Info, initialPageNumber: 1);
        string originalKey = reader.SingleRequest!.Key;
        await reader.RetryCurrentCommand.ExecuteAsync(null);
        Assert.NotEqual(originalKey, reader.SingleRequest!.Key);
        Assert.Equal(fixture.Handler.Image, File.ReadAllBytes(path));
        Assert.Equal(before, fixture.Snapshot(exclude: "00000001.png"));
        Assert.Single(fixture.Handler.Requests, p => p.EndsWith(".png"));
        Assert.False(File.Exists(Path.Combine(fixture.Folder, "00000002.png")));
    }

    [AvaloniaFact]
    public async Task FailedRetryKeepsTheOriginalPageAndReportsTheError()
    {
        using var fixture = new Fixture(); fixture.Handler.FailImage = true;
        using var reader = new ReaderViewModel(fixture.Services, fixture.Info, initialPageNumber: 1);
        var before = fixture.Snapshot();
        await reader.RetryCurrentCommand.ExecuteAsync(null);
        Assert.Equal(before, fixture.Snapshot());
        Assert.Contains("HTTP 500", reader.RestoreHint);
        Assert.True(reader.ShowRestoreHint);
        Assert.DoesNotContain(Directory.GetFiles(fixture.Folder), p => p.EndsWith(".part") || p.EndsWith(".retry"));
    }

    [AvaloniaFact]
    public async Task ConcurrentReadersShareOneDownloadAndNavigationDoesNotJumpBack()
    {
        using var fixture = new Fixture(); fixture.Handler.BlockImage = true;
        using var first = new ReaderViewModel(fixture.Services, fixture.Info, initialPageNumber: 2);
        using var second = new ReaderViewModel(fixture.Services, fixture.Info, initialPageNumber: 2);
        Task retry = first.RetryCurrentCommand.ExecuteAsync(null);
        await fixture.Handler.ImageStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task shared = second.RetryCurrentCommand.ExecuteAsync(null);
        first.GoToIndex(3, scrollContinuous: false);
        fixture.Handler.Release.TrySetResult();
        await Task.WhenAll(retry, shared);
        Assert.Single(fixture.Handler.Requests, p => p.EndsWith(".png"));
        Assert.Equal("4 / 4", first.PageLabel);
        Assert.False(second.IsCurrentPageMissing, second.RestoreHint);
    }

    [AvaloniaFact]
    public async Task PageRetryDoesNotStartPendingDownloadsAndPausedTasksRemainPaused()
    {
        using var fixture = new Fixture();
        var task = fixture.Services.Downloads.EnqueueRepair(new() { Gid = 42, Token = "0123456789", Pages = 4 },
            fixture.Folder, fixture.Services.Settings.Sources.Single().RootPath, startImmediately: false);
        using var reader = new ReaderViewModel(fixture.Services, fixture.Info, initialPageNumber: 2);
        await reader.RetryCurrentCommand.ExecuteAsync(null);
        Assert.Equal(DownloadState.Pending, task.State);
        fixture.Services.Downloads.Pause(task);
        reader.GoToIndex(2, scrollContinuous: false);
        await reader.RetryCurrentCommand.ExecuteAsync(null);
        Assert.Equal(DownloadState.Paused, task.State);
        Assert.False(reader.IsCurrentPageMissing, reader.RestoreHint);
        Assert.Equal(0, fixture.Services.Downloads.ActiveCount);
    }

    [AvaloniaFact]
    public async Task RetryWaitsForTheExistingFolderWriterBeforeReplacingItsPage()
    {
        using var fixture = new Fixture(); fixture.Handler.BlockImage = true;
        var task = fixture.Services.Downloads.EnqueueRepair(new() { Gid = 42, Token = "0123456789", Pages = 4 },
            fixture.Folder, fixture.Services.Settings.Sources.Single().RootPath);
        for (int i = 0; i < 250 && !fixture.Handler.ImageStarted.Task.IsCompleted && task.State != DownloadState.Failed; i++) await Task.Delay(20);
        Assert.True(fixture.Handler.ImageStarted.Task.IsCompleted, task.State + " / " + task.Message);
        using var reader = new ReaderViewModel(fixture.Services, fixture.Info, initialPageNumber: 2);
        Task retry = reader.RetryCurrentCommand.ExecuteAsync(null);
        fixture.Handler.Release.TrySetResult();
        await retry;
        Assert.Equal(DownloadState.Completed, task.State);
        Assert.False(reader.IsCurrentPageMissing, reader.RestoreHint);
        Assert.Equal(2, fixture.Handler.Requests.Count(p => p == "/2.png"));
        Assert.Equal(2, fixture.Handler.Requests.Count(p => p == "/s/2222222222/42-2"));
        Assert.Single(fixture.Handler.Requests, p => p == "/3.png");
    }

    [AvaloniaFact]
    public async Task OrdinaryFolderRetryReloadsFilesWithoutInventingAnOnlinePage()
    {
        using var fixture = new Fixture();
        fixture.Info.Gid = null; fixture.Info.Token = string.Empty; fixture.Info.HasNaturalPageNames = true;
        using var reader = new ReaderViewModel(fixture.Services, fixture.Info, initialPageNumber: 2);
        File.WriteAllBytes(Path.Combine(fixture.Folder, "00000002.png"), fixture.Handler.Image);
        await reader.RetryCurrentCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Handler.Requests);
        Assert.False(reader.IsCurrentPageMissing);
        Assert.Contains("只能重新载入本地文件", reader.RestoreHint);
    }

    [AvaloniaFact]
    public async Task DisposingDuringTransferCancelsOwnedWritesWithoutChangingTheSource()
    {
        using var fixture = new Fixture(); fixture.Handler.BlockImage = true;
        using var reader = new ReaderViewModel(fixture.Services, fixture.Info, initialPageNumber: 2);
        var before = fixture.Snapshot();
        Task retry = reader.RetryCurrentCommand.ExecuteAsync(null);
        await fixture.Handler.ImageStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reader.Dispose();
        fixture.Services.Dispose();
        await retry;
        Assert.Equal(before, fixture.Snapshot());
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(WorkspaceRoot(), ".qa/reader-retry-20261115/fixture-" + Guid.NewGuid().ToString("N"));
        private static string WorkspaceRoot()
        {
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
                if (File.Exists(Path.Combine(folder.FullName, "PRODUCT.md"))) return folder.FullName;
            throw new DirectoryNotFoundException("测试需要在项目工作区内运行。");
        }
        internal string Folder { get; }
        internal AppServices Services { get; }
        internal RetryHandler Handler { get; } = new();
        internal GalleryInfo Info { get; }
        internal Fixture(Action<string>? recycleDirectory = null)
        {
            string sourceRoot = Path.Combine(_root, "library");
            Folder = Path.GetFullPath(Path.Combine(sourceRoot, "42-fixture")); Directory.CreateDirectory(Folder);
            foreach (int page in new[] { 1, 4 }) File.WriteAllBytes(Path.Combine(Folder, $"{page:D8}.png"), SyntheticImages.Create(page, 200, 280).EncodePng());
            File.WriteAllText(Path.Combine(Folder, ".ehviewer"), "VERSION2\n0\n42\n0123456789\n0\n0\n0\n4\n");
            var paths = new AppPaths(Path.Combine(_root, "data")); paths.EnsureCreated();
            var settings = new AppSettings(); settings.Download.RootPath = Path.Combine(_root, "downloads");
            settings.Download.AutoStart = false; settings.Download.AddAsSource = false;
            settings.Network.ProxyMode = ProxyMode.None;
            var source = settings.AddSource(sourceRoot);
            new SettingsStore(paths).Save(settings, out _);
            Services = AppServices.Initialize(paths, s => new EhClient(s, new HttpClient(Handler), new HttpClient(Handler)), recycleDirectory);
            Info = Services.Scanner.Scan(sourceRoot, null, sourceId: source.Id, sourceName: source.DisplayName).Galleries.Single();
        }
        internal string[] Snapshot(string? exclude = null) => Directory.GetFiles(Folder).Where(p => Path.GetFileName(p) != exclude)
            .Order(StringComparer.Ordinal).Select(p => p + "|" + new FileInfo(p).Length + "|" + File.GetLastWriteTimeUtc(p).Ticks + "|" + Convert.ToHexString(File.ReadAllBytes(p))).ToArray();
        public void Dispose() { Services.Dispose(); Directory.Delete(_root, true); }
    }

    internal sealed class RetryHandler : HttpMessageHandler
    {
        internal byte[] Image { get; } = SyntheticImages.Create(99, 200, 280).EncodePng();
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Requests = new();
        internal readonly TaskCompletionSource ImageStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource GalleryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource SecondPageStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool FailImage, BlockImage, IgnoreImageCancellation, PauseAfterFirstPage, BlockGallery;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath; Requests.Enqueue(path);
            if (BlockGallery && path.StartsWith("/g/", StringComparison.Ordinal))
            {
                GalleryStarted.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            if (path.EndsWith(".png"))
            {
                ImageStarted.TrySetResult();
                if (PauseAfterFirstPage && path == "/2.png")
                {
                    SecondPageStarted.TrySetResult();
                    await Release.Task.WaitAsync(cancellationToken);
                }
                if (BlockImage) await Release.Task.WaitAsync(IgnoreImageCancellation ? CancellationToken.None : cancellationToken);
                return new(FailImage ? HttpStatusCode.InternalServerError : HttpStatusCode.OK) { Content = new ByteArrayContent(Image) };
            }
            string html = path.StartsWith("/s/")
                ? $"<img id='img' src='https://images.example/{path[(path.LastIndexOf('-') + 1)..]}.png'>"
                : "<h1 id='gn'>Fixture</h1><div id='gdd'><table><tr><td>Length:</td><td>4 pages</td></tr></table></div><div id='gdt'><a href='https://e-hentai.org/s/1111111111/42-1'><img></a><a href='https://e-hentai.org/s/2222222222/42-2'><img></a><a href='https://e-hentai.org/s/3333333333/42-3'><img></a><a href='https://e-hentai.org/s/4444444444/42-4'><img></a></div>";
            return new(HttpStatusCode.OK) { Content = new StringContent(html) };
        }
    }
}

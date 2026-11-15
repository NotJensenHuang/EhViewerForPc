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

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public sealed class ReaderRetryIntegrationTests
{
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
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool FailImage, BlockImage, IgnoreImageCancellation;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath; Requests.Enqueue(path);
            if (path.EndsWith(".png"))
            {
                ImageStarted.TrySetResult();
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

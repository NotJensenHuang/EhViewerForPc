using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EhViewerForPc.Controls;
using EhViewerForPc.Core.Download;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.Services;
using EhViewerForPc.ViewModels;

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public sealed class LocalManagementTests
{
    private static void MoveFixtureCopy(string folder)
    {
        string destination = Path.Combine(Directory.GetParent(Directory.GetParent(folder)!.FullName)!.FullName, "recycled-" + Guid.NewGuid().ToString("N"));
        DirectoryInfo? qa = new(folder);
        while (qa is not null && qa.Name != ".qa") qa = qa.Parent;
        if (qa is null || !PathIdentity.IsDescendantOf(folder, qa.FullName) || !PathIdentity.IsDescendantOf(destination, qa.FullName))
            throw new IOException("测试回收范围必须在工作区 .qa 内。");
        Directory.Move(folder, destination);
    }

    [AvaloniaFact]
    public async Task CompletedDownloadsOnlyAppearInArchiveIncludingLateProgressAndRecreatedPage()
    {
        using var fixture = new ReaderRetryIntegrationTests.Fixture();
        using var shell = new ShellViewModel(fixture.Services);
        var task = fixture.Services.Downloads.EnqueueRepair(new() { Gid = 42, Token = "0123456789", Pages = 4 },
            fixture.Folder, fixture.Services.Settings.Sources.Single().RootPath, startImmediately: false);
        shell.Downloads.SyncAll(); Assert.Single(shell.Downloads.Tasks);
        fixture.Services.Downloads.StartAll();
        for (int i = 0; i < 250 && task.State is not (DownloadState.Completed or DownloadState.Failed); i++) await Task.Delay(20);
        Assert.Equal(DownloadState.Completed, task.State);
        await Task.Delay(250); Dispatcher.UIThread.RunJobs();
        Assert.Empty(shell.Downloads.Tasks); Assert.False(shell.Downloads.HasTasks);
        Assert.Single(shell.Downloads.Archive);
        using var recreated = new DownloadsViewModel(fixture.Services, shell.Library);
        Assert.Empty(recreated.Tasks); Assert.Single(recreated.Archive);
    }

    [AvaloniaFact]
    public async Task DeletingOneCopyPreservesOtherVersionsProgressAndCacheWithoutRescanning()
    {
        int recycled = 0;
        using var fixture = new ReaderRetryIntegrationTests.Fixture(folder => { recycled++; MoveFixtureCopy(folder); });
        var services = fixture.Services;
        string secondRoot = Path.Combine(Directory.GetParent(Directory.GetParent(fixture.Folder)!.FullName)!.FullName, "second-source");
        string second = Path.Combine(secondRoot, "42-second"); Directory.CreateDirectory(second);
        foreach (string file in Directory.GetFiles(fixture.Folder)) File.Copy(file, Path.Combine(second, Path.GetFileName(file)));
        services.Settings.AddSource(secondRoot);
        using var shell = new ShellViewModel(services); await shell.Library.InitializeAsync();
        var item = shell.Library.SnapshotAllWorks().Single(); Assert.Equal(2, item.Versions.Count);
        var selected = item.Versions.Single(v => PathIdentity.Comparer.Equals(v.FolderPath, fixture.Folder));
        services.Progress.Set(item.WorkId, new ReadingProgress { PageNumber = 3, Favorite = true });
        using var versions = new VersionsViewModel(item, services, shell.Library.ApplyScannedCopy);
        versions.Deletion.Request(selected); versions.Deletion.CancelCommand.Execute(null);
        Assert.Equal(0, recycled); Assert.True(Directory.Exists(fixture.Folder));
        int scans = 0; shell.Library.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.IsScanning)) scans++; };
        versions.Deletion.Request(selected); await versions.Deletion.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(1, recycled); Assert.False(Directory.Exists(fixture.Folder)); Assert.True(Directory.Exists(second));
        Assert.Equal(0, scans); Assert.Single(shell.Library.SnapshotAllWorks().Single().Versions); Assert.Single(versions.Versions);
        Assert.Equal(3, services.Progress.Get(item.WorkId)!.PageNumber); Assert.True(services.Progress.Get(item.WorkId)!.Favorite);
        var source = services.Settings.Sources.Single(s => s.Id == selected.SourceId);
        Assert.Empty(services.CacheStore.Load(source.Id, source.RootPath, out _).Entries);
    }

    [AvaloniaFact]
    public async Task DeleteWaitsForDownloadWriterAndBlocksNewWritesUntilRecyclingEnds()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var fixture = new ReaderRetryIntegrationTests.Fixture(folder =>
        {
            Assert.Empty(Directory.GetFiles(folder, "*.part"));
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            MoveFixtureCopy(folder);
        });
        fixture.Handler.BlockImage = true;
        var source = fixture.Services.Settings.Sources.Single();
        var details = new EhViewerForPc.Core.Online.EhGalleryDetails { Gid = 42, Token = "0123456789", Pages = 4 };
        var task = fixture.Services.Downloads.EnqueueRepair(details, fixture.Folder, source.RootPath);
        await fixture.Handler.ImageStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task deleting = fixture.Services.DeleteLocalCopyAsync(fixture.Info, TestContext.Current.CancellationToken);
        try
        {
            for (int i = 0; i < 250 && !entered.IsSet; i++) await Task.Delay(20);
            Assert.True(entered.IsSet);
            Assert.Throws<IOException>(() => fixture.Services.Downloads.EnqueueRepair(details, fixture.Folder, source.RootPath, false));
            Assert.Throws<IOException>(() => fixture.Services.Downloads.Resume(task));
            await Assert.ThrowsAsync<IOException>(() => fixture.Services.Downloads.RedownloadPageAsync(details, 2, fixture.Folder, source.RootPath, null, TestContext.Current.CancellationToken));
        }
        finally { release.Set(); }
        await deleting;
        Assert.False(Directory.Exists(fixture.Folder)); Assert.Empty(fixture.Services.Downloads.Tasks);
    }

    [AvaloniaFact]
    public async Task RecycleFailureKeepsTheCopyAndConfirmationCanRetry()
    {
        bool fail = true;
        using var fixture = new ReaderRetryIntegrationTests.Fixture(folder => { if (fail) throw new IOException("fixture failure"); MoveFixtureCopy(folder); });
        using var deletion = new LocalCopyDeletionViewModel(fixture.Services);
        deletion.Request(fixture.Info); await deletion.ConfirmCommand.ExecuteAsync(null);
        Assert.True(Directory.Exists(fixture.Folder)); Assert.True(deletion.HasConfirmation);
        Assert.Contains("fixture failure", deletion.StatusText); Assert.True(deletion.ConfirmCommand.CanExecute(null));
        fail = false; await deletion.ConfirmCommand.ExecuteAsync(null);
        Assert.False(Directory.Exists(fixture.Folder)); Assert.False(deletion.HasConfirmation);
    }

    [AvaloniaFact]
    public async Task DeletionWaitsForPageRetryAndDisposalWaitsForRecycling()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var fixture = new ReaderRetryIntegrationTests.Fixture(folder =>
        {
            Assert.Empty(Directory.GetFiles(folder, "*.retry"));
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            MoveFixtureCopy(folder);
        });
        fixture.Handler.BlockImage = true;
        var source = fixture.Services.Settings.Sources.Single();
        var details = new EhViewerForPc.Core.Online.EhGalleryDetails { Gid = 42, Token = "0123456789", Pages = 4 };
        Task retry = fixture.Services.Downloads.RedownloadPageAsync(details, 2, fixture.Folder, source.RootPath, null, TestContext.Current.CancellationToken);
        await fixture.Handler.ImageStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task deleting = fixture.Services.DeleteLocalCopyAsync(fixture.Info, TestContext.Current.CancellationToken);
        Assert.False(entered.IsSet);
        fixture.Handler.Release.TrySetResult();
        await retry;
        for (int i = 0; i < 250 && !entered.IsSet; i++) await Task.Delay(20);
        Assert.True(entered.IsSet);
        Task? disposing = null;
        try
        {
            disposing = Task.Run(fixture.Services.Downloads.Dispose);
            await Task.Delay(50);
            Assert.False(disposing.IsCompleted);
        }
        finally { release.Set(); }
        await deleting;
        await disposing!;
        Assert.False(Directory.Exists(fixture.Folder));
    }

    [AvaloniaFact]
    public async Task RemovingAQueuedTaskStillWaitsForItsUnwindingWriterBeforeRecycling()
    {
        bool recycled = false;
        using var fixture = new ReaderRetryIntegrationTests.Fixture(folder => { recycled = true; MoveFixtureCopy(folder); });
        fixture.Handler.BlockImage = true;
        fixture.Handler.IgnoreImageCancellation = true;
        var source = fixture.Services.Settings.Sources.Single();
        var task = fixture.Services.Downloads.EnqueueRepair(new() { Gid = 42, Token = "0123456789", Pages = 4 }, fixture.Folder, source.RootPath);
        await fixture.Handler.ImageStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Services.Downloads.Remove(task, deleteFiles: false);
        Assert.Empty(fixture.Services.Downloads.Tasks);
        Task deleting = fixture.Services.DeleteLocalCopyAsync(fixture.Info, TestContext.Current.CancellationToken);
        try
        {
            await Task.Delay(50);
            Assert.False(recycled);
        }
        finally { fixture.Handler.Release.TrySetResult(); }
        await deleting;
        Assert.True(recycled);
    }

    [AvaloniaFact]
    public async Task DeletingTheLastCopyClearsLibraryAndVersionEmptyStates()
    {
        using var fixture = new ReaderRetryIntegrationTests.Fixture(MoveFixtureCopy);
        using var shell = new ShellViewModel(fixture.Services);
        await shell.Library.InitializeAsync();
        var item = shell.Library.SnapshotAllWorks().Single();
        using var versions = new VersionsViewModel(item, fixture.Services, shell.Library.ApplyScannedCopy);
        versions.Deletion.Request(item.Info);
        await versions.Deletion.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(shell.Library.Galleries);
        Assert.Empty(versions.Versions);
        Assert.Equal("本地库暂无作品", shell.Library.StatusText);
        Assert.Contains("0 个数据源", versions.SummaryText);
    }

    [AvaloniaFact]
    public async Task CancelledDeletionDoesNotChangeFilesOrDownloadState()
    {
        using var fixture = new ReaderRetryIntegrationTests.Fixture(_ => throw new Exception("回收不应执行"));
        var source = fixture.Services.Settings.Sources.Single();
        var task = fixture.Services.Downloads.EnqueueRepair(new() { Gid = 42, Token = "0123456789", Pages = 4 }, fixture.Folder, source.RootPath, false);
        var before = fixture.Snapshot();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Services.DeleteLocalCopyAsync(fixture.Info, cancellation.Token));
        Assert.Equal(DownloadState.Pending, task.State); Assert.Equal(before, fixture.Snapshot());
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DataSourceRootCannotBeDeleted(bool trailingSeparator)
    {
        using var fixture = new ReaderRetryIntegrationTests.Fixture(_ => throw new Exception("回收操作不应执行"));
        var source = fixture.Services.Settings.Sources.Single();
        await Assert.ThrowsAsync<IOException>(() => fixture.Services.DeleteLocalCopyAsync(new GalleryInfo { StableId = "fixture-root", FolderName = "root", Title = "fixture root", SourceId = source.Id, FolderPath = source.RootPath + (trailingSeparator ? Path.DirectorySeparatorChar.ToString() : string.Empty) }, TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(fixture.Folder));
    }

    [AvaloniaTheory]
    [InlineData(false, 920)] [InlineData(true, 920)] [InlineData(false, 740)] [InlineData(true, 740)]
    [InlineData(false, 640)] [InlineData(true, 640)] [InlineData(false, 540)] [InlineData(true, 540)]
    public void DeleteConfirmationShowsTheExactPathAndBindsBothCommands(bool light, int width)
    {
        using var fixture = new ReaderRetryIntegrationTests.Fixture();
        using var deletion = new LocalCopyDeletionViewModel(fixture.Services);
        deletion.Request(fixture.Info);
        var view = new LocalCopyDeletionView { DataContext = deletion };
        var window = new Window { Width = width, Height = 420, Content = view, RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var path = view.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => AutomationProperties.GetAutomationId(t) == "LocalDelete.Path");
            Assert.Equal(fixture.Folder, path.Text);
            var confirm = view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "LocalDelete.Confirm");
            var cancel = view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "LocalDelete.Cancel");
            Assert.Same(deletion.ConfirmCommand, confirm.Command); Assert.True(confirm.IsEffectivelyEnabled);
            Assert.True(confirm.Bounds.Right <= width);
            var point = cancel.TranslatePoint(new Point(cancel.Bounds.Width / 2, cancel.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.False(deletion.HasConfirmation); Assert.True(Directory.Exists(fixture.Folder));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task UnavailableDownloadMovesToItsOwnPageAndUnmarkRestoresIt()
    {
        using var fixture = new ReaderRetryIntegrationTests.Fixture();
        using var shell = new ShellViewModel(fixture.Services);
        var manager = fixture.Services.Downloads;
        var task = manager.Enqueue(new EhGalleryDetails { Gid = 90210, Token = "unavailable", Title = "合成不可用任务", Pages = 4 }, startImmediately: false);
        task.State = DownloadState.Failed;
        task.Message = EhParser.GalleryUnavailableMessage;
        manager.MarkGalleryUnavailable(task);

        shell.Downloads.SyncAll();
        Assert.Empty(shell.Downloads.Tasks);
        Assert.True(shell.Downloads.HasUnavailable);
        Assert.Contains("(1)", shell.Downloads.UnavailableTabTitle);
        var item = Assert.Single(shell.Downloads.UnavailableTasks);
        Assert.Equal(task.Id, item.Task.Id);

        await shell.Downloads.UnmarkUnavailable(item);
        shell.Downloads.SyncAll();
        Assert.False(task.GalleryUnavailable);
        Assert.Equal(DownloadState.Failed, task.State);
        Assert.Single(shell.Downloads.Tasks);
        Assert.Empty(shell.Downloads.UnavailableTasks);
        Assert.False(shell.Downloads.HasUnavailable);
    }
}

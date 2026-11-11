using EhViewerReader.Core.Download;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Online;
using EhViewerReader.Core.Storage;

namespace EhViewerReader.Core.Tests;

public sealed class DownloadRelocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EhReaderMove-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly EhClient _client;
    private string Source => Path.Combine(_root, "old");
    private string Target => Path.Combine(_root, "new");
    private string Relative => Path.Combine("label", "42-test");
    public DownloadRelocationTests()
    {
        _paths = new(Path.Combine(_root, "data")); _paths.EnsureCreated();
        _settings = new(); _settings.Download.RootPath = Source;
        _settings.AddSource(Source, "我的下载");
        Directory.CreateDirectory(Path.Combine(Source, Relative));
        File.WriteAllText(Path.Combine(Source, Relative, "00000001.jpg"), "synthetic bytes");
        new SettingsStore(_paths).Save(_settings, out _);
        JsonFileStore.Save(_paths.DownloadTasksFile, new DownloadFile
        {
            Tasks = [new() { Gid = 42, FolderPath = Path.Combine(Source, Relative), DestinationRoot = Source, State = DownloadState.Paused }],
            Archive = [new() { Gid = 42, FolderPath = Path.Combine(Source, Relative) }],
        }, StorageJsonContext.Default.DownloadFile, out _);
        _client = new(_settings);
    }
    [Fact]
    public async Task MoveUpdatesFilesTasksArchiveAndSourceWithoutChangingSourceIdentity()
    {
        string sourceId = _settings.Sources[0].Id;
        _settings.AddSource(Path.Combine(_root, "unrelated"), "备份");
        using var manager = new DownloadManager(_paths, _settings, _client);
        Assert.True(await manager.RelocateRootAsync(Target, null, CancellationToken.None));
        Assert.False(Directory.Exists(Source));
        Assert.Equal("synthetic bytes", File.ReadAllText(Path.Combine(Target, Relative, "00000001.jpg")));
        Assert.Equal(Path.Combine(Target, Relative), Assert.Single(manager.Tasks).FolderPath);
        Assert.Equal(Target, Assert.Single(manager.Tasks).DestinationRoot);
        Assert.Equal(DownloadState.Paused, Assert.Single(manager.Tasks).State);
        Assert.Equal(Path.Combine(Target, Relative), Assert.Single(manager.Archive).FolderPath);
        Assert.Equal(Target, _settings.Sources.Single(s => s.Id == sourceId).RootPath);
        Assert.Equal("我的下载", _settings.Sources.Single(s => s.Id == sourceId).Name);
        Assert.Equal(2, _settings.Sources.Count);
        Assert.Equal(Target, new SettingsStore(_paths).Load(out _).Download.RootPath);
        Assert.False(File.Exists(Path.Combine(_paths.BaseDirectory, "download-move.json")));
    }
    [Fact]
    public async Task NonemptyTargetAndOverlappingRootsLeaveEverythingIntact()
    {
        Directory.CreateDirectory(Target); File.WriteAllText(Path.Combine(Target, "keep.txt"), "keep");
        using var manager = new DownloadManager(_paths, _settings, _client);
        await Assert.ThrowsAsync<IOException>(() => manager.RelocateRootAsync(Target, null, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => manager.RelocateRootAsync(Path.Combine(Source, "inside"), null, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(Source, Relative, "00000001.jpg")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Target, "keep.txt")));
        Assert.Equal(Source, manager.RootPath);
    }
    [Fact]
    public async Task CrossVolumeCopyPathChecksBytesBeforeDeletingOriginals()
    {
        var move = await DownloadRootMover.PrepareAsync(_paths, Source, Target, null, CancellationToken.None, allowRename: false);
        Assert.True(Directory.Exists(Source));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Source, Relative, "00000001.jpg")), File.ReadAllBytes(Path.Combine(Target, Relative, "00000001.jpg")));
        move.Commit(); Assert.True(move.Cleanup());
        Assert.False(Directory.Exists(Source));
    }
    [Fact]
    public async Task CancelDuringCopyReturnsPartialBuffersAndKeepsOriginalDirectory()
    {
        using var cancel = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.Bytes > 0) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DownloadRootMover.PrepareAsync(_paths, Source, Target, progress, cancel.Token, allowRename: false));
        Assert.True(File.Exists(Path.Combine(Source, Relative, "00000001.jpg")));
        Assert.False(Directory.Exists(Target));
        Assert.Empty(Directory.EnumerateDirectories(_root, "new.ehr-move-*"));
    }

    [Fact]
    public async Task CancelCopyKeepsTheTargetFolderThatUserSelected()
    {
        Directory.CreateDirectory(Target);
        using var cancel = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.Bytes > 0) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DownloadRootMover.PrepareAsync(_paths, Source, Target, progress, cancel.Token, allowRename: false));
        Assert.True(Directory.Exists(Target));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Target));
        Assert.True(File.Exists(Path.Combine(Source, Relative, "00000001.jpg")));
    }

    [Fact]
    public async Task ExistingTargetFolderIsKeptThroughoutPreparationAndRollback()
    {
        Directory.CreateDirectory(Target);
        bool presentThroughout = true;
        var progress = new InlineProgress(_ => presentThroughout &= Directory.Exists(Target));
        var move = await DownloadRootMover.PrepareAsync(_paths, Source, Target, progress, CancellationToken.None);
        move.Rollback();
        Assert.True(presentThroughout);
        Assert.True(Directory.Exists(Target));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Target));
        Assert.True(File.Exists(Path.Combine(Source, Relative, "00000001.jpg")));
    }

    [Fact]
    public async Task RollingBackACopyKeepsFilesAddedByUserInTheTarget()
    {
        Directory.CreateDirectory(Target);
        var move = await DownloadRootMover.PrepareAsync(_paths, Source, Target, null, CancellationToken.None, allowRename: false);
        File.WriteAllText(Path.Combine(Target, "user-added.txt"), "user data");
        move.Rollback();
        Assert.Equal("user data", File.ReadAllText(Path.Combine(Target, "user-added.txt")));
        Assert.False(Directory.Exists(Path.Combine(Target, Relative)));
        Assert.True(File.Exists(Path.Combine(Source, Relative, "00000001.jpg")));
    }

    [Fact]
    public void RestartRollsBackPartialEntryMovesIntoAnExistingTarget()
    {
        Directory.CreateDirectory(Target);
        Directory.Move(Path.Combine(Source, "label"), Path.Combine(Target, "label"));
        JsonFileStore.Save(Path.Combine(_paths.BaseDirectory, "download-move.json"), new DownloadRootMoveJournal
        {
            Source = Source, Target = Target, Staging = Target + ".ehr-move-" + Guid.NewGuid().ToString("N"),
            TargetExisted = true, RenamedEntries = true, EntryNames = ["label"],
        }, StorageJsonContext.Default.DownloadRootMoveJournal, out _);
        using var recovered = new DownloadManager(_paths, _settings, _client);
        Assert.Equal(Source, recovered.RootPath);
        Assert.True(Directory.Exists(Target));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Target));
        Assert.True(File.Exists(Path.Combine(Source, Relative, "00000001.jpg")));
    }

    [Fact]
    public void SavingRootWithoutMovingAcceptsExistingFilesAndKeepsOldTaskPaths()
    {
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Target, "existing.txt"), "existing");
        using var manager = new DownloadManager(_paths, _settings, _client);
        using var archive = manager.AcquireArchiveWrite();
        manager.ChangeRootWithoutMoving(Target, registerAsSource: true);
        Assert.Equal(Target, manager.RootPath);
        Assert.Equal(Path.Combine(Source, Relative), Assert.Single(manager.Tasks).FolderPath);
        Assert.Equal(Source, Assert.Single(manager.Tasks).DestinationRoot);
        Assert.Equal(Path.Combine(Source, Relative), Assert.Single(manager.Archive).FolderPath);
        Assert.True(File.Exists(Path.Combine(Source, Relative, "00000001.jpg")));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(Target, "existing.txt")));
        Assert.Contains(_settings.Sources, s => s.RootPath == Source);
        Assert.Contains(_settings.Sources, s => s.RootPath == Target);
        var next = manager.Enqueue(new EhGalleryDetails { Gid = 99, Token = "fixture", Title = "New destination", Pages = 1 }, startImmediately: false);
        Assert.True(PathIdentity.IsDescendantOf(next.FolderPath, Target));
        Assert.Equal(Target, new SettingsStore(_paths).Load(out _).Download.RootPath);
        Assert.False(File.Exists(Path.Combine(_paths.BaseDirectory, "download-move.json")));
    }

    [Fact]
    public void FailedSaveWithoutMovingKeepsOriginalConfigurationAndFiles()
    {
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Target, "existing.txt"), "existing");
        using var manager = new DownloadManager(_paths, _settings, _client);
        Assert.Throws<IOException>(() => manager.ChangeRootWithoutMoving(Target, true, () => throw new IOException("synthetic save failure")));
        Assert.Equal(Source, manager.RootPath);
        Assert.Single(_settings.Sources);
        Assert.True(Directory.Exists(Target));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(Target, "existing.txt")));
        Assert.True(File.Exists(Path.Combine(Source, Relative, "00000001.jpg")));
    }

    [Fact]
    public void EmptyDefaultSourceIsReplacedWithoutDeletingItsDirectory()
    {
        string empty = Path.Combine(_root, "empty-default");
        Directory.CreateDirectory(empty);
        _settings.Download.RootPath = empty;
        _settings.AddSource(empty, "下载目录");
        using var manager = new DownloadManager(_paths, _settings, _client);
        manager.ChangeRootWithoutMoving(Target, true);
        Assert.DoesNotContain(_settings.Sources, s => s.RootPath == empty);
        Assert.Contains(_settings.Sources, s => s.RootPath == Target && s.Name == "下载目录");
        Assert.Contains(_settings.Sources, s => s.RootPath == Source);
        Assert.True(Directory.Exists(empty));
    }
    [Fact]
    public async Task RestartCompletesMetadataAfterFilesWereMoved()
    {
        await DownloadRootMover.PrepareAsync(_paths, Source, Target, null, CancellationToken.None);
        using var recovered = new DownloadManager(_paths, _settings, _client);
        Assert.Equal(Target, recovered.RootPath);
        Assert.Equal(Path.Combine(Target, Relative), Assert.Single(recovered.Tasks).FolderPath);
        Assert.True(File.Exists(Path.Combine(Target, Relative, "00000001.jpg")));
        Assert.False(File.Exists(Path.Combine(_paths.BaseDirectory, "download-move.json")));
    }
    [Fact]
    public async Task FailedMetadataSaveRollsBackPathsAndFiles()
    {
        using var manager = new DownloadManager(_paths, _settings, _client);
        int saves = 0;
        await Assert.ThrowsAsync<IOException>(() => manager.RelocateRootAsync(Target, null, CancellationToken.None, () => { if (++saves == 1) throw new IOException("synthetic failure"); }));
        Assert.Equal(Source, manager.RootPath);
        Assert.Equal(Path.Combine(Source, Relative), Assert.Single(manager.Tasks).FolderPath);
        Assert.True(File.Exists(Path.Combine(Source, Relative, "00000001.jpg")));
        Assert.False(Directory.Exists(Target));
    }
    [Fact]
    public async Task ActiveArchiveWriterBlocksMigrationBeforeAnyFilesMove()
    {
        using var manager = new DownloadManager(_paths, _settings, _client);
        using var lease = manager.AcquireArchiveWrite();
        await Assert.ThrowsAsync<IOException>(() => manager.RelocateRootAsync(Target, null, CancellationToken.None));
        Assert.True(Directory.Exists(Source)); Assert.False(Directory.Exists(Target));
    }
    [Fact]
    public async Task PersistentMetadataFailureKeepsValidRuntimePathsAndCanRecover()
    {
        using (var manager = new DownloadManager(_paths, _settings, _client))
        {
            await Assert.ThrowsAsync<IOException>(() => manager.RelocateRootAsync(Target, null, CancellationToken.None, () => throw new IOException("synthetic persistent failure")));
            Assert.Equal(Target, manager.RootPath);
            Assert.True(Directory.Exists(Assert.Single(manager.Tasks).FolderPath));
        }
        var restartedSettings = new SettingsStore(_paths).Load(out _);
        using var recovered = new DownloadManager(_paths, restartedSettings, _client);
        Assert.Equal(Target, recovered.RootPath);
        Assert.True(File.Exists(Path.Combine(Target, Relative, "00000001.jpg")));
        Assert.False(File.Exists(Path.Combine(_paths.BaseDirectory, "download-move.json")));
    }
    private sealed class InlineProgress(Action<DownloadMoveProgress> report) : IProgress<DownloadMoveProgress> { public void Report(DownloadMoveProgress value) => report(value); }
    public void Dispose() { _client.Dispose(); Directory.Delete(_root, true); }
}

using EhViewerReader.Core.Models;
using EhViewerReader.Core.Storage;
using EhViewerReader.Services;
using EhViewerReader.ViewModels;

namespace EhViewerReader.Core.Tests;

public sealed class ApplicationStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EhReaderState-" + Guid.NewGuid().ToString("N"));
    private readonly AppServices _services;
    public ApplicationStateTests()
    {
        var paths = new AppPaths(Path.Combine(_root, "data"));
        paths.EnsureCreated();
        var settings = new AppSettings();
        settings.Download.RootPath = Path.Combine(_root, "downloads");
        settings.Download.AddAsSource = false;
        new SettingsStore(paths).Save(settings, out _);
        _services = AppServices.Initialize(paths);
    }

    [Fact]
    public void CachedEditorDoesNotEraseRuntimeChanges()
    {
        var editor = new SettingsViewModel(_services);
        _services.Settings.AddSource(Path.Combine(_root, "added"));
        _services.Settings.DefaultReadingMode = ReadingMode.Continuous;
        _services.Settings.ImageQuality = ImageQuality.Resampled;
        editor.Apply();
        Assert.Single(_services.Settings.Sources);
        Assert.Equal(ReadingMode.Continuous, _services.Settings.DefaultReadingMode);
        Assert.Equal(ImageQuality.Resampled, _services.Settings.ImageQuality);
        editor.ModeIndex = (int)ReadingMode.Double;
        editor.Apply();
        Assert.Equal(ReadingMode.Double, _services.Settings.DefaultReadingMode);
    }

    [Fact]
    public void UnsavedSourceDraftDoesNotMutateRegisteredSources()
    {
        var editor = new SettingsViewModel(_services);
        editor.AddSource(Path.Combine(_root, "draft"));
        Assert.Empty(_services.Settings.Sources);
        editor.RefreshSources();
        Assert.Single(editor.Sources);
        editor.Apply();
        Assert.Single(_services.Settings.Sources);
    }

    [Fact]
    public void OnlineHistoryAndHiddenRecordsPreserveProgressAndFavorite()
    {
        _services.Works.Upsert(new WorkRecord { WorkId = "42", Gid = 42, Token = "abc", Title = "Online work", PageCount = 10 });
        _services.Progress.RecordRead("42", 4, 3, 0.3, 10, false);
        _services.Progress.ToggleFavorite("42");
        using var library = new MainViewModel(_services);
        using var history = new HistoryViewModel(_services, library);
        var entry = Assert.Single(history.Items);
        Assert.Equal("Online work", entry.Title);
        entry.RemoveCommand.Execute(null);
        Assert.Empty(history.Items);
        var saved = _services.Progress.Get("42")!;
        Assert.Equal(4, saved.PageNumber);
        Assert.True(saved.Favorite);
        _services.Progress.RecordRead("42", 5, 4, 0, 10, false);
        history.Reload();
        Assert.Single(history.Items);
    }

    [Fact]
    public void NativeScratchIsRejectedBeforeAllocationAndReservationsRelease()
    {
        long before = ImageMemoryBudget.UsedBytes;
        long limit = ImageMemoryBudget.LimitBytes;
        Assert.Throws<InvalidOperationException>(() => ImageMemoryBudget.Reserve(limit, scratch: true));
        using (ImageMemoryBudget.Reserve(1024 * 1024)) Assert.Equal(before + 1024 * 1024, ImageMemoryBudget.UsedBytes);
        Assert.Equal(before, ImageMemoryBudget.UsedBytes);
    }

    [Fact]
    public void CacheEvictionRemovesOldestAcrossBothDirectories()
    {
        string old = Path.Combine(_services.Paths.OnlineCacheDirectory, "old.img");
        string recent = Path.Combine(_services.Paths.ThumbnailCacheDirectory, "recent.img");
        File.WriteAllBytes(old, new byte[64]); File.WriteAllBytes(recent, new byte[64]);
        File.SetLastAccessTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        File.SetLastAccessTimeUtc(recent, DateTime.UtcNow);
        _services.OnlineImages.PruneCache(TimeSpan.FromDays(14), 64);
        Assert.False(File.Exists(old)); Assert.True(File.Exists(recent));
    }
    [Fact]
    public void MarkUnreadPreservesFavoriteAndDoesNotAppearInHistory()
    {
        _services.Progress.RecordRead("42", 4, 3, 0, 10, false);
        _services.Progress.ToggleFavorite("42");
        _services.Progress.MarkUnread("42");
        var progress = _services.Progress.Get("42")!;
        Assert.Equal(default, progress.LastReadUtc);
        Assert.True(progress.Favorite);
        Assert.True(progress.HiddenFromHistory);
    }

    [Fact]
    public void CompactWindowSizeSurvivesSettingsRoundTrip()
    {
        var store = new SettingsStore(_services.Paths);
        var settings = new AppSettings { WindowWidth = 600, WindowHeight = 480 };
        Assert.True(store.Save(settings, out _));
        var restored = store.Load(out _);
        Assert.Equal(600, restored.WindowWidth);
        Assert.Equal(480, restored.WindowHeight);
    }

    [Fact]
    public void CompressedDefaultsAndImageSizeChoiceSurviveSaving()
    {
        Assert.Equal(ImageQuality.Resampled, new AppSettings().ImageQuality);
        var legacy = new AppSettings { SchemaVersion = 4, ImageQuality = ImageQuality.Original };
        Assert.Equal(ImageQuality.Resampled, legacy.Normalize().ImageQuality);
        var editor = new SettingsViewModel(_services) { ResampledWidthIndex = 3 };
        editor.Apply();
        Assert.Equal(1600, _services.Settings.ResampledWidth);
        Assert.Equal(ImageQuality.Resampled, _services.Settings.ImageQuality);
    }

    [Fact]
    public void SettingsLoginStateUsesCurrentSessionInsteadOfStaticLoginGuidance()
    {
        var editor = new SettingsViewModel(_services);
        Assert.False(editor.IsSignedIn);
        _services.Online.Session.Import(new Dictionary<string, string> { ["ipb_member_id"] = "42", ["ipb_pass_hash"] = "synthetic" });
        editor.RefreshAccount();
        Assert.True(editor.IsSignedIn);
        Assert.Contains("已登录", editor.AccountStatus);
        Assert.DoesNotContain("推荐使用网页登录", editor.AccountStatus);
        _services.Online.SignOut(); editor.RefreshAccount();
        Assert.False(editor.IsSignedIn);
        Assert.Contains("未登录", editor.AccountStatus);
    }

    [Fact]
    public async Task SettingsSaveMovesExistingFilesAndRefreshesTheSameSourceRow()
    {
        string oldRoot = _services.Downloads.RootPath;
        Directory.CreateDirectory(Path.Combine(oldRoot, "label", "42-work"));
        File.WriteAllText(Path.Combine(oldRoot, "label", "42-work", "page.bin"), "synthetic");
        var source = _services.Settings.AddSource(oldRoot, "下载目录");
        var editor = new SettingsViewModel(_services);
        string destination = Path.Combine(_root, "moved");
        editor.DownloadDirectoryMode = 1;
        editor.SetDownloadRoot(destination);
        Assert.Equal("保存并搬迁下载", editor.SaveLabel);
        Assert.True(await editor.ApplyAsync(), editor.DownloadStatus);
        Assert.False(editor.IsSaving);
        Assert.False(editor.IsDownloadRootChanged);
        Assert.Equal(destination, _services.Downloads.RootPath);
        Assert.Equal(destination, editor.Sources.Single(s => s.Source.Id == source.Id).RootPath);
        Assert.Equal("synthetic", File.ReadAllText(Path.Combine(destination, "label", "42-work", "page.bin")));
        Assert.False(Directory.Exists(oldRoot));
    }

    [Fact]
    public async Task SettingsCanSaveNewRootWithoutMovingAndKeepBothSources()
    {
        string oldRoot = _services.Downloads.RootPath;
        Directory.CreateDirectory(Path.Combine(oldRoot, "old-work"));
        File.WriteAllText(Path.Combine(oldRoot, "old-work", "page.bin"), "old data");
        _services.Settings.AddSource(oldRoot, "下载目录");
        var editor = new SettingsViewModel(_services) { DownloadAddAsSource = true };
        string target = Path.Combine(_root, "existing-library");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "existing.bin"), "existing data");
        editor.SetDownloadRoot(target);
        Assert.Equal(0, editor.DownloadDirectoryMode);
        Assert.Equal("保存设置（不搬迁）", editor.SaveLabel);
        Assert.True(await editor.ApplyAsync(), editor.DownloadStatus);
        Assert.Null(editor.LastMovedRoot);
        Assert.Equal(oldRoot, editor.PreviousDownloadRoot);
        Assert.Equal(target, _services.Downloads.RootPath);
        Assert.Equal("old data", File.ReadAllText(Path.Combine(oldRoot, "old-work", "page.bin")));
        Assert.Equal("existing data", File.ReadAllText(Path.Combine(target, "existing.bin")));
        Assert.Contains(editor.Sources, s => s.RootPath == oldRoot && s.DisplayName == "旧下载目录");
        Assert.Contains(editor.Sources, s => s.RootPath == target && s.DisplayName == "下载目录");
    }

    [Fact]
    public void CachedPageRefreshesEffectiveImageSettingsWithoutDiscardingDrafts()
    {
        var editor = new SettingsViewModel(_services);
        _services.Settings.ResampledWidth = 2400;
        _services.Settings.ImageQuality = ImageQuality.Original;
        _services.Settings.Download.AddAsSource = true;
        editor.RefreshSources();
        Assert.Equal(4, editor.ResampledWidthIndex);
        Assert.Equal(0, editor.ImageQualityIndex);
        Assert.True(editor.DownloadAddAsSource);
        editor.ResampledWidthIndex = 0;
        _services.Settings.ResampledWidth = 1600;
        editor.RefreshSources();
        Assert.Equal(0, editor.ResampledWidthIndex);
    }

    public void Dispose() { _services.Dispose(); Directory.Delete(_root, true); }

    [Fact]
    public async Task LocalDetailsUseCachedMetadataAndKeepExactTagSearchTerms()
    {
        var details = new Core.Online.EhGalleryDetails { Gid = 42, Token = "token", Title = "Cached title", Pages = 3 };
        details.Tags.Add(new() { Namespace = "artist", Name = "test artist" });
        _services.Works.Upsert(WorkRecord.FromDetails(details));
        var info = new GalleryInfo { StableId = "42", WorkId = "42", Gid = 42, Title = "Local title", FolderName = "42-Local", FolderPath = _root, PageCount = 1, ExpectedPages = 3 };
        var item = new GalleryItemViewModel(Core.Scanning.LibraryMerger.Group([info]).Single(),
            g => new CoverImageRequest(g, _services.Covers), null);
        using var vm = new LocalGalleryDetailsViewModel(item, _services);
        string? query = null;
        vm.TagSearchRequested += (_, term) => query = term;
        await vm.InitializeAsync();
        Assert.False(vm.Presentation.IsLoading);
        Assert.Equal("Cached title", vm.Presentation.Title);
        Assert.Equal("1/3 页", vm.Presentation.PagesText);
        var tag = Assert.Single(vm.Presentation.Tags);
        tag.Command!.Execute(null);
        Assert.Equal("artist:test artist", query);
    }

    [Fact]
    public void ReadingAnOfflineCopyDoesNotEraseCachedTagsOrExposeMutableSnapshots()
    {
        var details = new Core.Online.EhGalleryDetails { Gid = 42, Token = "token", Title = "Cached title", Pages = 3 };
        details.Tags.Add(new() { Namespace = "language", Name = "chinese" });
        _services.Works.Upsert(WorkRecord.FromDetails(details));
        _services.Works.Upsert(new WorkRecord { WorkId = "42", Gid = 42, Title = "Local title", PageCount = 1 });
        var snapshot = _services.Works.Get("42")!;
        Assert.Equal("token", snapshot.Token);
        Assert.Equal("language:chinese", Assert.Single(snapshot.Metadata!.Tags));
        snapshot.Metadata.Tags[0] = "modified";
        Assert.Equal("language:chinese", Assert.Single(_services.Works.Get("42")!.Metadata!.Tags));
    }

    [Fact]
    public void ReadingTheLastDownloadedPageDoesNotCompleteAnUnfinishedWork()
    {
        string folder = Path.Combine(_root, "partial");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "00000001.jpg"), [0xFF, 0xD8, 0xFF, 0xE0]);
        var info = new GalleryInfo { StableId = "42", WorkId = "42", Gid = 42, Title = "Partial",
            FolderName = "partial", FolderPath = folder, PageCount = 1, ExpectedPages = 3, IsIncomplete = true };
        using var reader = new ReaderViewModel(_services, info);
        reader.SaveProgress();
        var progress = _services.Progress.Get("42")!;
        Assert.False(progress.Completed);
        Assert.Equal(3, progress.PageCountAtSave);
        Assert.Equal(3, _services.Works.Get("42")!.PageCount);
    }

    [Fact]
    public void HistoryWithoutReadablePagesDoesNotPretendToBeComplete()
    {
        using var library = new MainViewModel(_services);
        using var history = new HistoryViewModel(_services, library);
        var entry = new HistoryEntryViewModel(null, new WorkRecord { WorkId = "empty", Title = "Empty", PageCount = 0 },
            new ReadingProgress { PageNumber = 1, Completed = true }, _services, history);
        Assert.Equal("无可读页面", entry.ProgressDetail);
        Assert.Equal(0, entry.ProgressFraction);
        Assert.False(entry.OpenCommand.CanExecute(null));
    }
}

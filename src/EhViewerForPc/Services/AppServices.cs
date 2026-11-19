using EhViewerForPc.Core.Download;
using System.Text.Json;
using EhViewerForPc.Core.Imaging;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Core.Scanning;
using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Services;

/// <summary>
/// Application-wide service container. Created once on the UI thread before the
/// main window is shown and disposed when the application exits.
/// </summary>
public sealed partial class AppServices : IDisposable
{
    private bool _disposed;
    private readonly object _settingsSaveGate = new();
    private long _settingsRevision;

    private AppServices(AppPaths paths, AppSettings settings, Func<AppSettings, EhClient>? createOnline = null, Action<string>? recycleDirectory = null)
    {
        _recycleDirectory = recycleDirectory ?? WindowsRecycleBin.MoveDirectory;
        Paths = paths;
        Settings = settings;
        Log = new LogService(paths.LogDirectory);
        SettingsStore = new SettingsStore(paths);
        Writes = new DeferredSaveQueue();
        Progress = new ProgressStore(paths);
        Works = new WorkCatalog(paths, Writes);
        CacheStore = new LibraryCacheStore(paths);
        Scanner = new LibraryScanner();
        Decoder = new ImageDecoder(Log);
        Images = new ImageLoader(Decoder, Log);
        Covers = new CoverService(Images, Log, Paths.CoverCacheDirectory);
        Dimensions = new PageDimensionService(paths, Decoder, Log);
        Online = createOnline?.Invoke(settings) ?? new EhClient(settings, message => Log.Warn(message));
        PageImages = new GalleryImageStore(paths, Online);
        OnlineImages = new OnlineImageService(paths, Online, Images, Log, settings, PageImages);
        TagTranslation = new TagTranslationService(paths, settings, Online, Log);
        Downloads = new DownloadManager(paths, settings, Online, message => Log.Warn(message), PageImages);
        ImageSearch = new LocalImageSearchService(this);
        PageImages.FindLocalPage = Downloads.FindPage;
        ApplySettings(settings);
    }

    public static AppServices Current { get; private set; } = null!;

    public AppPaths Paths { get; }
    public AppSettings Settings { get; private set; }
    public LogService Log { get; }
    public SettingsStore SettingsStore { get; }
    public ProgressStore Progress { get; }
    public DeferredSaveQueue Writes { get; }
    public WorkCatalog Works { get; }
    public LibraryCacheStore CacheStore { get; }
    public LibraryScanner Scanner { get; }
    public ImageDecoder Decoder { get; }
    public ImageLoader Images { get; }
    public CoverService Covers { get; }
    public PageDimensionService Dimensions { get; }

    /// <summary>E-Hentai / ExHentai client (browsing, search, account).</summary>
    public EhClient Online { get; }

    /// <summary>Thumbnail and page cache for online content.</summary>
    public OnlineImageService OnlineImages { get; }
    public GalleryImageStore PageImages { get; }

    /// <summary>EhTagTranslation database (lazy, updatable from settings).</summary>
    public TagTranslationService TagTranslation { get; }

    /// <summary>Independent download engine.</summary>
    public DownloadManager Downloads { get; }

    /// <summary>Gets the lazy managed local image retrieval service.</summary>
    public LocalImageSearchService ImageSearch { get; }

    public static AppServices Initialize(AppPaths? paths = null)
        => InitializeCore(paths ?? new AppPaths(), null);

    internal static AppServices Initialize(AppPaths paths, Func<AppSettings, EhClient> createOnline, Action<string>? recycleDirectory = null)
        => InitializeCore(paths, createOnline, recycleDirectory);

    private static AppServices InitializeCore(AppPaths paths, Func<AppSettings, EhClient>? createOnline, Action<string>? recycleDirectory = null)
    {
        paths.EnsureCreated();
        var store = new SettingsStore(paths);
        AppSettings settings = store.Load(out string? error);
        var services = new AppServices(paths, settings, createOnline, recycleDirectory);
        if (error is not null)
        {
            services.Log.Warn("Settings load failed: " + error);
        }

        Current = services;
        return services;
    }

    public void ApplySettings(AppSettings settings)
    {
        Settings = settings;
        if (!settings.ImageSearch.Enabled) ImageSearch?.Cancel();
        ImageSearch?.ApplySettings();
        ImageMemoryBudget.Configure(settings.PageCacheMegabytes);
        Images.MaxMegapixels = settings.MaxDecodeMegapixels;
        Images.MaxDimension = settings.MaxDecodeDimension;
        Images.CoverDecodeWidth = settings.CoverDecodeWidth;

        // A smaller disk budget must take effect immediately, not after the next
        // batch of writes.
        OnlineImages.ApplyPolicy();
    }

    public bool TrySaveSettings(out string? error)
    {
        ApplySettings(Settings);
        // Capture on the caller/UI thread; serialize shared mutable settings only here.
        AppSettings snapshot = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(Settings, StorageJsonContext.Default.AppSettings), StorageJsonContext.Default.AppSettings)!;
        long revision = Interlocked.Increment(ref _settingsRevision);
        Writes.Schedule("settings", () => { lock (_settingsSaveGate) { if (revision != _settingsRevision) return; if (!SettingsStore.Save(snapshot, out var failure)) Log.Warn("Settings save failed: " + failure); } });
        error = null;
        return true;
    }

    public Task<bool> RelocateDownloadsAsync(string target, IProgress<DownloadMoveProgress>? progress, CancellationToken token) =>
        Task.Run(() => Downloads.RelocateRootAsync(target, progress, token, () =>
        {
            lock (_settingsSaveGate)
            {
                Interlocked.Increment(ref _settingsRevision);
                if (!SettingsStore.Save(Settings, out var error)) throw new IOException(error);
            }
        }), token);

    public Task ChangeDownloadRootWithoutMovingAsync(string target, bool registerAsSource) => Task.Run(() =>
        Downloads.ChangeRootWithoutMoving(target, registerAsSource, () =>
        {
            lock (_settingsSaveGate)
            {
                Interlocked.Increment(ref _settingsRevision);
                if (!SettingsStore.Save(Settings, out var error)) throw new IOException(error);
            }
        }));

    /// <summary>Rebuilds the HTTP stacks after proxy/DNS/hosts changes.</summary>
    public void ReloadNetwork()
    {
        Online.ReloadNetwork();
    }

    /// <summary>
    /// Registers the download root as a library source so finished downloads
    /// appear in the local library without any extra setup.
    /// </summary>
    public LibrarySource? EnsureDownloadSource()
    {
        if (!Settings.Download.AddAsSource) return null;

        string root = Downloads.RootPath;
        return TryCreateDownloadDirectory(root) ? RegisterDownloadSource(root) : null;
    }

    public async Task<LibrarySource?> EnsureDownloadSourceAsync(CancellationToken token)
    {
        if (!Settings.Download.AddAsSource) return null;

        string root = Downloads.RootPath;
        bool created = await Task.Run(() => TryCreateDownloadDirectory(root), token);
        token.ThrowIfCancellationRequested();
        if (!created || !Settings.Download.AddAsSource || !PathIdentity.Comparer.Equals(root, Downloads.RootPath)) return null;
        return RegisterDownloadSource(root);
    }

    private bool TryCreateDownloadDirectory(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Create download root failed: " + root + " | " + ex.Message);
            return false;
        }
    }

    private LibrarySource RegisterDownloadSource(string root)
    {
        LibrarySource source = Settings.AddSource(root, "下载目录");
        TrySaveSettings(out _);
        return source;
    }

    public bool TrySaveProgress(out string? error)
    {
        Writes.Schedule("progress", () => { if (!Progress.Save(out var failure)) Log.Warn("Progress save failed: " + failure); });
        error = null;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Progress.Save(out _);
        }
        catch
        {
            // Best effort on shutdown.
        }

        Downloads.Dispose();
        ImageSearch.Dispose();
        Writes.Dispose();
        Online.Dispose();
        Covers.Dispose();
        Decoder.Dispose();
    }
}

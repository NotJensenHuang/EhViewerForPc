using EhViewerReader.Core.Download;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Online;
using EhViewerReader.Core.Scanning;
using EhViewerReader.Core.Storage;

namespace EhViewerReader.Services;

/// <summary>
/// Application-wide service container. Created once on the UI thread before the
/// main window is shown and disposed when the application exits.
/// </summary>
public sealed class AppServices : IDisposable
{
    private bool _disposed;

    private AppServices(AppPaths paths, AppSettings settings)
    {
        Paths = paths;
        Settings = settings;
        Log = new LogService(paths.LogDirectory);
        SettingsStore = new SettingsStore(paths);
        Progress = new ProgressStore(paths);
        CacheStore = new LibraryCacheStore(paths);
        Scanner = new LibraryScanner();
        Decoder = new ImageDecoder(Log);
        Images = new ImageLoader(Decoder, Log);
        Covers = new CoverService(Images, Log, Paths.CoverCacheDirectory);
        Dimensions = new PageDimensionService(paths, Decoder, Log);
        Online = new EhClient(settings, message => Log.Warn(message));
        OnlineImages = new OnlineImageService(paths, Online, Images, Log);
        TagTranslation = new TagTranslationService(paths, settings, Online, Log);
        Downloads = new DownloadManager(paths, settings, Online, message => Log.Warn(message));
        ApplySettings(settings);
    }

    public static AppServices Current { get; private set; } = null!;

    public AppPaths Paths { get; }
    public AppSettings Settings { get; private set; }
    public LogService Log { get; }
    public SettingsStore SettingsStore { get; }
    public ProgressStore Progress { get; }
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

    /// <summary>EhTagTranslation database (lazy, updatable from settings).</summary>
    public TagTranslationService TagTranslation { get; }

    /// <summary>Independent download engine.</summary>
    public DownloadManager Downloads { get; }

    public static AppServices Initialize(AppPaths? paths = null)
    {
        paths ??= new AppPaths();
        paths.EnsureCreated();
        var store = new SettingsStore(paths);
        AppSettings settings = store.Load(out string? error);
        var services = new AppServices(paths, settings);
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
        Images.MaxMegapixels = settings.MaxDecodeMegapixels;
        Images.MaxDimension = settings.MaxDecodeDimension;
    }

    public bool TrySaveSettings(out string? error)
    {
        ApplySettings(Settings);
        return SettingsStore.Save(Settings, out error);
    }

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
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Create download root failed: " + root + " | " + ex.Message);
            return null;
        }

        LibrarySource source = Settings.AddSource(root, "下载目录");
        TrySaveSettings(out _);
        return source;
    }

    public bool TrySaveProgress(out string? error) => Progress.Save(out error);

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
        Online.Dispose();
        Covers.Dispose();
        Decoder.Dispose();
    }
}

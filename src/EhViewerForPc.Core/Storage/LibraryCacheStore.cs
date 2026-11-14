using EhViewerForPc.Core.Models;

namespace EhViewerForPc.Core.Storage;

public sealed class LibraryCacheStore
{
    private readonly AppPaths _paths;
    private readonly object _gate = new();

    public LibraryCacheStore(AppPaths paths)
    {
        _paths = paths;
    }

    public LibraryCache Load(string rootPath, out string? error) =>
        Load("default", rootPath, out error);

    public LibraryCache Load(string sourceId, string rootPath, out string? error)
    {
        string normalizedRoot = Path.GetFullPath(rootPath);
        string file = _paths.GetLibraryCacheFile(sourceId);

        // Adopt the schema-1 single-library cache the first time a source is
        // loaded, so upgrading does not force a full rescan of the old root.
        if (!File.Exists(file) && File.Exists(_paths.LegacyLibraryCacheFile))
        {
            file = _paths.LegacyLibraryCacheFile;
        }

        var cache = JsonFileStore.LoadOrDefault(
            file, StorageJsonContext.Default.LibraryCache, () => new LibraryCache(), out error);
        // schema 3 与 4 的数据结构相同，只是缺页诊断语义不同；保留条目，扫描器定向重查。
        if (cache.SchemaVersion is not (3 or LibraryCache.CurrentSchemaVersion) ||
            !PathIdentity.Comparer.Equals(cache.RootPath, normalizedRoot))
        {
            cache = new LibraryCache { RootPath = normalizedRoot };
        }

        cache.Entries = new Dictionary<string, GalleryCacheEntry>(cache.Entries, PathIdentity.Comparer);
        return cache;
    }

    public bool Save(LibraryCache cache, out string? error) => Save("default", cache, out error);

    public bool Save(string sourceId, LibraryCache cache, out string? error)
    {
        lock (_gate)
        {
            // 未经扫描的旧条目仍需保留旧语义标记；扫描器输出的新缓存才升级为当前版本。
            if (cache.SchemaVersion != 3) cache.SchemaVersion = LibraryCache.CurrentSchemaVersion;
            cache.SavedUtc = DateTime.UtcNow;
            return JsonFileStore.Save(
                _paths.GetLibraryCacheFile(sourceId), cache, StorageJsonContext.Default.LibraryCache, out error);
        }
    }

    public void Delete(string sourceId)
    {
        lock (_gate)
        {
            try
            {
                string file = _paths.GetLibraryCacheFile(sourceId);
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort; a stale cache is invalidated by the signature check.
            }
        }
    }
}

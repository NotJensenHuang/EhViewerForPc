using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EhViewerForPc.Core.Format;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.ImageSearch;
using Microsoft.Data.Sqlite;

namespace EhViewerForPc.Services;

/// <summary>Captures immutable gallery scalars before leaving the UI thread.</summary>
public sealed record ImageSearchGallery(string SourceId, string StableId, string WorkId, string Folder);
/// <summary>Reports the current index job without exposing backend details in the search flow.</summary>
public sealed record LocalImageSearchStatus(string Message, int Processed = 0, int Total = 0, int Indexed = 0, int Skipped = 0, int Failed = 0, int Reused = 0);
/// <summary>Identifies an exact physical page and its owning local copy.</summary>
public sealed record LocalImageSearchHit(GalleryInfo Gallery, int PageNumber, string FilePath, long Length, long MtimeTicks, float Score, string GenerationId);
/// <summary>Requests explicit navigation to an original page number in a concrete copy.</summary>
public sealed record LocalPageReadRequest(GalleryInfo Gallery, int PageNumber);
internal sealed record ImageSearchPointer(int SchemaVersion, string GenerationId, string ProfileId, string ManifestSha256, int Pages, DateTime PublishedUtc);

/// <summary>Builds and queries immutable local generations using the managed backend on background threads.</summary>
public sealed class LocalImageSearchService : IDisposable
{
    private readonly AppServices _services;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _activeJob;
    private readonly object _generationGate = new();
    private readonly Dictionary<string, int> _activeReaders = new(StringComparer.Ordinal);
    private string? _configurationIdentity;

    /// <summary>Initializes a lazy service without touching index or gallery files.</summary>
    public LocalImageSearchService(AppServices services) => _services = services;
    /// <summary>Gets whether this instance currently owns a build job.</summary>
    public bool IsBuilding => _activeJob is not null;
    /// <summary>Gets the configured index root without creating it.</summary>
    public string DirectoryPath => string.IsNullOrWhiteSpace(_services.Settings.ImageSearch.StorageDirectory)
        ? Path.Combine(_services.Paths.BaseDirectory, "image-search") : Path.GetFullPath(_services.Settings.ImageSearch.StorageDirectory);
    /// <summary>Stops the current owned job at a cancellation checkpoint.</summary>
    public void Cancel() => _activeJob?.Cancel();
    /// <summary>Cancels a job whose source or storage configuration is no longer current.</summary>
    public void ApplySettings()
    {
        if (!_services.Settings.ImageSearch.Enabled) { Cancel(); _configurationIdentity = "disabled"; return; }
        try
        {
        string identity = DirectoryPath + "\n" + _services.Settings.ImageSearch.Enabled + "\n" + string.Join("|", _services.Settings.ImageSearch.SourceIds.Order(StringComparer.Ordinal))
            + "\n" + string.Join("\n", _services.Settings.Sources.Select(s => s.Id + "|" + s.Enabled + "|" + s.RootPath).Order(StringComparer.Ordinal));
        if (_configurationIdentity is not null && _configurationIdentity != identity) Cancel();
        _configurationIdentity = identity;
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { Cancel(); _configurationIdentity = null; }
    }

    /// <summary>Reads index availability without enumerating any library pages.</summary>
    public Task<LocalImageSearchStatus> GetStatusAsync(CancellationToken token = default)
    {
        if (!_services.Settings.ImageSearch.Enabled) return Task.FromResult(new LocalImageSearchStatus("尚未启用以图搜图"));
        string root = DirectoryPath;
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var pointer = LoadAvailablePointer(root);
            return pointer is null ? new LocalImageSearchStatus("尚未建立图片索引") : new LocalImageSearchStatus($"已索引 {pointer.Pages:N0} 页", Indexed: pointer.Pages);
        }, token);
    }

    /// <summary>Validates index placement, including all existing reparse-point ancestors.</summary>
    public static void ValidateStorage(string root, IEnumerable<LibrarySource> sources)
    {
        root = Path.GetFullPath(root);
        foreach (var source in sources)
        {
            if (IsInside(root, source.RootPath) || IsInside(source.RootPath, root)) throw new InvalidOperationException("图片索引目录不能与图库目录相互包含。");
            for (DirectoryInfo? info = new(source.RootPath); info is not null; info = info.Parent)
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("图片检索的数据源请使用实际路径，不能经过链接或目录联接。");
        }
        for (DirectoryInfo? info = new(root); info is not null; info = info.Parent)
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("图片索引目录不能经过链接或目录联接。");
    }

    /// <summary>Builds a fresh generation, keeping the last published index available until atomic publication.</summary>
    public async Task<LocalImageSearchStatus> BuildAsync(IReadOnlyList<ImageSearchGallery> galleries, IProgress<LocalImageSearchStatus>? progress, CancellationToken token, bool rebuild = false)
    {
        if (!_services.Settings.ImageSearch.Enabled) throw new InvalidOperationException("请先在设置中启用以图搜图。");
        if (!await _writer.WaitAsync(0, token)) throw new InvalidOperationException("图片索引正在更新。");
        using var job = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        _activeJob = job;
        string root = DirectoryPath;
        var sources = SelectedSources();
        var allSources = _services.Settings.Sources.Select(s => s.Clone()).ToArray();
        try
        {
            return await Task.Run(() => Build(root, galleries.ToArray(), sources, allSources, progress, job.Token, rebuild), job.Token);
        }
        finally { _activeJob = null; _writer.Release(); }
    }

    private LocalImageSearchStatus Build(string root, ImageSearchGallery[] galleries, LibrarySource[] sources, LibrarySource[] allSources, IProgress<LocalImageSearchStatus>? progress, CancellationToken token, bool rebuild)
    {
        ValidateStorage(root, allSources);
        if (sources.Length == 0 || galleries.Length == 0) throw new InvalidOperationException("请先添加本地数据源并扫描图库。");
        var selected = sources.ToDictionary(s => s.Id, StringComparer.Ordinal);
        if (sources.Any(s => !Directory.Exists(s.RootPath))) throw new IOException("选中的数据源离线，当前索引已保留。");
        Directory.CreateDirectory(root);
        using var lease = new FileStream(Path.Combine(root, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string id = Guid.NewGuid().ToString("N"), generation = Path.Combine(root, "generations", id);
        Directory.CreateDirectory(generation);
        File.WriteAllText(Path.Combine(generation, ".ehreader-image-search-generation"), "ehreader-image-search/1");
        bool published = false;
        try
        {
            progress?.Report(new("正在枚举本地页图…"));
            var pages = new List<(ImageSearchGallery Gallery, GalleryPage Page)>();
            foreach (var gallery in galleries.DistinctBy(g => (g.SourceId, g.StableId)))
            {
                token.ThrowIfCancellationRequested();
                if (!selected.TryGetValue(gallery.SourceId, out var source) || !IsSafeSourcePath(gallery.Folder, source.RootPath)) continue;
                var scan = PageFileParser.Scan(gallery.Folder, null, out string? error);
                if (error is not null) throw new IOException("图库目录暂时无法读取，当前索引已保留。");
                pages.AddRange(scan.Pages.Select(p => (gallery, p)));
            }
            if (pages.Count == 0) throw new InvalidOperationException("选中的本地库中没有可索引页图。");
            var previous = rebuild ? null : LoadAvailablePointer(root);
            string? previousDirectory = previous is null ? null : Path.Combine(root, "generations", previous.GenerationId);
            using var previousManifest = previousDirectory is null ? null : OpenManifest(previousDirectory, writable: false);
            if (previousDirectory is not null)
            {
                progress?.Report(new("正在准备增量索引快照…"));
                using var original = new ImageDatabase(Path.Combine(previousDirectory, "backend"));
                original.CopySnapshotTo(Path.Combine(generation, "backend"), token);
            }
            using var backend = new ImageDatabase(Path.Combine(generation, "backend"), writable: true);
            using var manifest = OpenManifest(generation, writable: true);
            Execute(manifest, "CREATE TABLE member(alias TEXT NOT NULL,source_id TEXT NOT NULL,stable_id TEXT NOT NULL,page INTEGER NOT NULL,path TEXT NOT NULL,length INTEGER NOT NULL,mtime INTEGER NOT NULL,state TEXT NOT NULL,PRIMARY KEY(source_id,stable_id,page)); PRAGMA user_version=1;");
            int processed = 0, indexed = 0, skipped = 0, failed = 0, descriptors = 0, reused = 0;
            bool needsBuild = previous is null;
            var assets = new Dictionary<string, (string State, int Count)>(StringComparer.Ordinal);
            var extractor = new OrbExtractor(backend.Profile);
            foreach (var (gallery, page) in pages)
            {
                token.ThrowIfCancellationRequested();
                bool handled = false;
                foreach (var candidate in page.Candidates)
                {
                    if (candidate.Length <= 0 || !IsSafeSourcePath(candidate.FilePath, selected[gallery.SourceId].RootPath)) continue;
                    try
                    {
                        string relativePath = Path.GetRelativePath(selected[gallery.SourceId].RootPath, candidate.FilePath);
                        if (previousManifest is not null)
                        {
                            using var lookup = previousManifest.CreateCommand();
                            lookup.CommandText = "SELECT alias,state FROM member WHERE source_id=$source AND stable_id=$stable AND page=$page AND path=$path AND length=$length AND mtime=$mtime";
                            foreach (var (key, value) in new (string, object)[] { ("$source", gallery.SourceId), ("$stable", gallery.StableId), ("$page", page.Number), ("$path", relativePath), ("$length", candidate.Length), ("$mtime", candidate.MtimeTicks) }) lookup.Parameters.AddWithValue(key, value);
                            using var cachedPage = lookup.ExecuteReader();
                            if (cachedPage.Read())
                            {
                                string state = cachedPage.GetString(1);
                                Execute(manifest, "INSERT INTO member VALUES($alias,$source,$stable,$page,$path,$length,$mtime,$state)",
                                    ("$alias", cachedPage.GetString(0)), ("$source", gallery.SourceId), ("$stable", gallery.StableId), ("$page", page.Number),
                                    ("$path", relativePath), ("$length", candidate.Length), ("$mtime", candidate.MtimeTicks), ("$state", state));
                                if (state == "indexed") indexed++; else skipped++;
                                reused++; handled = true; break;
                            }
                        }
                        GrayImage image = GrayImage.ReadFile(candidate.FilePath);
                        byte[] canonical = image.EncodePng();
                        string payload = Convert.ToHexStringLower(SHA256.HashData(canonical));
                        string asset = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(FeatureProfile.NormalizerVersion + "\n" + payload)));
                        string alias = "pages/" + asset + ".png";
                        if (!assets.TryGetValue(alias, out var cached))
                        {
                            var features = extractor.Extract(image, token).Select(f => f.Descriptor).ToArray();
                            var imported = backend.Add(alias, Blake3.Hash(canonical), features);
                            if (imported.Status == "added") needsBuild = true;
                            cached = (imported.Status == "skipped_low_features" ? "skipped" : "indexed", features.Length);
                            assets.Add(alias, cached);
                            if (cached.State == "indexed") descriptors += features.Length;
                        }
                        var actual = new FileInfo(candidate.FilePath);
                        if (!actual.Exists || actual.Length != candidate.Length || actual.LastWriteTimeUtc.Ticks != candidate.MtimeTicks) throw new IOException("SourceChanging");
                        Execute(manifest, "INSERT INTO member VALUES($alias,$source,$stable,$page,$path,$length,$mtime,$state)",
                            ("$alias", alias), ("$source", gallery.SourceId), ("$stable", gallery.StableId), ("$page", page.Number),
                            ("$path", Path.GetRelativePath(selected[gallery.SourceId].RootPath, candidate.FilePath)), ("$length", actual.Length), ("$mtime", actual.LastWriteTimeUtc.Ticks), ("$state", cached.State));
                        if (cached.State == "indexed") indexed++; else skipped++;
                        handled = true; break;
                    }
                    catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { }
                }
                if (!handled) failed++;
                processed++;
                if (processed % 10 == 0 || processed == pages.Count) progress?.Report(new($"正在提取页图 {processed:N0} / {pages.Count:N0}", processed, pages.Count, indexed, skipped, failed));
            }
            if (previous is null)
            {
                if (descriptors < 30) throw new InvalidOperationException("有效特征不足，原有索引已保留。");
                int trainingDescriptors = backend.ReadCached(limit: 3000).Sum(c => c.Descriptors.Length);
                int centers = Math.Min(4096, Math.Max(1, Math.Min(trainingDescriptors / 30, (int)(4 * Math.Sqrt(trainingDescriptors)))));
                progress?.Report(new("正在训练图片索引…", processed, pages.Count, indexed, skipped, failed));
                ImageSearchEngine.Train(backend, centers, 3000, cancellationToken: token);
            }
            progress?.Report(new("正在构建图片索引…", processed, pages.Count, indexed, skipped, failed));
            if (needsBuild) ImageSearchEngine.Build(backend, cancellationToken: token);
            var validated = new ImageSearchEngine(backend);
            if (validated.IndexedImages == 0) throw new InvalidDataException("索引中没有可搜索图片。");
            manifest.Close();
            token.ThrowIfCancellationRequested();
            using var manifestFile = File.OpenRead(Path.Combine(generation, "manifest.db"));
            var pointer = new ImageSearchPointer(1, id, backend.Profile.Id, Convert.ToHexStringLower(SHA256.HashData(manifestFile)), indexed, DateTime.UtcNow);
            string next = Path.Combine(root, "current.json.new"), current = Path.Combine(root, "current.json");
            using (var stream = new FileStream(next, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, pointer, LocalImageSearchJsonContext.Default.ImageSearchPointer); stream.Flush(true); }
            if (File.Exists(current)) File.Replace(next, current, Path.Combine(root, "current.previous.json")); else File.Move(next, current);
            published = true;
            try { PruneUnusedGenerations(root); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _services.Log.Warn("Image index cleanup: " + error.GetType().Name); }
            return new($"已索引 {indexed:N0} / {pages.Count:N0} 页 · 复用 {reused:N0} · 低特征 {skipped:N0} · 失败 {failed:N0}", processed, pages.Count, indexed, skipped, failed, reused);
        }
        finally
        {
            // Interrupted generations survive until cleanup or a later successful publication.
            if (!published) progress?.Report(new("本次索引未发布，已有索引已保留"));
        }
    }

    /// <summary>Queries a frozen generation and filters every physical hit against current sources and files.</summary>
    public Task<LocalImageSearchHit[]> SearchAsync(byte[] input, ImageCrop? crop, IReadOnlyList<GalleryInfo> galleries, CancellationToken token)
    {
        if (!_services.Settings.ImageSearch.Enabled) throw new InvalidOperationException("请先在设置中启用以图搜图。");
        string root = DirectoryPath; var sources = SelectedSources(); int nprobe = _services.Settings.ImageSearch.NProbe;
        var copies = galleries.DistinctBy(g => (g.SourceId, g.StableId)).ToDictionary(g => (g.SourceId, g.StableId));
        return Task.Run(() =>
        {
            ImageSearchPointer pointer;
            lock (_generationGate)
            {
                pointer = LoadAvailablePointer(root) ?? throw new InvalidOperationException("请先建立图片索引。");
                _activeReaders[pointer.GenerationId] = _activeReaders.GetValueOrDefault(pointer.GenerationId) + 1;
            }
            try
            {
            string generation = Path.Combine(root, "generations", pointer.GenerationId);
            using var backend = new ImageDatabase(Path.Combine(generation, "backend"));
            var engine = new ImageSearchEngine(backend);
            var results = engine.Search(GrayImage.Decode(input, crop), new() { NProbe = Math.Min(nprobe, engine.Centers) }, token);
            using var manifest = OpenManifest(generation, writable: false);
            var selected = sources.ToDictionary(s => s.Id, StringComparer.Ordinal); var hits = new List<LocalImageSearchHit>();
            foreach (var result in results)
            foreach (string alias in result.Aliases)
            {
                token.ThrowIfCancellationRequested();
                using var command = manifest.CreateCommand(); command.CommandText = "SELECT source_id,stable_id,page,path,length,mtime FROM member WHERE alias=$alias AND state='indexed'";
                command.Parameters.AddWithValue("$alias", alias); using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string sourceId = reader.GetString(0), stableId = reader.GetString(1);
                    if (!selected.TryGetValue(sourceId, out var source) || !copies.TryGetValue((sourceId, stableId), out var gallery)) continue;
                    string path = Path.GetFullPath(Path.Combine(source.RootPath, reader.GetString(3)));
                    if (!IsSafeSourcePath(path, source.RootPath)) continue;
                    var file = new FileInfo(path); long length = reader.GetInt64(4), mtime = reader.GetInt64(5);
                    if (!file.Exists || file.Length != length || file.LastWriteTimeUtc.Ticks != mtime) continue;
                    hits.Add(new(gallery, reader.GetInt32(2), path, length, mtime, result.Score, pointer.GenerationId));
                }
            }
            return hits.GroupBy(h => (h.Gallery.SourceId, h.Gallery.StableId, h.PageNumber)).Select(g => g.MaxBy(h => h.Score)!)
                .OrderByDescending(h => h.Score).ThenBy(h => h.Gallery.StableId, StringComparer.Ordinal).ThenBy(h => h.PageNumber).Take(30).ToArray();
            }
            finally
            {
                lock (_generationGate)
                    if (--_activeReaders[pointer.GenerationId] == 0) _activeReaders.Remove(pointer.GenerationId);
            }
        }, token);
    }

    /// <summary>Removes only owned, unused generations, retaining current, previous and active-query snapshots.</summary>
    public async Task<int> CleanupAsync(CancellationToken token)
    {
        if (!_services.Settings.ImageSearch.Enabled) throw new InvalidOperationException("请先启用以图搜图。");
        if (!await _writer.WaitAsync(0, token)) throw new InvalidOperationException("图片索引正在更新。");
        string root = DirectoryPath; var sources = _services.Settings.Sources.Select(s => s.Clone()).ToArray();
        try
        {
            return await Task.Run(() =>
            {
                ValidateStorage(root, sources); if (!Directory.Exists(root)) return 0;
                using var lease = new FileStream(Path.Combine(root, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return PruneUnusedGenerations(root);
            }, token);
        }
        finally { _writer.Release(); }
    }

    private int PruneUnusedGenerations(string root)
    {
        lock (_generationGate)
        {
            int deleted = 0;
            var keep = new HashSet<string>(_activeReaders.Keys, StringComparer.Ordinal);
            try
            {
                if (ReadPointer(root) is { } current) keep.Add(current.GenerationId);
                if (ReadPointer(root, backup: true) is { } previous) keep.Add(previous.GenerationId);
            }
            catch (Exception error) when (error is IOException or JsonException) { return 0; }
            string generations = Path.GetFullPath(Path.Combine(root, "generations"));
            if (!Directory.Exists(generations)) return 0;
            foreach (string directory in Directory.EnumerateDirectories(generations))
            {
                string id = Path.GetFileName(directory);
                if (keep.Contains(id) || id.Length != 32 || id.AsSpan().IndexOfAnyExcept("0123456789abcdef") >= 0
                    || !PathIdentity.Comparer.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), generations)) continue;
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    string marker = Path.Combine(directory, ".ehreader-image-search-generation");
                    if (!File.Exists(marker) || File.ReadAllText(marker) != "ehreader-image-search/1" || ContainsReparsePoint(directory)) continue;
                    Directory.Delete(directory, recursive: true); deleted++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Retain busy or inaccessible generations. */ }
            }
            return deleted;
        }
    }
    private static bool ContainsReparsePoint(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? directory))
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
            if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
        }
        return false;
    }

    /// <summary>Rechecks a result immediately before reader navigation.</summary>
    public bool CanRead(LocalImageSearchHit hit)
    {
        try
        {
            return _services.Settings.ImageSearch.Enabled && SelectedSources().Any(source => source.Id == hit.Gallery.SourceId
                && IsSafeSourcePath(hit.FilePath, source.RootPath)) && new FileInfo(hit.FilePath) is { Exists: true } file && file.Length == hit.Length && file.LastWriteTimeUtc.Ticks == hit.MtimeTicks;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private LibrarySource[] SelectedSources()
    {
        var settings = _services.Settings;
        return settings.Sources.Where(s => s.Enabled && (settings.ImageSearch.SourceIds.Count == 0 || settings.ImageSearch.SourceIds.Contains(s.Id))).Select(s => s.Clone()).ToArray();
    }
    private static ImageSearchPointer? ReadPointer(string root, bool backup = false)
    {
        string file = Path.Combine(root, backup ? "current.previous.json" : "current.json"); if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > 16384) throw new InvalidDataException("图片索引指针过大。");
        var pointer = JsonSerializer.Deserialize(File.ReadAllBytes(file), LocalImageSearchJsonContext.Default.ImageSearchPointer) ?? throw new InvalidDataException("图片索引指针损坏。");
        if (pointer.SchemaVersion != 1 || pointer.GenerationId is not { Length: 32 } || pointer.GenerationId.AsSpan().IndexOfAnyExcept("0123456789abcdef") >= 0 || pointer.ProfileId != new FeatureProfile().Id || pointer.Pages < 0)
            throw new InvalidDataException("图片索引版本不兼容，请重新建库。");
        using var manifest = File.OpenRead(Path.Combine(root, "generations", pointer.GenerationId, "manifest.db"));
        if (Convert.ToHexStringLower(SHA256.HashData(manifest)) != pointer.ManifestSha256) throw new InvalidDataException("图片索引清单校验失败。");
        return pointer;
    }
    private static ImageSearchPointer? LoadAvailablePointer(string root)
    {
        try { return ReadPointer(root) ?? ReadPointer(root, backup: true); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            try { if (ReadPointer(root, backup: true) is { } previous) return previous; }
            catch (Exception prior) when (prior is IOException or JsonException or UnauthorizedAccessException) { }
            throw new InvalidDataException("图片索引无法恢复，请重新提取重建。", error);
        }
    }
    private static SqliteConnection OpenManifest(string generation, bool writable)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(generation, "manifest.db"), Mode = writable ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); return connection;
    }
    private static void Execute(SqliteConnection connection, string sql, params (string, object)[] values)
    { using var command = connection.CreateCommand(); command.CommandText = sql; foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value); command.ExecuteNonQuery(); }
    private static bool IsInside(string path, string root) => PathIdentity.Comparer.Equals(path, root) || PathIdentity.IsDescendantOf(path, root);
    private static bool IsSafeSourcePath(string path, string root)
    {
        try
        {
        if (!IsInside(path, root)) return false;
        for (var info = new FileInfo(path); info is not null && IsInside(info.FullName, root); info = info.Directory is { } parent ? new FileInfo(parent.FullName) : null)
            if (File.Exists(info.FullName) || Directory.Exists(info.FullName))
                if ((File.GetAttributes(info.FullName) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    /// <summary>Cancels owned tasks without touching source files.</summary>
    public void Dispose() { _lifetime.Cancel(); Cancel(); }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ImageSearchPointer))]
internal partial class LocalImageSearchJsonContext : JsonSerializerContext;

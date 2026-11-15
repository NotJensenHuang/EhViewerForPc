using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using EhViewerForPc.Core.Format;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.ImageSearch;
using Microsoft.Data.Sqlite;

namespace EhViewerForPc.Services;

/// <summary>Captures immutable gallery scalars before leaving the UI thread.</summary>
public sealed record ImageSearchGallery(string SourceId, string StableId, string WorkId, string Folder);
/// <summary>Reports the current index job without exposing backend details in the search flow.</summary>
public sealed record LocalImageSearchStatus(string Message, int Processed = 0, int Total = 0, int Indexed = 0, int Skipped = 0, int Failed = 0, int Reused = 0, bool CanResume = false)
{
    public IndexBuildProgress? IndexProgress { get; init; }
    public ImageIndexBuildPhase? Phase { get; init; }
}
/// <summary>Identifies an exact physical page and its owning local copy.</summary>
public sealed record LocalImageSearchHit(GalleryInfo Gallery, int PageNumber, string FilePath, long Length, long MtimeTicks, float Score, string GenerationId);
/// <summary>Requests explicit navigation to an original page number in a concrete copy.</summary>
public sealed record LocalPageReadRequest(GalleryInfo Gallery, int PageNumber);
internal sealed record ImageSearchPointer(int SchemaVersion, string GenerationId, string ProfileId, string ManifestSha256, int Pages, DateTime PublishedUtc);

/// <summary>Builds and queries immutable local generations using the managed backend on background threads.</summary>
public sealed partial class LocalImageSearchService : IDisposable
{
    private readonly AppServices _services;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _activeJob;
    private readonly object _jobGate = new();
    private int _disposed;
    private int _notificationPending;
    private int _resumeAfterInterruption = 1;
    private LocalImageSearchStatus? _buildStatus;
    private readonly object _generationGate = new();
    private readonly Dictionary<string, int> _activeReaders = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retiredGenerations = new(StringComparer.Ordinal);
    private readonly object _readGate = new();
    private int _pendingReads;
    private string? _configurationIdentity;

    /// <summary>Initializes a lazy service without touching index or gallery files.</summary>
    public LocalImageSearchService(AppServices services) => _services = services;
    /// <summary>Gets whether this instance currently owns a build job.</summary>
    public bool IsBuilding { get { lock (_jobGate) return _activeJob is not null; } }
    public LocalImageSearchStatus? BuildStatus => Volatile.Read(ref _buildStatus);
    /// <summary>在 UI 线程发布服务持有的任务状态，页面重建后可重新订阅。</summary>
    public event EventHandler? BuildStateChanged;
    /// <summary>Gets the configured index root without creating it.</summary>
    public string DirectoryPath => string.IsNullOrWhiteSpace(_services.Settings.ImageSearch.StorageDirectory)
        ? Path.Combine(_services.Paths.BaseDirectory, "image-search") : Path.GetFullPath(_services.Settings.ImageSearch.StorageDirectory);
    /// <summary>Stops the current owned job at a cancellation checkpoint.</summary>
    public void Cancel()
    {
        lock (_jobGate)
        {
            if (_activeJob is null) return;
            Volatile.Write(ref _resumeAfterInterruption, 0);
            _activeJob.Cancel();
        }
    }
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
        var sources = _services.Settings.Sources.Select(s => s.Clone()).ToArray();
        return RunReadAsync(readToken =>
        {
            ValidateStorage(root, sources);
            readToken.ThrowIfCancellationRequested();
            var pointer = LoadAvailablePointer(root);
            var pending = LoadBuildCheckpoint(root);
            if (pending is not null && pending.GenerationId != pointer?.GenerationId)
            {
                int processed = CountCheckpointPages(root, pending);
                string done = pending.TotalPages > 0 ? $"{processed:N0} / {pending.TotalPages:N0} 页" : $"{processed:N0} 页";
                return new LocalImageSearchStatus($"索引未完成，点击继续（已处理 {done}）", processed,
                    pending.TotalPages, Indexed: pointer?.Pages ?? 0, CanResume: true);
            }
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
    public async Task<LocalImageSearchStatus> BuildAsync(IReadOnlyList<ImageSearchGallery> galleries, IProgress<LocalImageSearchStatus>? progress, CancellationToken token, bool rebuild = false, IndexBuildOptions? options = null)
        => await BuildCoreAsync(galleries, progress, token, rebuild, resumeExisting: !rebuild, options: options).ConfigureAwait(false);

    /// <summary>自动维护沿用建库管线，但不能恢复用户显式暂停的任务。</summary>
    public Task<LocalImageSearchStatus> UpdateAutomaticallyAsync(IReadOnlyList<ImageSearchGallery> galleries, CancellationToken token)
        => BuildCoreAsync(galleries, null, token, rebuild: false, resumeExisting: true, automatic: true);

    private async Task<LocalImageSearchStatus> BuildCoreAsync(IReadOnlyList<ImageSearchGallery> galleries, IProgress<LocalImageSearchStatus>? progress,
        CancellationToken token, bool rebuild, bool resumeExisting, bool automatic = false, IndexBuildOptions? options = null)
    {
        options ??= new IndexBuildOptions();
        options.Validate();
        if (!_services.Settings.ImageSearch.Enabled) throw new InvalidOperationException("请先在设置中启用以图搜图。");
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!await _writer.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("图片索引正在更新。");
        CancellationTokenSource? job = null;
        try
        {
            job = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            lock (_jobGate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                _activeJob = job;
                Volatile.Write(ref _resumeAfterInterruption, 1);
            }
            Volatile.Write(ref _statistics, null);
            string root = DirectoryPath;
            var sources = SelectedSources();
            var allSources = _services.Settings.Sources.Select(s => s.Clone()).ToArray();
            var snapshot = galleries.ToArray();
            PublishBuildStatus(new("正在准备图片索引…", Indexed: BuildStatus?.Indexed ?? 0));
            var status = await Task.Run(() => Build(root, snapshot, sources, allSources,
                new BuildProgress(this, progress), job.Token, rebuild, resumeExisting, automatic, options)).ConfigureAwait(false);
            PublishBuildStatus(status);
            return status;
        }
        catch (OperationCanceledException)
        {
            PublishBuildStatus(new("已取消建库，已完成页面和原有索引已保留，可继续", CanResume: true));
            throw;
        }
        catch (Exception error)
        {
            _services.Log.Warn("Image index build: " + error.GetType().Name);
            PublishBuildStatus(new("建立索引失败：" + error.Message, CanResume: BuildStatus?.CanResume == true));
            throw;
        }
        finally
        {
            try { FinishComputeJob(); }
            catch (Exception error) { _services.Log.Warn("GPU working set cleanup: " + error.GetType().Name); }
            lock (_jobGate) _activeJob = null;
            job?.Dispose();
            _writer.Release();
            NotifyBuildState();
        }
    }

    private void PublishBuildStatus(LocalImageSearchStatus status)
    {
        Volatile.Write(ref _buildStatus, status);
        NotifyBuildState();
    }

    private void NotifyBuildState()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _notificationPending, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _notificationPending, 0);
            if (Volatile.Read(ref _disposed) == 0) BuildStateChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private sealed class BuildProgress(LocalImageSearchService owner, IProgress<LocalImageSearchStatus>? observer) : IProgress<LocalImageSearchStatus>
    {
        public void Report(LocalImageSearchStatus value)
        {
            owner.PublishBuildStatus(value);
            observer?.Report(value);
        }
    }

    private async Task<LocalImageSearchStatus> Build(string root, ImageSearchGallery[] galleries, LibrarySource[] sources, LibrarySource[] allSources, IProgress<LocalImageSearchStatus>? progress, CancellationToken token, bool rebuild, bool resumeExisting, bool automatic, IndexBuildOptions options)
    {
        ValidateStorage(root, allSources);
        if (sources.Length == 0 || galleries.Length == 0) throw new InvalidOperationException("请先添加本地数据源并扫描图库。");
        var selected = sources.ToDictionary(s => s.Id, StringComparer.Ordinal);
        if (sources.Any(s => !Directory.Exists(s.RootPath))) throw new IOException("选中的数据源离线，当前索引已保留。");
        Directory.CreateDirectory(root);
        using var lease = new FileStream(Path.Combine(root, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = LoadAvailablePointer(root);
        string configuration = ConfigurationId(root, sources);
        var checkpoint = resumeExisting ? LoadBuildCheckpoint(root) ?? FindLegacyPendingBuild(root, galleries, sources) : null;
        if (checkpoint is not null && checkpoint.GenerationId == current?.GenerationId)
        {
            CompleteBuildCheckpoint(root);
            return new($"已索引 {current.Pages:N0} 页", Indexed: current.Pages);
        }
        if (automatic && checkpoint is { ResumeOnStartup: false })
            return new("索引已暂停，点击继续建立索引", Indexed: current?.Pages ?? 0, CanResume: true);
        bool resumed = checkpoint is not null && checkpoint.ConfigurationId == configuration &&
            checkpoint.PreviousGenerationId == (current?.GenerationId ?? string.Empty);
        if (!resumed)
        {
            checkpoint = new ImageIndexBuildCheckpoint
            {
                GenerationId = Guid.NewGuid().ToString("N"), PreviousGenerationId = current?.GenerationId ?? string.Empty,
                ConfigurationId = configuration, ProfileId = new FeatureProfile().Id, Rebuild = rebuild,
            };
        }
        var job = checkpoint!;
        var requestedGalleries = galleries.Select(g => new ImageIndexGalleryCheckpoint(g.SourceId, g.StableId, g.WorkId, g.Folder)).ToArray();
        if (resumed && !job.Galleries.SequenceEqual(requestedGalleries)) job.PlanPrepared = false;
        job.Galleries = requestedGalleries;
        job.ResumeOnStartup = Volatile.Read(ref _resumeAfterInterruption) != 0;
        SaveBuildCheckpoint(root, job);
        string id = job.GenerationId, generation = Path.Combine(root, "generations", id);
        ValidateStorage(generation, allSources);
        string marker = Path.Combine(generation, ".ehreader-image-search-generation");
        if (Directory.Exists(generation) && !File.Exists(marker)) throw new InvalidDataException("未识别的索引任务目录。");
        Directory.CreateDirectory(generation);
        if (ContainsReparsePoint(generation)) throw new IOException("索引任务目录不能包含链接。");
        if (File.Exists(marker) && File.ReadAllText(marker) != "ehreader-image-search/1") throw new InvalidDataException("索引任务目录标记无效。");
        if (!File.Exists(marker)) File.WriteAllText(marker, "ehreader-image-search/1");
        bool published = false;
        try
        {
            Volatile.Write(ref _planStatistics, new(0, 0, job.Phase));
            RemoveInterruptedTemporaryFiles(generation, token);
            if (!job.FeaturesMigrated)
            {
                progress?.Report(new("正在整理共享特征库…"));
                MigrateOwnedFeatureCaches(root, allSources, token);
                job.FeaturesMigrated = true;
                SaveBuildCheckpoint(root, job);
            }
            var previous = job.Rebuild ? null : current;
            string? previousDirectory = previous is null ? null : Path.Combine(root, "generations", previous.GenerationId);
            using var manifest = OpenManifest(generation, writable: true);
            Execute(manifest, "CREATE TABLE IF NOT EXISTS member(alias TEXT NOT NULL,source_id TEXT NOT NULL,stable_id TEXT NOT NULL,page INTEGER NOT NULL,path TEXT NOT NULL,length INTEGER NOT NULL,mtime INTEGER NOT NULL,state TEXT NOT NULL,PRIMARY KEY(source_id,stable_id,page)); CREATE INDEX IF NOT EXISTS member_state ON member(state); PRAGMA user_version=1;");
            AttachBuildPlan(manifest, generation);
            progress?.Report(new(resumed ? "正在读取未完成索引任务…" : "正在枚举本地页图…"));
            if (!job.PlanPrepared)
            {
                ReconcilePlanMembership(manifest, galleries, token);
                PrepareBuildPlan(manifest, galleries, selected, verify: false, token,
                    (done, total) => progress?.Report(new($"正在枚举本地页图 {done:N0} / {total:N0}")));
                Execute(manifest, "DELETE FROM member WHERE NOT EXISTS(SELECT 1 FROM build.plan_page p WHERE p.source=member.source_id AND p.stable=member.stable_id AND p.page=member.page)");
                job.PlanPrepared = true;
                SaveBuildCheckpoint(root, job);
            }
            job.TotalPages = PlanCount(manifest);
            if (job.TotalPages == 0)
            {
                // 空目录以后可能重新出现页图；下次继续需要重新发现，不能固定在空计划。
                Execute(manifest, "DELETE FROM build.plan_gallery");
                job.PlanPrepared = false;
                throw new InvalidOperationException("选中的本地库中没有可索引页图。");
            }
            if (!resumed && previousDirectory is not null && TryReuseUnchangedIndex(previousDirectory,
                ReadPlannedPages(generation, selected, pendingOnly: false, token), job.TotalPages, selected, token) is { } unchanged)
            {
                token.ThrowIfCancellationRequested();
                manifest.Close(); published = true;
                CompleteBuildCheckpoint(root);
                try { PruneUnusedGenerations(root); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
                { _services.Log.Warn("Image index cleanup: " + error.GetType().Name); }
                Volatile.Write(ref _statistics, new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Unchanged: true));
                return unchanged;
            }
            string backendPath = Path.Combine(generation, "backend");
            if (!job.BackendPrepared && job.Phase != ImageIndexBuildPhase.Preparing && File.Exists(Path.Combine(backendPath, "images.db")))
                job.BackendPrepared = true; // 兼容旧版：离开准备阶段前已完成后端快照。
            if (!job.BackendPrepared)
            {
                if (Directory.Exists(backendPath))
                {
                    // 只重置本应用标记目录中的未完成快照，保留独立的页任务计划。
                    string target = Path.GetFullPath(backendPath);
                    if (!PathIdentity.IsDescendantOf(target, Path.GetFullPath(generation)) || ContainsReparsePoint(target))
                        throw new IOException("未完成索引快照路径无效。");
                    Directory.Delete(target, recursive: true);
                }
                if (previousDirectory is not null)
                {
                    progress?.Report(new("正在准备增量索引快照…"));
                    using var original = new ImageDatabase(Path.Combine(previousDirectory, "backend"));
                    original.CopySnapshotTo(backendPath, token, shareImmutableFiles: true);
                }
            }
            using var backend = new ImageDatabase(backendPath, writable: true);
            backend.UseSharedFeatureCache(SharedFeaturesPath(root));
            job.BackendPrepared = true;
            if (resumed) Execute(manifest, "UPDATE build.plan_page SET done=0 WHERE done=2");
            if (resumed && (job.Phase != ImageIndexBuildPhase.CheckingSources || job.SourcesVerified))
            {
                job.SourcesVerified = false;
                Execute(manifest, "UPDATE build.plan_gallery SET verified=0");
            }
            SaveBuildCheckpoint(root, job);
            var extraction = ReadPlanResult(manifest);
            Volatile.Write(ref _planStatistics, PlanStatistics! with { RestoredPages = extraction.Reused });
            Volatile.Write(ref _statistics, new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
            PrepareComputeJob(options, token);
            bool needsBuild = previous is null && !File.Exists(Path.Combine(backendPath, "invlists.bin"));
            bool resumeBuild = resumed && job.Phase == ImageIndexBuildPhase.Building;
            void VerifySources()
            {
                job.Phase = ImageIndexBuildPhase.CheckingSources;
                SaveBuildCheckpoint(root, job);
                progress?.Report(new("正在核对源目录变更…", extraction.Processed, job.TotalPages, extraction.Indexed, extraction.Skipped, extraction.Failed, extraction.Reused));
                PrepareBuildPlan(manifest, galleries, selected, verify: true, token,
                    (done, total) => progress?.Report(new($"正在核对源目录变更 {done:N0} / {total:N0}", extraction.Processed, job.TotalPages, extraction.Indexed, extraction.Skipped, extraction.Failed, extraction.Reused)));
                job.TotalPages = PlanCount(manifest);
                job.SourcesVerified = true;
                SaveBuildCheckpoint(root, job);
            }
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (PlanCount(manifest, "done=0") > 0)
                {
                    job.Phase = ImageIndexBuildPhase.Extracting;
                    SaveBuildCheckpoint(root, job);
                    extraction = await ExtractPagesAsync(root, generation, previousDirectory, backend, manifest, job.TotalPages, selected,
                        job.Rebuild, options, progress, token).ConfigureAwait(false);
                    needsBuild |= extraction.NeedsBuild;
                }
                else
                {
                    var counts = ReadPlanResult(manifest);
                    extraction = counts with { Reused = Math.Min(extraction.Reused, counts.Indexed + counts.Skipped) };
                }
                var liveAliases = new HashSet<string>(StringComparer.Ordinal);
                using (var aliases = manifest.CreateCommand())
                {
                    aliases.CommandText = "SELECT DISTINCT alias FROM member";
                    using var reader = aliases.ExecuteReader();
                    while (reader.Read()) liveAliases.Add(reader.GetString(0));
                }
                needsBuild |= backend.RetainAliases(liveAliases) > 0;
                if (!File.Exists(Path.Combine(backendPath, "quantizer.bin")))
                {
                    int trainingDescriptors = backend.ReadCached(limit: 3000).Sum(c => c.Descriptors.Length);
                    if (trainingDescriptors < 30)
                    {
                        if (!job.SourcesVerified) { VerifySources(); continue; }
                        job.Phase = ImageIndexBuildPhase.Training;
                        SaveBuildCheckpoint(root, job);
                        throw new InvalidOperationException("有效特征不足，原有索引已保留。");
                    }
                    job.Phase = ImageIndexBuildPhase.Training;
                    SaveBuildCheckpoint(root, job);
                    int centers = Math.Min(4096, Math.Max(1, Math.Min(trainingDescriptors / 30, (int)(4 * Math.Sqrt(trainingDescriptors)))));
                    progress?.Report(new("正在训练图片索引…", extraction.Processed, job.TotalPages, extraction.Indexed, extraction.Skipped, extraction.Failed));
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    ImageSearchEngine.Train(backend, centers, 3000, cancellationToken: token, workers: options.Workers, accelerator: _jobAccelerator);
                    if (BuildStatistics is { } statistics)
                        Volatile.Write(ref _statistics, statistics with { TrainingMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                }
                if (needsBuild || resumeBuild || backend.HasPendingImages)
                {
                    job.Phase = ImageIndexBuildPhase.Building;
                    SaveBuildCheckpoint(root, job);
                    progress?.Report(new("正在构建图片索引…", extraction.Processed, job.TotalPages, extraction.Indexed, extraction.Skipped, extraction.Failed));
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    ImageSearchEngine.Build(backend, new IndexProgressObserver(value =>
                        progress?.Report(new(DescribeIndexProgress(value), extraction.Processed, job.TotalPages,
                            extraction.Indexed, extraction.Skipped, extraction.Failed, extraction.Reused) { IndexProgress = value })),
                        cancellationToken: token, allowedImageIds: backend.AliasedImageIds(), workers: options.Workers);
                    if (BuildStatistics is { } statistics)
                        Volatile.Write(ref _statistics, statistics with { IndexMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                    needsBuild = resumeBuild = false;
                }
                if (!job.SourcesVerified) { VerifySources(); continue; }
                break;
            }
            int processed = extraction.Processed, indexed = extraction.Indexed, skipped = extraction.Skipped, failed = extraction.Failed, reused = extraction.Reused;
            var validated = new ImageSearchEngine(backend, allowLinkedPublication: true);
            if (validated.IndexedImages == 0) throw new InvalidDataException("索引中没有可搜索图片。");
            manifest.Close();
            token.ThrowIfCancellationRequested();
            using var manifestFile = File.OpenRead(Path.Combine(generation, "manifest.db"));
            var pointer = new ImageSearchPointer(1, id, backend.Profile.Id, Convert.ToHexStringLower(SHA256.HashData(manifestFile)), indexed, DateTime.UtcNow);
            string next = Path.Combine(root, "current.json.new"), pointerPath = Path.Combine(root, "current.json");
            using (var stream = new FileStream(next, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, pointer, LocalImageSearchJsonContext.Default.ImageSearchPointer); stream.Flush(true); }
            lock (_generationGate)
            {
                if (File.Exists(pointerPath)) File.Replace(next, pointerPath, Path.Combine(root, "current.previous.json")); else File.Move(next, pointerPath);
            }
            published = true;
            CompleteBuildCheckpoint(root);
            try { File.Delete(Path.Combine(generation, "build-plan.db")); }
            catch (IOException error) { _services.Log.Warn("Completed index plan cleanup: " + error.GetType().Name); }
            try { PruneUnusedGenerations(root); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException) { _services.Log.Warn("Image index cleanup: " + error.GetType().Name); }
            return new($"已索引 {indexed:N0} / {job.TotalPages:N0} 页 · 复用 {reused:N0} · 低特征 {skipped:N0} · 失败 {failed:N0}", processed, job.TotalPages, indexed, skipped, failed, reused);
        }
        finally
        {
            // Interrupted generations survive until cleanup or a later successful publication.
            if (!published)
            {
                job.ResumeOnStartup = Volatile.Read(ref _resumeAfterInterruption) != 0;
                SaveBuildCheckpoint(root, job);
                progress?.Report(new("本次索引未发布，已完成页面和已有索引已保留", CanResume: true));
            }
        }
    }

    /// <summary>Queries a frozen generation and filters every physical hit against current sources and files.</summary>
    public Task<LocalImageSearchHit[]> SearchAsync(byte[] input, ImageCrop? crop, IReadOnlyList<GalleryInfo> galleries, CancellationToken token)
        => SearchAsync(input, crop, galleries, null, token);

    public Task<LocalImageSearchHit[]> SearchAsync(byte[] input, ImageCrop? crop, IReadOnlyList<GalleryInfo> galleries,
        IProgress<string>? progress, CancellationToken token)
    {
        if (!_services.Settings.ImageSearch.Enabled) throw new InvalidOperationException("请先在设置中启用以图搜图。");
        string root = DirectoryPath;
        var sources = SelectedSources();
        int nprobe = _services.Settings.ImageSearch.NProbe;
        var copies = galleries.DistinctBy(g => (g.SourceId, g.StableId)).ToDictionary(g => (g.SourceId, g.StableId));
        return RunReadAsync(readToken => SearchCore(root, input, crop, sources, copies, nprobe, progress, readToken), token, _querySerial);
    }

    /// <summary>Removes owned, unused snapshots, retaining the current index, pending job and active queries.</summary>
    public async Task<int> CleanupAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_services.Settings.ImageSearch.Enabled) throw new InvalidOperationException("请先启用以图搜图。");
        if (!await _writer.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("图片索引正在更新。");
        try
        {
            string root = DirectoryPath; var sources = _services.Settings.Sources.Select(s => s.Clone()).ToArray();
            return await Task.Run(() =>
            {
                ValidateStorage(root, sources); if (!Directory.Exists(root)) return 0;
                using var lease = new FileStream(Path.Combine(root, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                MigrateOwnedFeatureCaches(root, sources, token);
                return PruneUnusedGenerations(root);
            }, token).ConfigureAwait(false);
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
                if (ReadPointer(root) is { } published)
                {
                    keep.Add(published.GenerationId);
                    foreach (string active in _activeReaders.Keys)
                        if (active != published.GenerationId) _retiredGenerations.Add(active);
                    if (!JsonFileStore.Save(Path.Combine(root, "current.previous.json"), published, LocalImageSearchJsonContext.Default.ImageSearchPointer, out _)) return 0;
                }
                else if (LoadBuildCheckpoint(root) is null) return 0;
                if (LoadBuildCheckpoint(root) is { } pending) keep.Add(pending.GenerationId);
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
            PruneSharedFeatures(root);
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
    private static ImageSearchPointer? ReadPointer(string root, bool backup = false, bool validateManifest = true, CancellationToken token = default)
    {
        string file = Path.Combine(root, backup ? "current.previous.json" : "current.json"); if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > 16384) throw new InvalidDataException("图片索引指针过大。");
        var pointer = JsonSerializer.Deserialize(File.ReadAllBytes(file), LocalImageSearchJsonContext.Default.ImageSearchPointer) ?? throw new InvalidDataException("图片索引指针损坏。");
        if (pointer.SchemaVersion != 1 || pointer.GenerationId is not { Length: 32 } || pointer.GenerationId.AsSpan().IndexOfAnyExcept("0123456789abcdef") >= 0 || pointer.ProfileId != new FeatureProfile().Id || pointer.Pages < 0)
            throw new InvalidDataException("图片索引版本不兼容，请重新建库。");
        if (!validateManifest) return pointer;
        using var manifest = File.OpenRead(Path.Combine(root, "generations", pointer.GenerationId, "manifest.db"));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        int count;
        while ((count = manifest.Read(buffer)) != 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != pointer.ManifestSha256) throw new InvalidDataException("图片索引清单校验失败。");
        return pointer;
    }
    private ImageSearchPointer? LoadAvailablePointer(string root)
    {
        lock (_generationGate) return LoadAvailablePointerCore(root);
    }

    private static ImageSearchPointer? LoadAvailablePointerCore(string root, CancellationToken token = default)
    {
        try { return ReadPointer(root, token: token) ?? ReadPointer(root, backup: true, token: token); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            try { if (ReadPointer(root, backup: true, token: token) is { } previous) return previous; }
            catch (Exception prior) when (prior is IOException or JsonException or UnauthorizedAccessException) { }
            throw new InvalidDataException("图片索引无法恢复，请重新提取重建。", error);
        }
    }
    private static SqliteConnection OpenManifest(string generation, bool writable)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(generation, "manifest.db"), Mode = writable ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
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
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        lock (_jobGate) _activeJob?.Cancel();
        // 后台建库不依赖 UI continuation；退出时等在途 SQLite / 文件写入收尾。
        _writer.Wait();
        _writer.Release();
        lock (_readGate)
            while (_pendingReads != 0) Monitor.Wait(_readGate);
        _queryCache?.Dispose();
        _querySerial.Dispose();
        try { _gpu?.Dispose(); }
        finally { _sourceIo.Dispose(); }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ImageSearchPointer))]
internal partial class LocalImageSearchJsonContext : JsonSerializerContext;

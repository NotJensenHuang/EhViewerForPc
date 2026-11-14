using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using EhViewerForPc.Core.Format;
using EhViewerForPc.Core.Models;
using EhViewerForPc.ImageSearch;
using Microsoft.Data.Sqlite;

namespace EhViewerForPc.Services;

public sealed record LocalIndexBuildStatistics(int Workers, int BatchSize, long WorkingSetLimit, long PeakWorkspaceBytes,
    int DecodedPages, int ExtractedPages, int CommittedBatches, double DecodeMilliseconds, double ExtractMilliseconds,
    double WriteMilliseconds, bool Unchanged = false, double HashMilliseconds = 0, double TrainingMilliseconds = 0, double IndexMilliseconds = 0,
    int PeakParallelPages = 0, double MemoryWaitMilliseconds = 0);

public sealed partial class LocalImageSearchService
{
    private LocalIndexBuildStatistics? _statistics;
    public LocalIndexBuildStatistics? BuildStatistics => Volatile.Read(ref _statistics);

    private readonly record struct CachedPage(string Alias, string State);
    private readonly record struct IndexCandidate(PageCandidate File, string Relative, CachedPage? Cached, bool Saved);
    private sealed record IndexPageWork(ImageSearchGallery Gallery, int Page, IndexCandidate[] Candidates);
    private sealed record IndexPageResult(ImageSearchGallery Gallery, int Page, IndexCandidate? Candidate, string Alias = "",
        string State = "", byte[]? Hash = null, BinaryDescriptor[]? Descriptors = null, bool Reused = false);
    private sealed record ExtractionResult(int Processed, int Indexed, int Skipped, int Failed, int Reused, bool NeedsBuild);

    private sealed class ExtractionCounters
    {
        internal int Decoded, Extracted, Active, Peak;
        internal long DecodeTicks, HashTicks, ExtractTicks, MemoryWaitTicks;

        internal void Enter()
        {
            int active = Interlocked.Increment(ref Active), peak;
            do { peak = Volatile.Read(ref Peak); }
            while (active > peak && Interlocked.CompareExchange(ref Peak, active, peak) != peak);
        }
    }

    private sealed class ExtractionWorker : IDisposable
    {
        internal readonly OrbExtractor Extractor;
        internal readonly SharedFeatureCache? Cache;
        internal readonly string ProfileId;
        internal ExtractionWorker(FeatureProfile profile, string root, bool rebuild, bool useSimd)
        {
            Extractor = new(profile, useSimd);
            ProfileId = profile.Id;
            if (!rebuild) Cache = new SharedFeatureCache(SharedFeaturesPath(root), writable: false);
        }
        public void Dispose() => Cache?.Dispose();
    }

    private sealed class ManifestLookup : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteCommand _command;
        internal ManifestLookup(string directory)
        {
            _connection = OpenManifest(directory, writable: false);
            _command = _connection.CreateCommand();
            try
            {
                _command.CommandText = "SELECT alias,state FROM member WHERE source_id=$source AND stable_id=$stable AND page=$page AND path=$path AND length=$length AND mtime=$mtime";
                foreach (string name in new[] { "$source", "$stable", "$path" }) _command.Parameters.Add(name, SqliteType.Text);
                foreach (string name in new[] { "$page", "$length", "$mtime" }) _command.Parameters.Add(name, SqliteType.Integer);
                _command.Prepare();
            }
            catch { Dispose(); throw; }
        }
        internal CachedPage? Find(ImageSearchGallery gallery, int page, PageCandidate file, string relative)
        {
            _command.Parameters["$source"].Value = gallery.SourceId;
            _command.Parameters["$stable"].Value = gallery.StableId;
            _command.Parameters["$page"].Value = page;
            _command.Parameters["$path"].Value = relative;
            _command.Parameters["$length"].Value = file.Length;
            _command.Parameters["$mtime"].Value = file.MtimeTicks;
            using var reader = _command.ExecuteReader();
            if (!reader.Read()) return null;
            string state = reader.GetString(1);
            return state is "indexed" or "skipped" ? new(reader.GetString(0), state) : null;
        }
        public void Dispose() { _command.Dispose(); _connection.Dispose(); }
    }

    private IEnumerable<IndexPageWork> PreparePageWork(string generation, string? previous,
        List<(ImageSearchGallery Gallery, GalleryPage Page)> pages, Dictionary<string, LibrarySource> sources, CancellationToken token)
    {
        using var saved = new ManifestLookup(generation);
        using var old = previous is null ? null : new ManifestLookup(previous);
        foreach (var (gallery, page) in pages)
        {
            token.ThrowIfCancellationRequested();
            var candidates = new List<IndexCandidate>(page.Candidates.Count);
            foreach (var file in page.Candidates)
            {
                if (file.Length <= 0 || !IsSafeSourcePath(file.FilePath, sources[gallery.SourceId].RootPath)) continue;
                string relative = Path.GetRelativePath(sources[gallery.SourceId].RootPath, file.FilePath);
                var cached = saved.Find(gallery, page.Number, file, relative);
                bool isSaved = cached is not null;
                cached ??= old?.Find(gallery, page.Number, file, relative);
                candidates.Add(new(file, relative, cached, isSaved));
            }
            yield return new(gallery, page.Number, candidates.ToArray());
        }
    }

    private static LocalImageSearchStatus? TryReuseUnchangedIndex(string previous,
        List<(ImageSearchGallery Gallery, GalleryPage Page)> pages, Dictionary<string, LibrarySource> sources, CancellationToken token)
    {
        using (var manifest = OpenManifest(previous, writable: false))
        using (var command = manifest.CreateCommand())
        {
            command.CommandText = "SELECT count(*) FROM member";
            if ((long)command.ExecuteScalar()! != pages.Count) return null;
        }
        using var lookup = new ManifestLookup(previous);
        int indexed = 0, skipped = 0;
        foreach (var (gallery, page) in pages)
        {
            token.ThrowIfCancellationRequested();
            CachedPage? match = null;
            foreach (var file in page.Candidates)
            {
                if (!IsSafeSourcePath(file.FilePath, sources[gallery.SourceId].RootPath)) continue;
                string relative = Path.GetRelativePath(sources[gallery.SourceId].RootPath, file.FilePath);
                match = lookup.Find(gallery, page.Number, file, relative);
                // 新增的更优候选必须先尝试解码，不能因后面的旧候选匹配就跳过更新。
                break;
            }
            if (match is not { } cached) return null;
            if (cached.State == "indexed") indexed++; else skipped++;
        }
        return new($"索引无变化 · 已索引 {indexed:N0} 页 · 复用 {pages.Count:N0} 页", pages.Count, pages.Count,
            indexed, skipped, Reused: pages.Count);
    }

    private static bool IsUnchangedCandidate(IndexCandidate candidate)
    {
        try
        {
            var file = new FileInfo(candidate.File.FilePath);
            return file.Exists && file.Length == candidate.File.Length && file.LastWriteTimeUtc.Ticks == candidate.File.MtimeTicks;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static async ValueTask<IndexPageResult> ExtractPageAsync(ExtractionWorker worker, IndexPageWork work,
        FeatureProfile profile, IndexBuildOptions options, ImageWorkBudget budget, ExtractionCounters counters, CancellationToken token)
    {
        foreach (var candidate in work.Candidates)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(candidate.File.FilePath);
                if (!file.Exists || file.Length != candidate.File.Length || file.LastWriteTimeUtc.Ticks != candidate.File.MtimeTicks) continue;
                if (candidate.Cached is { } cached)
                    return new(work.Gallery, work.Page, candidate, cached.Alias, cached.State, Reused: true);
                long estimate, waitStarted = Stopwatch.GetTimestamp();
                // Header 解析同样受配额约束；解析完释放后再申请整个像素工作区，避免升级租约死锁。
                using (await budget.ReserveAsync(Math.Min(52L * 1024 * 1024, file.Length + 2L * 1024 * 1024), token).ConfigureAwait(false))
                {
                    Interlocked.Add(ref counters.MemoryWaitTicks, Stopwatch.GetTimestamp() - waitStarted);
                    estimate = GrayImage.EstimateWorkingSet(file.FullName, profile);
                }
                waitStarted = Stopwatch.GetTimestamp();
                using var memory = await budget.ReserveAsync(estimate, token).ConfigureAwait(false);
                Interlocked.Add(ref counters.MemoryWaitTicks, Stopwatch.GetTimestamp() - waitStarted);
                counters.Enter();
                try
                {
                    long started = Stopwatch.GetTimestamp();
                    var image = GrayImage.ReadFile(file.FullName, useSimd: options.UseSimd);
                    Interlocked.Add(ref counters.DecodeTicks, Stopwatch.GetTimestamp() - started);
                    started = Stopwatch.GetTimestamp();
                    byte[] canonical = image.EncodePng();
                    string payload = Convert.ToHexStringLower(SHA256.HashData(canonical));
                    string asset = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(FeatureProfile.NormalizerVersion + "\n" + payload)));
                    byte[] hash = Blake3.Hash(canonical);
                    Interlocked.Add(ref counters.HashTicks, Stopwatch.GetTimestamp() - started);
                    Interlocked.Increment(ref counters.Decoded);
                    BinaryDescriptor[]? descriptors;
                    try { descriptors = worker.Cache?.ReadDescriptors(worker.ProfileId, hash); }
                    catch (InvalidDataException error) { throw new InvalidOperationException("共享图片特征损坏，请重新提取重建。", error); }
                    bool reused = descriptors is not null;
                    if (descriptors is null)
                    {
                        started = Stopwatch.GetTimestamp();
                        var features = worker.Extractor.Extract(image, token);
                        descriptors = new BinaryDescriptor[features.Length];
                        for (int i = 0; i < features.Length; i++) descriptors[i] = features[i].Descriptor;
                        Interlocked.Add(ref counters.ExtractTicks, Stopwatch.GetTimestamp() - started);
                        Interlocked.Increment(ref counters.Extracted);
                    }
                    file.Refresh();
                    if (!file.Exists || file.Length != candidate.File.Length || file.LastWriteTimeUtc.Ticks != candidate.File.MtimeTicks) continue;
                    return new(work.Gallery, work.Page, candidate, "pages/" + asset + ".png", descriptors.Length <= 30 ? "skipped" : "indexed", hash, descriptors, reused);
                }
                finally { Interlocked.Decrement(ref counters.Active); }
            }
            catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException) { }
        }
        return new(work.Gallery, work.Page, null);
    }

    private async Task<ExtractionResult> ExtractPagesAsync(string root, string generation, string? previous, ImageDatabase backend,
        SqliteConnection manifest, List<(ImageSearchGallery Gallery, GalleryPage Page)> pages, Dictionary<string, LibrarySource> sources,
        bool rebuild, IndexBuildOptions options, IProgress<LocalImageSearchStatus>? progress, CancellationToken token)
    {
        long overhead = (16L + 2 * options.Workers) * 1024 * 1024;
        long workspace = Math.Min(options.WorkingSetBytes, ImageMemoryBudget.IndexLimitBytes - overhead);
        long reserved = workspace + overhead;
        IDisposable? reservation;
        bool waiting = false;
        while (!ImageMemoryBudget.TryReserveIndex(reserved, out reservation))
        {
            token.ThrowIfCancellationRequested();
            if (!waiting) { progress?.Report(new("正在等待可用图像内存…", Total: pages.Count)); waiting = true; }
            await Task.Delay(50, token).ConfigureAwait(false);
        }
        using var imageMemory = reservation;
        var budget = new ImageWorkBudget(workspace);
        var counters = new ExtractionCounters();
        using var writer = backend.CreateImportWriter();
        using var insert = manifest.CreateCommand();
        insert.CommandText = "INSERT OR REPLACE INTO member VALUES($alias,$source,$stable,$page,$path,$length,$mtime,$state)";
        foreach (string name in new[] { "$alias", "$source", "$stable", "$path", "$state" }) insert.Parameters.Add(name, SqliteType.Text);
        foreach (string name in new[] { "$page", "$length", "$mtime" }) insert.Parameters.Add(name, SqliteType.Integer);
        insert.Prepare();
        using var delete = manifest.CreateCommand();
        delete.CommandText = "DELETE FROM member WHERE source_id=$source AND stable_id=$stable AND page=$page";
        delete.Parameters.Add("$source", SqliteType.Text); delete.Parameters.Add("$stable", SqliteType.Text); delete.Parameters.Add("$page", SqliteType.Integer);
        delete.Prepare();
        SqliteTransaction? transaction = null;
        int processed = 0, indexed = 0, skipped = 0, failed = 0, reused = 0, buffered = 0, commits = 0;
        bool needsBuild = false, writeFailed = false;
        long writeTicks = 0;
        void Flush()
        {
            if (buffered == 0) return;
            long start = Stopwatch.GetTimestamp();
            writer.Commit();
            transaction?.Commit();
            transaction?.Dispose();
            transaction = null;
            buffered = 0;
            commits++;
            writeTicks += Stopwatch.GetTimestamp() - start;
        }
        void CommitPage(IndexPageResult result)
        {
            try
            {
                long start = Stopwatch.GetTimestamp();
                if (result.Candidate is not { } candidate || !IsUnchangedCandidate(candidate))
                {
                    transaction ??= manifest.BeginTransaction(); delete.Transaction = transaction;
                    delete.Parameters["$source"].Value = result.Gallery.SourceId;
                    delete.Parameters["$stable"].Value = result.Gallery.StableId;
                    delete.Parameters["$page"].Value = result.Page;
                    delete.ExecuteNonQuery();
                    failed++;
                }
                else
                {
                    if (result.Descriptors is { } descriptors)
                        needsBuild |= writer.Add(result.Alias, result.Hash!, descriptors).Status == "added";
                    if (!candidate.Saved)
                    {
                        transaction ??= manifest.BeginTransaction(); insert.Transaction = transaction;
                        insert.Parameters["$alias"].Value = result.Alias;
                        insert.Parameters["$source"].Value = result.Gallery.SourceId;
                        insert.Parameters["$stable"].Value = result.Gallery.StableId;
                        insert.Parameters["$page"].Value = result.Page;
                        insert.Parameters["$path"].Value = candidate.Relative;
                        insert.Parameters["$length"].Value = candidate.File.Length;
                        insert.Parameters["$mtime"].Value = candidate.File.MtimeTicks;
                        insert.Parameters["$state"].Value = result.State;
                        insert.ExecuteNonQuery();
                    }
                    if (result.State == "indexed") indexed++; else skipped++;
                    if (result.Reused) reused++;
                }
                processed++;
                buffered++;
                writeTicks += Stopwatch.GetTimestamp() - start;
                if (buffered >= options.BatchSize || processed % 10 == 0 || processed == pages.Count) Flush();
                if (processed % 10 == 0 || processed == pages.Count)
                    progress?.Report(new($"正在提取页图 {processed:N0} / {pages.Count:N0} · {options.Workers} 并行", processed, pages.Count, indexed, skipped, failed, reused));
            }
            catch { writeFailed = true; throw; }
        }
        try
        {
            progress?.Report(new($"正在提取页图…（{options.Workers} 并行，{workspace / 1024 / 1024} MiB 工作区）", Total: pages.Count));
            await OrderedImagePipeline.RunAsync(PreparePageWork(generation, previous, pages, sources, token),
                () => new ExtractionWorker(backend.Profile, root, rebuild, options.UseSimd),
                (worker, work, ct) => ExtractPageAsync(worker, work, backend.Profile, options, budget, counters, ct),
                CommitPage, options.Workers, options.Workers * 2, token).ConfigureAwait(false);
            Flush();
            return new(processed, indexed, skipped, failed, reused, needsBuild);
        }
        finally
        {
            try
            {
                // 正常取消也保存已按序消费的不足一批页面；写入失败则完整回滚当前批。
                if (!writeFailed && !writer.IsFaulted) Flush();
            }
            finally
            {
                transaction?.Dispose();
                Volatile.Write(ref _statistics, new(options.Workers, options.BatchSize, workspace, budget.PeakBytes,
                    counters.Decoded, counters.Extracted, commits, counters.DecodeTicks * 1000d / Stopwatch.Frequency,
                    counters.ExtractTicks * 1000d / Stopwatch.Frequency, writeTicks * 1000d / Stopwatch.Frequency,
                    HashMilliseconds: counters.HashTicks * 1000d / Stopwatch.Frequency,
                    PeakParallelPages: counters.Peak, MemoryWaitMilliseconds: counters.MemoryWaitTicks * 1000d / Stopwatch.Frequency));
            }
        }
    }
}

using System.Globalization;
using EhViewerForPc.Core.Format;
using EhViewerForPc.Core.Imaging;
using System.Text;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Core.Download;

/// <summary>
/// Standalone download engine. It is deliberately independent from the library:
/// the download root is a normal folder, tasks are persisted separately, and the
/// resulting folders follow EhViewer's layout so the same directory can be
/// registered as a library source (that is what makes "downloads" a data source
/// the user can point anywhere, label and re-enable at will).
/// </summary>
public sealed partial class DownloadManager : IDisposable
{
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly EhClient _client;
    private readonly GalleryImageStore _images;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly List<DownloadTask> _tasks = [];
    private readonly List<DownloadArchiveEntry> _archive = [];
    private readonly Dictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _running = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deleteAfterCancel = new(StringComparer.Ordinal);

    private bool _disposed;
    private int _activeTasks;
    private bool _relocating;
    private int _archiveWriters;
    private string? _loadError;

    public DownloadManager(AppPaths paths, AppSettings settings, EhClient client, Action<string>? log = null, GalleryImageStore? images = null)
    {
        _paths = paths;
        _settings = settings;
        _client = client;
        _images = images ?? new GalleryImageStore(paths, client);
        _log = log;
        Load();
        RecoverRootMove();
    }

    /// <summary>Raised whenever a task's state/progress changed (any thread).</summary>
    public event EventHandler<DownloadTask>? TaskChanged;

    /// <summary>Raised when a task finishes successfully.</summary>
    public event EventHandler<DownloadTask>? TaskCompleted;

    /// <summary>Raised when tasks are added/removed or the archive changes.</summary>
    public event EventHandler? Changed;

    public string RootPath
    {
        get
        {
            string configured = _settings.Download.RootPath;
            return string.IsNullOrWhiteSpace(configured) ? _paths.DefaultDownloadDirectory : configured;
        }
    }

    public string? LoadError => _loadError;
    public bool IsRelocating { get { lock (_gate) return _relocating; } }

    /// <summary>Changes the destination of future tasks only. Existing task and
    /// archive paths continue to describe where their files actually reside.</summary>
    public void ChangeRootWithoutMoving(string target, bool registerAsSource, Action? saveSettings = null)
    {
        target = AppSettings.NormalizeRoot(target);
        if (PathIdentity.Comparer.Equals(target, _paths.BaseDirectory) || PathIdentity.IsDescendantOf(target, _paths.BaseDirectory) || PathIdentity.IsDescendantOf(_paths.BaseDirectory, target))
            throw new IOException("下载目录不能与应用数据目录重叠");
        if (File.Exists(target)) throw new IOException("下载路径指向一个文件，请选择文件夹");
        Directory.CreateDirectory(target);
        lock (_gate)
        {
            if (_relocating) throw new IOException("目录迁移尚未完成，请稍后更改保存位置");
            string oldRoot = RootPath, oldConfigured = _settings.Download.RootPath;
            bool oldRegistration = _settings.Download.AddAsSource;
            var sources = _settings.Sources.Select(s => s.Clone()).ToList();
            try
            {
                _settings.Download.RootPath = target;
                _settings.Download.AddAsSource = registerAsSource;
                var previous = _settings.Sources.FirstOrDefault(s => PathIdentity.Comparer.Equals(s.RootPath, oldRoot));
                if (!PathIdentity.Comparer.Equals(oldRoot, target) && previous?.Name == "下载目录")
                {
                    bool hasTasks = _archiveWriters != 0 || _tasks.Any(t => t.FolderPath.Length > 0 && PathIdentity.IsDescendantOf(t.FolderPath, oldRoot)) || _archive.Any(a => a.FolderPath.Length > 0 && PathIdentity.IsDescendantOf(a.FolderPath, oldRoot));
                    bool empty = false;
                    try { empty = Directory.Exists(oldRoot) && !Directory.EnumerateFileSystemEntries(oldRoot).Any(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    if (!hasTasks && empty) _settings.Sources.Remove(previous);
                    else previous.Name = "旧下载目录";
                }
                if (registerAsSource)
                {
                    var source = _settings.AddSource(target, "下载目录");
                    if (source.Name == "旧下载目录") source.Name = "下载目录";
                }
                if (saveSettings is not null) saveSettings();
                else if (!new SettingsStore(_paths).Save(_settings, out var error)) throw new IOException(error);
            }
            catch
            {
                _settings.Download.RootPath = oldConfigured;
                _settings.Download.AddAsSource = oldRegistration;
                _settings.Sources = sources;
                throw;
            }
        }
        RaiseChanged();
    }

    public IDisposable AcquireArchiveWrite()
    {
        lock (_gate)
        {
            if (_relocating) throw new IOException("下载目录正在迁移，请完成后再下载归档");
            _archiveWriters++;
            return new ArchiveWriteLease(this);
        }
    }
    private sealed class ArchiveWriteLease(DownloadManager manager) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) lock (manager._gate) manager._archiveWriters--; }
    }

    public async Task<bool> RelocateRootAsync(string target, IProgress<DownloadMoveProgress>? progress,
        CancellationToken token, Action? saveSettings = null)
    {
        string oldRoot = AppSettings.NormalizeRoot(RootPath);
        target = AppSettings.NormalizeRoot(target);
        if (PathIdentity.Comparer.Equals(oldRoot, target)) return true;
        Task[] workers;
        HashSet<string> resume;
        lock (_gate)
        {
            if (_relocating) throw new IOException("下载目录正在迁移");
            if (_archiveWriters != 0) throw new IOException("有归档正在下载，请等待归档完成后再迁移目录");
            _relocating = true;
            resume = _tasks.Where(t => t.State is DownloadState.Running or DownloadState.Pending).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            workers = _running.Values.ToArray();
            foreach (var cts in _cancellations.Values) cts.Cancel();
        }
        RaiseChanged();
        DownloadRootMover? move = null;
        var oldSources = _settings.Sources.Select(s => s.Clone()).ToList();
        var oldPreferences = new Dictionary<string, string>(_settings.PreferredSourceByWork, StringComparer.OrdinalIgnoreCase);
        string oldConfiguredRoot = _settings.Download.RootPath;
        bool metadataChanged = false;
        try
        {
            progress?.Report(new("正在暂停下载并等待文件写入结束", 0, 0));
            await Task.WhenAll(workers).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            move = await DownloadRootMover.PrepareAsync(_paths, oldRoot, target, progress, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            metadataChanged = true;
            lock (_gate)
            {
                RebaseRoot(oldRoot, target);
                if (!SaveLocked()) throw new IOException("无法保存迁移后的下载记录");
            }
            SaveSettings();
            move.Commit();
            progress?.Report(new("正在清理旧目录", 0, 0));
            return move.Cleanup();
        }
        catch
        {
            if (move is not null && !move.Journal.MetadataCommitted)
            {
                if (metadataChanged)
                {
                    try
                    {
                        lock (_gate)
                        {
                            RebaseRoot(target, oldRoot);
                            _settings.Download.RootPath = oldConfiguredRoot;
                            _settings.Sources = oldSources;
                            _settings.PreferredSourceByWork = oldPreferences;
                            if (!SaveLocked()) throw new IOException("无法恢复下载记录");
                        }
                        // Roll back files only after both old metadata documents are durable.
                        SaveSettings();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Keep runtime paths pointing at the existing prepared files.
                        // The journal will finish both metadata writes on restart.
                        lock (_gate) { RebaseRoot(oldRoot, target); SaveLocked(); }
                        throw new IOException("下载文件已保存在新目录，设置暂时无法保存；恢复写入权限后重新启动会继续同步。", ex);
                    }
                }
                move.Rollback();
            }
            throw;
        }
        finally
        {
            lock (_gate)
            {
                foreach (var task in _tasks)
                    if (resume.Contains(task.Id) && task.State != DownloadState.Completed) { task.State = DownloadState.Pending; task.Message = "等待中"; }
                _relocating = false;
                SaveLocked();
            }
            SchedulePump();
            RaiseChanged();
        }
        void SaveSettings()
        {
            if (saveSettings is not null) saveSettings();
            else if (!new SettingsStore(_paths).Save(_settings, out var error)) throw new IOException(error);
        }
    }

    private static string Rebase(string path, string source, string target) =>
        PathIdentity.Comparer.Equals(path, source) ? target :
        !string.IsNullOrEmpty(path) && PathIdentity.IsDescendantOf(path, source) ? Path.Combine(target, Path.GetRelativePath(source, path)) : path;

    private void RebaseRoot(string source, string target)
    {
        foreach (var task in _tasks)
        {
            task.FolderPath = Rebase(task.FolderPath, source, target);
            task.DestinationRoot = Rebase(task.DestinationRoot, source, target);
        }
        foreach (var item in _archive) item.FolderPath = Rebase(item.FolderPath, source, target);
        var oldSource = _settings.Sources.FirstOrDefault(s => PathIdentity.Comparer.Equals(s.RootPath, source));
        var duplicate = _settings.Sources.FirstOrDefault(s => PathIdentity.Comparer.Equals(s.RootPath, target));
        if (oldSource is not null && duplicate is not null && oldSource != duplicate)
        {
            foreach (var key in _settings.PreferredSourceByWork.Where(p => p.Value == duplicate.Id).Select(p => p.Key).ToArray()) _settings.PreferredSourceByWork[key] = oldSource.Id;
            _settings.Sources.Remove(duplicate);
        }
        foreach (var item in _settings.Sources) item.RootPath = Rebase(item.RootPath, source, target);
        _settings.Download.RootPath = target;
        if (_settings.Download.AddAsSource && !_settings.Sources.Any(s => PathIdentity.Comparer.Equals(s.RootPath, target))) _settings.AddSource(target, "下载目录");
    }

    private void RecoverRootMove()
    {
        var pending = DownloadRootMover.OpenPending(_paths);
        if (pending is null) return;
        if (pending.CompletePreparedFiles())
        {
            RebaseRoot(pending.Journal.Source, pending.Journal.Target);
            if (!SaveLocked() || !new SettingsStore(_paths).Save(_settings, out _)) throw new IOException("下载目录迁移恢复尚未完成，请检查应用数据目录是否可写");
            pending.Commit();
            if (!pending.Cleanup()) _log?.Invoke("下载已迁移，旧目录含变化或占用的文件，已保留：" + pending.Journal.Source);
        }
        else pending.Rollback();
    }

    public int ActiveCount
    {
        get
        {
            lock (_gate) return _activeTasks;
        }
    }

    public IReadOnlyList<DownloadTask> Tasks
    {
        get
        {
            lock (_gate) return _tasks.ToList();
        }
    }

    public IReadOnlyList<DownloadArchiveEntry> Archive
    {
        get
        {
            lock (_gate) return _archive.ToList();
        }
    }

    public DownloadTask? FindTask(long gid)
    {
        lock (_gate) return _tasks.FirstOrDefault(t => t.Gid == gid);
    }

    public bool IsQueued(long gid)
    {
        lock (_gate) return _tasks.Any(t => t.Gid == gid);
    }

    public DownloadArchiveEntry? FindInArchive(long gid)
    {
        lock (_gate) return _archive.FirstOrDefault(e => e.Gid == gid);
    }

    public string? FindPage(long gid, int page, ImageQuality quality, int resampledWidth)
    {
        string folder;
        lock (_gate)
        {
            var task = _tasks.FirstOrDefault(t => t.Gid == gid && t.Quality == quality && (quality == ImageQuality.Original || t.ResampledWidth == resampledWidth) &&
                (quality != ImageQuality.Original || !t.ResampledPages.Contains(page)));
            if (task is null) return null;
            folder = task.FolderPath;
        }
        string prefix = page.ToString("D8", CultureInfo.InvariantCulture);
        foreach (string ext in new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" })
        {
            string file = Path.Combine(folder, prefix + ext);
            if (ImageSignature.IsValidFile(file)) return file;
        }
        return null;
    }

    // --------------------------------------------------------------- queueing

    public DownloadTask Enqueue(
        EhGalleryDetails details,
        string? label = null,
        bool startImmediately = true,
        ImageQuality? quality = null)
    {
        DownloadTask task;
        bool changed = false;
        lock (_gate)
        {
            DownloadTask? existing = _tasks.FirstOrDefault(t => t.Gid == details.Gid);
            if (existing is not null)
            {
                if (existing.State is DownloadState.Completed or DownloadState.Running or DownloadState.Pending)
                {
                    return existing;
                }

                existing.State = DownloadState.Pending;
                existing.Message = "等待中";
                task = existing;
                changed = true;
            }
            else
            {
                task = new DownloadTask
                {
                    Gid = details.Gid,
                    Token = details.Token,
                    Title = details.DisplayTitle,
                    Category = details.Category,
                    ThumbUrl = details.ThumbUrl,
                    Label = (label ?? _settings.Download.DefaultLabel).Trim(),
                    Quality = quality ?? _settings.ImageQuality,
                    ResampledWidth = _settings.ResampledWidth,
                    PageCount = details.Pages,
                    State = DownloadState.Pending,
                    Message = "等待中",
                };

                task.DestinationRoot = RootPath;
                task.FolderPath = BuildFolderPath(task, RootPath);
                _tasks.Insert(0, task);
                changed = true;
            }

            SaveLocked();
        }

        if (changed) RaiseChanged();
        RaiseTask(task);
        if (startImmediately) SchedulePump();
        return task;
    }

    public void Pause(DownloadTask task)
    {
        lock (_gate)
        {
            if (task.State is DownloadState.Completed) return;
            task.State = DownloadState.Paused;
            task.Message = "已暂停";
            if (_cancellations.TryGetValue(task.Id, out CancellationTokenSource? cts))
            {
                cts.Cancel();
            }

            SaveLocked();
        }

        RaiseTask(task);
        RaiseChanged();
    }

    public void Resume(DownloadTask task)
    {
        lock (_gate)
        {
            if (!_tasks.Contains(task) || task.State is DownloadState.Completed or DownloadState.Running or DownloadState.Pending) return;
            task.State = DownloadState.Pending;
            task.Message = "等待中";
            task.FailedPages.Clear();
            SaveLocked();
        }

        RaiseTask(task);
        RaiseChanged();
        SchedulePump();
    }

    public void Retry(DownloadTask task) => Resume(task);

    public void Remove(DownloadTask task, bool deleteFiles)
    {
        if (deleteFiles && task.FillMissingOnly) throw new InvalidOperationException("补页任务保留原目录，请只移除任务。");
        bool deleteNow;
        lock (_gate)
        {
            if (_relocating) return;
            _tasks.Remove(task);
            bool active = _cancellations.TryGetValue(task.Id, out var cts);
            if (active && deleteFiles) _deleteAfterCancel.Add(task.Id);
            cts?.Cancel();
            deleteNow = deleteFiles && !active;
            if (deleteFiles) _archive.RemoveAll(e => PathIdentity.Comparer.Equals(e.FolderPath, task.FolderPath));
            SaveLocked();
        }
        // Running writers must have closed their files before deletion.
        if (deleteNow) TryDeleteFolder(task.FolderPath, task.DestinationRoot);
        RaiseChanged();
    }

    public void StartAll()
    {
        lock (_gate)
        {
            foreach (DownloadTask task in _tasks)
            {
                if (task.State is DownloadState.Paused or DownloadState.Failed)
                {
                    task.State = DownloadState.Pending;
                    task.Message = "等待中";
                }
            }

            SaveLocked();
        }

        RaiseChanged();
        SchedulePump();
    }

    public void PauseAll()
    {
        lock (_gate)
        {
            foreach (DownloadTask task in _tasks)
            {
                if (task.State is DownloadState.Pending or DownloadState.Running)
                {
                    task.State = DownloadState.Paused;
                    task.Message = "已暂停";
                    if (_cancellations.TryGetValue(task.Id, out CancellationTokenSource? cts)) cts.Cancel();
                }
            }

            SaveLocked();
        }

        RaiseChanged();
    }

    public void ClearFinished()
    {
        lock (_gate)
        {
            _tasks.RemoveAll(t => t.State == DownloadState.Completed);
            SaveLocked();
        }

        RaiseChanged();
    }

    public void ClearArchive()
    {
        lock (_gate)
        {
            _archive.Clear();
            SaveLocked();
        }

        RaiseChanged();
    }

    /// <summary>Starts pending tasks (called once at startup when AutoStart is on).</summary>
    public void AutoStart()
    {
        if (!_settings.Download.AutoStart) return;
        lock (_gate)
        {
            foreach (DownloadTask task in _tasks)
            {
                if (task.State is DownloadState.Running)
                {
                    // A previous process died mid-task; treat it as pending.
                    task.State = DownloadState.Pending;
                    task.Message = "等待中";
                }
            }
        }

        SchedulePump();
    }

    // ------------------------------------------------------------------ pump

    private void SchedulePump()
    {
        lock (_gate)
        {
            if (_disposed || _relocating) return;
            int limit = Math.Clamp(_settings.Download.MaxConcurrentTasks, 1, 8);
            while (_activeTasks < limit)
            {
                DownloadTask? next = _tasks.FirstOrDefault(t => t.State == DownloadState.Pending && !_cancellations.ContainsKey(t.Id));
                if (next is null) break;
                var cts = new CancellationTokenSource();
                _cancellations.Add(next.Id, cts);
                _activeTasks++;
                next.State = DownloadState.Running;
                next.Message = "准备中";
                // Reserve both the slot and cancellation before the worker can run.
                _running[next.Id] = Task.Run(() => RunTaskAsync(next, cts));
            }
        }
        RaiseChanged();
    }

    private async Task RunTaskAsync(DownloadTask task, CancellationTokenSource cts)
    {
        CancellationToken token = cts.Token;
        var meter = new DownloadTransferMeter();
        task.BytesPerSecond = 0;
        using var samplingStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task sampling = SampleSpeedAsync(task, meter, samplingStop.Token);
        try
        {
            await ExecuteAsync(task, meter, token).ConfigureAwait(false);

            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (!_tasks.Contains(task)) return;
                task.State = DownloadState.Completed;
                task.Message = task.ResampledPages.Count == 0 ? "已完成" : $"已完成（{task.ResampledPages.Count} 页使用压缩图）";
                task.FinishedUtc = DateTime.UtcNow.ToString("O");
                task.TotalBytes = FolderSize(task.FolderPath);
                _archive.RemoveAll(e => PathIdentity.Comparer.Equals(e.FolderPath, task.FolderPath));
                _archive.Insert(0, new DownloadArchiveEntry
                {
                    Gid = task.Gid,
                    Title = task.Title,
                    Category = task.Category,
                    Label = task.Label,
                    FolderPath = task.FolderPath,
                    PageCount = task.PageCount,
                    TotalBytes = task.TotalBytes,
                    FinishedUtc = task.FinishedUtc!,
                });
                SaveLocked();
            }

            RaiseTask(task);
            TaskCompleted?.Invoke(this, task);
            RaiseChanged();
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (task.State == DownloadState.Running)
                {
                    task.State = DownloadState.Paused;
                    task.Message = "已暂停";
                }

                SaveLocked();
            }

            RaiseTask(task);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Download failed ({task.Gid}): {ex}");
            lock (_gate)
            {
                if (!_tasks.Contains(task)) return;
                task.State = DownloadState.Failed;
                task.Message = ex.Message;
                SaveLocked();
            }

            RaiseTask(task);
            RaiseChanged();
        }
        finally
        {
            samplingStop.Cancel();
            try { await sampling.ConfigureAwait(false); }
            catch (Exception error) { _log?.Invoke("Download speed sampling failed: " + error.Message); }
            task.BytesPerSecond = 0;
            bool delete;
            lock (_gate)
            {
                _cancellations.Remove(task.Id);
                _running.Remove(task.Id);
                _activeTasks--;
                delete = _deleteAfterCancel.Remove(task.Id);
            }
            cts.Dispose();
            if (delete) TryDeleteFolder(task.FolderPath, task.DestinationRoot);
            SchedulePump();
        }
    }

    private async Task ExecuteAsync(DownloadTask task, DownloadTransferMeter meter, CancellationToken token)
    {
        if (task.FillMissingOnly) ValidateRepairTarget(task.FolderPath, task.DestinationRoot);
        else Directory.CreateDirectory(RootPath);
        if (task.FolderPath.Length == 0)
        {
            task.FolderPath = BuildFolderPath(task, RootPath);
        }

        if (!task.FillMissingOnly) Directory.CreateDirectory(task.FolderPath);

        task.Message = "获取页面列表";
        RaiseTask(task);

        if (task.FillMissingOnly)
        {
            var details = await _client.GetDetailsAsync(task.Gid, task.Token, token).ConfigureAwait(false);
            if (details.Pages > 0) task.PageCount = details.Pages;
        }

        IReadOnlyList<EhPageRef> pages = await _client
            .GetAllPagesAsync(task.Gid, task.Token, task.PageCount, null, token)
            .ConfigureAwait(false);

        int expected = task.PageCount;
        if (pages.Count == 0 || (expected > 0 &&
            (pages.Count != expected || !pages.Select(p => p.Page).Order().SequenceEqual(Enumerable.Range(1, expected)))))
        {
            throw new EhException($"页面列表不完整：预期 {expected} 页，取得 {pages.Count} 页，请重试");
        }

        lock (_gate)
        {
            task.PageCount = pages.Count;
            SaveLocked();
        }

        if (_settings.Download.WriteEhviewerHeader && !task.FillMissingOnly)
            await File.WriteAllTextAsync(Path.Combine(task.FolderPath, ".ehviewer"), BuildEhviewerHeader(task), new UTF8Encoding(false), token).ConfigureAwait(false);

        var repairPages = task.FillMissingOnly ? ReadExistingRepairPages(task.FolderPath) : null;
        bool HasPage(int page) => repairPages?.Contains(page) ?? HasPageFile(task.FolderPath, page);
        int already = pages.Count(p => HasPage(p.Page));
        task.DownloadedPages = already;
        task.Message = "下载中";
        RaiseTask(task);

        int maxPages = Math.Clamp(_settings.Download.MaxConcurrentPages, 1, 8);
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxPages,
            CancellationToken = token,
        };

        int completed = already;
        var failed = new List<int>();
        var failedGate = new object();

        await Parallel.ForEachAsync(pages, options, async (page, innerToken) =>
        {
            string pageUrl = page.PageUrl.Length > 0 ? page.PageUrl : EhUrls.ImagePage(_client.Site, page);
            try
            {
                if (HasPage(page.Page)) return;

                var cached = await _images.GetAsync(page, task.Quality == ImageQuality.Original, innerToken, task.ResampledWidth, meter.ObservePage()).ConfigureAwait(false);
                string extension = ImageSignature.DetectFile(cached.Path) switch
                {
                    ImageKind.Png => ".png", ImageKind.Gif => ".gif", ImageKind.Webp => ".webp", _ => ".jpg"
                };
                string target = Path.Combine(task.FolderPath, page.Page.ToString("D8", CultureInfo.InvariantCulture) + extension);
                if (task.FillMissingOnly)
                {
                    ValidateRepairTarget(task.FolderPath, task.DestinationRoot);
                    target = GetRepairPagePath(task.FolderPath, page.Page, extension);
                }
                string temporary = task.FillMissingOnly ? target + "." + Guid.NewGuid().ToString("N") + ".part" : target + ".part";
                try
                {
                    await using (var input = File.OpenRead(cached.Path))
                    await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
                        await input.CopyToAsync(output, innerToken).ConfigureAwait(false);
                    innerToken.ThrowIfCancellationRequested();
                    File.Move(temporary, target, overwrite: !task.FillMissingOnly);
                }
                finally { try { File.Delete(temporary); } catch (IOException) { } }
                if (task.Quality == ImageQuality.Original && !cached.IsOriginal)
                    lock (_gate) { if (!task.ResampledPages.Contains(page.Page)) task.ResampledPages.Add(page.Page); }

                int done = Interlocked.Increment(ref completed);
                lock (_gate)
                {
                    task.DownloadedPages = done;
                }

                RaiseTask(task);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Page {page.Page} failed ({task.Gid}): {ex.Message}");
                lock (failedGate) failed.Add(page.Page);
            }
        }).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();

        var remaining = task.FillMissingOnly ? ReadExistingRepairPages(task.FolderPath) : null;
        if (remaining is not null)
            failed.AddRange(pages.Where(p => !remaining.Contains(p.Page)).Select(p => p.Page));
        lock (_gate)
        {
            task.FailedPages = failed.Distinct().OrderBy(p => p).ToList();
            task.DownloadedPages = pages.Count(p => remaining?.Contains(p.Page) ?? HasPageFile(task.FolderPath, p.Page));
            SaveLocked();
        }

        if (task.FailedPages.Count > 0)
        {
            throw new EhException($"{task.FailedPages.Count} 页下载失败（可重试）");
        }

        if (!task.FillMissingOnly) await WriteMetadataAsync(task, token).ConfigureAwait(false);
    }

    private async Task WriteMetadataAsync(DownloadTask task, CancellationToken token)
    {
        if (_settings.Download.WriteThumb && task.ThumbUrl.Length > 0)
        {
            string thumbPath = Path.Combine(task.FolderPath, ".thumb");
            if (!File.Exists(thumbPath) || new FileInfo(thumbPath).Length == 0)
            {
                try
                {
                    byte[] bytes = await _client
                        .DownloadBytesAsync(task.ThumbUrl, EhUrls.Base(_client.Site) + "/", token)
                        .ConfigureAwait(false);
                    await File.WriteAllBytesAsync(thumbPath, bytes, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is EhException or HttpRequestException or IOException)
                {
                    _log?.Invoke($"Thumb download failed ({task.Gid}): {ex.Message}");
                }
            }
        }

        if (_settings.Download.WriteEhviewerHeader)
        {
            string header = BuildEhviewerHeader(task);
            await File.WriteAllTextAsync(Path.Combine(task.FolderPath, ".ehviewer"), header, new UTF8Encoding(false), token)
                .ConfigureAwait(false);
        }
    }

    /// <summary>VERSION2 header understood by EhViewer and by this app's parser.</summary>
    private static string BuildEhviewerHeader(DownloadTask task)
    {
        var builder = new StringBuilder();
        builder.AppendLine("VERSION2");
        builder.AppendLine("0");                       // startPage (hex)
        builder.AppendLine(task.Gid.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine(task.Token);
        builder.AppendLine("0");                       // ignored
        builder.AppendLine("0");                       // previewPages
        builder.AppendLine("0");                       // previewPerPage
        builder.AppendLine(task.PageCount.ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    // --------------------------------------------------------------- helpers

    public static string BuildFolderPath(DownloadTask task, string root)
    {
        if (task.FolderPath.Length > 0) return task.FolderPath;
        string labelFolder = SanitizeLabel(task.Label);
        string name = SanitizeFolderName($"{task.Gid}-{task.DisplayTitle}");
        return labelFolder.Length == 0
            ? Path.Combine(root, name)
            : Path.Combine(root, labelFolder, name);
    }

    private static string SanitizeLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return string.Empty;
        string cleaned = SanitizeFolderName(label);
        return cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }

    public static string SanitizeFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "untitled";
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (char ch in name)
        {
            builder.Append(invalid.Contains(ch) || ch is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
                ? '_'
                : ch);
        }

        string result = builder.ToString().Trim().TrimEnd('.');
        while (result.Contains("  ", StringComparison.Ordinal)) result = result.Replace("  ", " ");
        if (result.Length == 0) result = "untitled";
        if (result.Length > 120) result = result[..120].TrimEnd();
        return result;
    }

    private static string GuessExtension(string url)
    {
        string path = url;
        int query = path.IndexOfAny(['?', '#']);
        if (query >= 0) path = path[..query];
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" => extension,
            _ => ".jpg",
        };
    }

    private static bool HasPageFile(string folder, int pageNumber)
    {
        string prefix = pageNumber.ToString("D8", CultureInfo.InvariantCulture);
        foreach (string candidate in new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" })
        {
            string path = Path.Combine(folder, prefix + candidate);
            try
            {
                if (ImageSignature.IsValidFile(path)) return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        return false;
    }

    private static int CountExistingPages(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return 0;
            int count = 0;
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (name.Length < 10 || name[8] != '.') continue;
                bool digits = true;
                for (int i = 0; i < 8; i++)
                {
                    if (name[i] is < '0' or > '9')
                    {
                        digits = false;
                        break;
                    }
                }

                if (!digits) continue;
                if (new FileInfo(file).Length > 0) count++;
            }

            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long FolderSize(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return 0;
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
            {
                total += new FileInfo(file).Length;
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void TryDeleteFolder(string folder, string destinationRoot)
    {
        try
        {
            if (folder.Length == 0 || !Directory.Exists(folder)) return;
            string root = Path.GetFullPath(string.IsNullOrEmpty(destinationRoot) ? RootPath : destinationRoot);
            string target = Path.GetFullPath(folder);
            if (!PathIdentity.IsDescendantOf(target, root)) return;
            Directory.Delete(target, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke("Delete download folder failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------ durability

    private void Load()
    {
        DownloadFile file = JsonFileStore.LoadOrDefault(
            _paths.DownloadTasksFile,
            StorageJsonContext.Default.DownloadFile,
            () => new DownloadFile(),
            out string? error);

        _loadError = error;
        if (file.SchemaVersion != 1)
        {
            file = new DownloadFile();
        }

        lock (_gate)
        {
            _tasks.Clear();
            _archive.Clear();
            foreach (DownloadTask task in file.Tasks)
            {
                if (task.State == DownloadState.Running) task.State = DownloadState.Pending;
                task.FailedPages ??= [];
                task.ResampledPages ??= [];
                _tasks.Add(task);
            }

            _archive.AddRange(file.Archive);
        }
    }

    private bool SaveLocked()
    {
        var file = new DownloadFile
        {
            Tasks = _tasks.ToList(),
            Archive = _archive.ToList(),
        };

        bool saved = JsonFileStore.Save(_paths.DownloadTasksFile, file, StorageJsonContext.Default.DownloadFile, out string? error);
        if (!saved && error is not null)
        {
            _log?.Invoke("Download state save failed: " + error);
        }
        return saved;
    }

    private void RaiseTask(DownloadTask task)
    {
        lock (_gate) { if (!_tasks.Contains(task)) return; }
        TaskChanged?.Invoke(this, task);
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (CancellationTokenSource cts in _cancellations.Values)
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Ignore a race with task completion.
                }
            }

            SaveLocked();
        }

        // Workers own their cancellation sources until they have released all I/O.
        Task[] pending;
        lock (_gate) pending = _running.Values.ToArray();
        try { Task.WhenAll(pending).GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
    }
}

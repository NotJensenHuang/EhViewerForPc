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
    /// <summary>单页尝试次数。页级失败多为图床节点抖动或响应截断，重试一次
    /// 往往就能成功，但失败页过多时不能让整本无限等待。</summary>
    private const int PageAttempts = 3;
    private const int PageRetryDelayMilliseconds = 400;

    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly EhClient _client;
    private readonly GalleryImageStore _images;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly List<DownloadTask> _tasks = [];
    private readonly List<DownloadArchiveEntry> _archive = [];
    private readonly Dictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DownloadTask Download, Task Work)> _running = new(StringComparer.Ordinal);
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
            if (_relocating || _localDeletes.Count != 0) throw new IOException("目录操作进行中，请完成后再下载归档");
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
            if (_localDeletes.Count != 0) throw new IOException("本地副本正在删除，请完成后再迁移目录。");
            if (_pageRetries.Count != 0) throw new IOException("正在重新下载阅读页，请等待完成后再迁移目录");
            _relocating = true;
            resume = _tasks.Where(t => t.State is DownloadState.Running or DownloadState.Pending).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            workers = _running.Values.Select(writer => writer.Work).ToArray();
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
                if (IsLocalCopyDeleting(existing.FolderPath)) throw new IOException("该副本正在删除，无法加入下载。");
                if (existing.State is DownloadState.Completed or DownloadState.Running or DownloadState.Pending)
                {
                    return existing;
                }

                existing.State = DownloadState.Pending;
                existing.Message = "等待中";
                // 用户主动重新入队即视为重试，撤销「画廊不可用」标记。
                existing.GalleryUnavailable = false;
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
                if (IsLocalCopyDeleting(task.FolderPath)) throw new IOException("该副本正在删除，无法加入下载。");
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
            if (IsLocalCopyDeleting(task.FolderPath)) throw new IOException("该副本正在删除，无法继续下载。");
            if (!_tasks.Contains(task) || task.State is DownloadState.Completed or DownloadState.Running or DownloadState.Pending) return;
            task.State = DownloadState.Pending;
            task.Message = "等待中";
            task.FailedPages.Clear();
            // 显式重试：先撤销不可用标记，否则任务会继续被自动调度跳过。
            task.GalleryUnavailable = false;
            SaveLocked();
        }

        RaiseTask(task);
        RaiseChanged();
        SchedulePump();
    }

    /// <summary>
    /// 将任务归入「画廊不可用」分类：站点已明确回答该画廊不可用，自动开始与
    /// 重试都会跳过它；文件、进度和归档记录不变，用户移除标记或重新入队后恢复。
    /// </summary>
    public void MarkGalleryUnavailable(DownloadTask task)
    {
        lock (_gate)
        {
            if (!_tasks.Contains(task) || task.GalleryUnavailable) return;
            // 只归类失败任务：运行中的任务应继续走正常失败 / 暂停流程。
            if (task.State != DownloadState.Failed) return;
            task.GalleryUnavailable = true;
            SaveLocked();
        }

        RaiseTask(task);
        RaiseChanged();
    }

    /// <summary>
    /// 移除「画廊不可用」标记，让任务回到普通下载队列。任务保持原来的失败状态
    /// 与理由，用户可继续 / 重试，构建失败时不会悄悄变成成功。
    /// </summary>
    public void ClearGalleryUnavailable(DownloadTask task)
    {
        lock (_gate)
        {
            if (!_tasks.Contains(task) || !task.GalleryUnavailable) return;
            task.GalleryUnavailable = false;
            SaveLocked();
        }

        RaiseTask(task);
        RaiseChanged();
    }

    public void Retry(DownloadTask task) => Resume(task);

    public void Remove(DownloadTask task, bool deleteFiles)
    {
        if (deleteFiles && task.FillMissingOnly) throw new InvalidOperationException("补页任务保留原目录，请只移除任务。");
        bool deleteNow;
        lock (_gate)
        {
            if (deleteFiles && IsLocalCopyDeleting(task.FolderPath)) throw new IOException("该副本正在删除。");
            if (deleteFiles && _pageRetries.ContainsKey(task.FolderPath)) throw new IOException("正在重新下载阅读页，请等待写入完成后再删除。");
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
                if (task.GalleryUnavailable) continue;
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
        _ = RefreshRepairProgressAsync();
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

    private bool CanStartDownloadLocked(DownloadTask task)
    {
        if (task.State != DownloadState.Pending || task.GalleryUnavailable || _cancellations.ContainsKey(task.Id) ||
            _pageRetries.ContainsKey(task.FolderPath) || IsLocalCopyDeleting(task.FolderPath)) return false;
        foreach (var writer in _running.Values)
            if (FoldersOverlap(writer.Download.FolderPath, task.FolderPath)) return false;
        return true;
    }

    private void SchedulePump()
    {
        bool started = false;
        lock (_gate)
        {
            if (_disposed || _relocating) return;
            foreach (var task in _tasks)
                if (task.State == DownloadState.Pending && _pageRetries.TryGetValue(task.FolderPath, out var retry)) retry.ResumeQueue = true;
            int limit = Math.Clamp(_settings.Download.MaxConcurrentTasks, 1, 8);
            while (_activeTasks < limit)
            {
                DownloadTask? next = _tasks.FirstOrDefault(CanStartDownloadLocked);
                if (next is null) break;
                var cts = new CancellationTokenSource();
                _cancellations.Add(next.Id, cts);
                _activeTasks++;
                next.State = DownloadState.Running;
                next.Message = "准备中";
                // Reserve both the slot and cancellation before the worker can run.
                _running[next.Id] = (next, Task.Run(() => RunTaskAsync(next, cts)));
                started = true;
            }
        }
        if (started) RaiseChanged();
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
                MarkCompletedLocked(task, task.ResampledPages.Count == 0
                    ? "已完成"
                    : $"已完成（{task.ResampledPages.Count} 页使用压缩图）");
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
            bool quota = ex is EhException { QuotaExceeded: true };
            lock (_gate)
            {
                if (!_tasks.Contains(task)) return;
                // 配额不足是可恢复的外部条件：暂停而不是失败，用户不会以为页面坏了
                // 反复重试，自动调度也不会立刻把下一次请求又打上去。
                task.State = quota ? DownloadState.Paused : DownloadState.Failed;
                task.Message = quota ? "站点返回 509（配额已用尽或节点限流），已暂停；稍后点继续" : ex.Message;
                if (EhParser.IsGalleryUnavailableMessage(ex.Message)) task.GalleryUnavailable = true;
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
        if (task.FillMissingOnly)
            task.DownloadedPages = ReadExistingRepairPages(task.FolderPath, token).Count(page => task.PageCount <= 0 || page <= task.PageCount);
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

                var cached = await LoadPageImageAsync(page, task, meter, innerToken).ConfigureAwait(false);
                string extension = ImageSignature.DetectFile(cached.Path) switch
                {
                    ImageKind.Png => ".png", ImageKind.Gif => ".gif", ImageKind.Webp => ".webp", _ => ".jpg"
                };
                string target = Path.Combine(task.FolderPath, page.Page.ToString("D8", CultureInfo.InvariantCulture) + extension);
                bool replaceInvalid = false;
                if (task.FillMissingOnly)
                {
                    ValidateRepairTarget(task.FolderPath, task.DestinationRoot);
                    (target, replaceInvalid) = GetRepairPagePath(task.FolderPath, page.Page, extension);
                }
                string temporary = task.FillMissingOnly ? target + "." + Guid.NewGuid().ToString("N") + ".part" : target + ".part";
                try
                {
                    await using (var input = File.OpenRead(cached.Path))
                    await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
                        await input.CopyToAsync(output, innerToken).ConfigureAwait(false);
                    innerToken.ThrowIfCancellationRequested();
                    File.Move(temporary, target, overwrite: !task.FillMissingOnly || replaceInvalid);
                }
                finally { try { File.Delete(temporary); } catch (IOException) { } }
                // 换容器重下时旧扩展名的坏文件会留下来，目录看着「已齐」但仍有坏页。
                RemoveInvalidSamePageFiles(task.FolderPath, page.Page, target);
                if (task.Quality == ImageQuality.Original && !cached.IsOriginal)
                    lock (_gate) { if (!task.ResampledPages.Contains(page.Page)) task.ResampledPages.Add(page.Page); }

                int done = Interlocked.Increment(ref completed);
                lock (_gate)
                {
                    task.DownloadedPages = done;
                }

                RaiseTask(task);
            }
            catch (OperationCanceledException) when (innerToken.IsCancellationRequested)
            {
                throw;
            }
            catch (EhException ex) when (ex.QuotaExceeded)
            {
                // 配额用尽后继续逐页请求只会把剩余配额也打光，直接停下整批。
                _log?.Invoke($"Gallery {task.Gid} stopped at page {page.Page}: {ex.Message}");
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
            // 这次已经写成功的页不再算损坏（补页允许替换无效文件）。
            task.CorruptPages.RemoveAll(page => !task.FailedPages.Contains(page) && HasPageFile(task.FolderPath, page));
            task.DownloadedPages = pages.Count(p => remaining?.Contains(p.Page) ?? HasPageFile(task.FolderPath, p.Page));
            SaveLocked();
        }

        if (task.FailedPages.Count > 0)
        {
            int corrupt = task.CorruptPages.Count(page => task.FailedPages.Contains(page));
            throw new EhException(DescribePageState(task.FailedPages.Count - corrupt, corrupt));
        }

        if (!task.FillMissingOnly) await WriteMetadataAsync(task, token).ConfigureAwait(false);
    }

    /// <summary>
    /// 获取一页的本地文件。失败后按固定次数重试，并在重试时绕过解析缓存与
    /// 在线图片缓存，让站点换一个图床节点；只有外部真正取消时才中止整批下载。
    /// HttpClient 超时同样表现为 <see cref="OperationCanceledException"/>，
    /// 但调用方的 token 未被取消，因此必须按可重试失败处理。
    /// </summary>
    private async Task<CachedGalleryImage> LoadPageImageAsync(
        EhPageRef page, DownloadTask task, DownloadTransferMeter meter, CancellationToken token)
    {
        IProgress<ImageLoadStatus>? progress = meter.ObservePage();
        Exception? last = null;
        for (int attempt = 1; attempt <= PageAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return await _images
                    .GetAsync(page, task.Quality == ImageQuality.Original, token, task.ResampledWidth, progress, refresh: attempt > 1)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (EhException ex) when (ex.QuotaExceeded)
            {
                // 配额类失败再试只会继续消耗站点配额，交给整批流程停下。
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                _log?.Invoke($"Page {page.Page} attempt {attempt}/{PageAttempts} failed ({task.Gid}): {ex.Message}");
                if (attempt == PageAttempts) break;
                await Task.Delay(PageRetryDelayMilliseconds * attempt, token).ConfigureAwait(false);
            }
        }

        throw last ?? new EhException($"第 {page.Page} 页下载失败");
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

    /// <summary>
    /// 标记任务完成并写入归档。调用方必须已持有 <see cref="_gate"/>。
    /// 正常收尾与「本地页文件已补齐」的核对共用这一段，避免两套完成语义。
    /// </summary>
    private void MarkCompletedLocked(DownloadTask task, string message)
    {
        task.State = DownloadState.Completed;
        task.Message = message;
        task.CorruptPages.Clear();
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

    /// <summary>
    /// 按任务目录的实际页文件重算进度。用户在阅读器逐页重试或从外部补齐后，
    /// 失败页、页数与消息不应继续停留在旧状态；只读取目录，不修改任何文件。
    /// 缺失页与损坏页分开统计：前者没有文件，后者有文件但容器不完整。
    /// 目录枚举与容器校验放在后台线程，避免大画廊卡住 UI。
    /// </summary>
    public async Task<bool> RefreshLocalProgressAsync(DownloadTask task, CancellationToken token = default)
    {
        string folder = task.FolderPath;
        int expected = task.PageCount;
        if (folder.Length == 0 || expected <= 0) return false;

        (HashSet<int> Present, List<int> Corrupt) state;
        try
        {
            state = await Task.Run(() => ClassifyLocalPages(folder, expected, token), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Reconcile local pages failed ({task.Gid}): {ex.Message}");
            return false;
        }

        HashSet<int> present = state.Present;
        List<int> corruptPages = state.Corrupt;
        var corruptSet = corruptPages.ToHashSet();
        var missing = Enumerable.Range(1, expected).Where(page => !present.Contains(page) && !corruptSet.Contains(page)).ToList();
        // 补页要覆盖两类页：没有任何文件的，以及文件损坏需要替换的。
        var failed = corruptPages.Concat(missing).OrderBy(page => page).ToList();

        bool completed = false;
        lock (_gate)
        {
            // 运行中的任务由写入流程自己维护计数，核对会与在途文件冲突。
            if (!_tasks.Contains(task) || task.State == DownloadState.Running) return false;
            bool changed = task.DownloadedPages != present.Count ||
                           !task.CorruptPages.SequenceEqual(corruptPages) ||
                           !task.FailedPages.SequenceEqual(failed);
            task.DownloadedPages = present.Count;
            task.CorruptPages = corruptPages;
            task.FailedPages = failed;
            if (present.Count >= expected)
            {
                MarkCompletedLocked(task, "已完成（按本地页文件核对）");
                completed = true;
            }
            else if (changed)
            {
                // 失败任务的文案必须跟着实际分类走；暂停任务可能带着 509 说明，
                // 不能用「N 页缺失」覆盖掉配额提示。
                if (task.State == DownloadState.Failed) task.Message = DescribePageState(missing.Count, corruptPages.Count);
                SaveLocked();
            }

            if (!changed && !completed) return false;
        }

        RaiseTask(task);
        if (completed) TaskCompleted?.Invoke(this, task);
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 目录里每一页的真实状态：有效页、损坏页（有字节但容器不完整）。0 字节
    /// 文件按缺失处理——它没有任何可恢复内容，重下就是新增。
    /// </summary>
    private static (HashSet<int> Present, List<int> Corrupt) ClassifyLocalPages(string folder, int expected, CancellationToken token)
    {
        var present = new HashSet<int>();
        var corrupt = new HashSet<int>();
        if (!Directory.Exists(folder)) return (present, []);

        var files = PageFileParser.Enumerate(folder, out string? error);
        if (error is not null) throw new IOException(error);
        foreach (PageCandidate candidate in files)
        {
            token.ThrowIfCancellationRequested();
            if (!PageFileParser.TryParsePageName(candidate.FileName, out int page, out _)) continue;
            if (page <= 0 || page > expected || present.Contains(page)) continue;
            if (candidate.Length <= 0) continue;
            // 共享读：正在被阅读器或另一个写入方打开的文件不是损坏页。
            if (ImageSignature.IsValidFile(candidate.FilePath, FileShare.ReadWrite | FileShare.Delete))
            {
                present.Add(page);
                corrupt.Remove(page);
            }
            else
            {
                corrupt.Add(page);
            }
        }

        return (present, corrupt.OrderBy(page => page).ToList());
    }

    /// <summary>失败页数量到卡片文案的唯一映射，下载、核对、补页共用。</summary>
    public static string DescribePageState(int missing, int corrupt) => (missing, corrupt) switch
    {
        (0, 0) => "已完成",
        (0, _) => $"{corrupt} 页文件损坏（可重试）",
        (_, 0) => $"{missing} 页下载失败（可重试）",
        _ => $"{missing} 页缺失、{corrupt} 页文件损坏（可重试）",
    };

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
                // 只有失败任务可能带有不可用标记；其它状态一律清掉，避免
                // 被跳过的等待任务永远轮不到调度。
                if (task.GalleryUnavailable && task.State != DownloadState.Failed) task.GalleryUnavailable = false;
                task.FailedPages ??= [];
                task.CorruptPages ??= [];
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
        _repairProgressStop.Cancel();
        _pageRetryStop.Cancel();
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
        lock (_gate) pending = _running.Values.Select(writer => writer.Work).Concat(_pageRetries.Values.Select(p => p.Work)).Concat(_localDeletes.Values.Select(d => d.Task)).Append(_repairProgressTask).ToArray();
        try { Task.WhenAll(pending).GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        finally
        {
            _repairProgressStop.Dispose();
            _pageRetryStop.Dispose();
        }
    }
}

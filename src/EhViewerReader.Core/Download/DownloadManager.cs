using System.Globalization;
using System.Text;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Online;
using EhViewerReader.Core.Storage;

namespace EhViewerReader.Core.Download;

/// <summary>
/// Standalone download engine. It is deliberately independent from the library:
/// the download root is a normal folder, tasks are persisted separately, and the
/// resulting folders follow EhViewer's layout so the same directory can be
/// registered as a library source (that is what makes "downloads" a data source
/// the user can point anywhere, label and re-enable at will).
/// </summary>
public sealed class DownloadManager : IDisposable
{
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly EhClient _client;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly List<DownloadTask> _tasks = [];
    private readonly List<DownloadArchiveEntry> _archive = [];
    private readonly Dictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _pumpGate = new(1, 1);

    private bool _disposed;
    private bool _pumpQueued;
    private int _activeTasks;
    private string? _loadError;

    public DownloadManager(AppPaths paths, AppSettings settings, EhClient client, Action<string>? log = null)
    {
        _paths = paths;
        _settings = settings;
        _client = client;
        _log = log;
        Load();
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

    public bool IsQueued(long gid)
    {
        lock (_gate) return _tasks.Any(t => t.Gid == gid);
    }

    public DownloadArchiveEntry? FindInArchive(long gid)
    {
        lock (_gate) return _archive.FirstOrDefault(e => e.Gid == gid);
    }

    // --------------------------------------------------------------- queueing

    public DownloadTask Enqueue(EhGalleryDetails details, string? label = null, bool startImmediately = true)
    {
        DownloadTask task;
        bool changed = false;
        lock (_gate)
        {
            DownloadTask? existing = _tasks.FirstOrDefault(t => t.Gid == details.Gid);
            if (existing is not null)
            {
                if (existing.State == DownloadState.Completed)
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
                    PageCount = details.Pages,
                    State = DownloadState.Pending,
                    Message = "等待中",
                };

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
            if (task.State == DownloadState.Completed) return;
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
        lock (_gate)
        {
            if (_cancellations.TryGetValue(task.Id, out CancellationTokenSource? cts)) cts.Cancel();
            _tasks.Remove(task);
            SaveLocked();
        }

        if (deleteFiles)
        {
            TryDeleteFolder(task.FolderPath);
        }

        RaiseTask(task);
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
        if (_disposed) return;
        lock (_gate)
        {
            if (_pumpQueued) return;
            _pumpQueued = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await PumpAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke("Download pump failed: " + ex);
            }
            finally
            {
                lock (_gate) _pumpQueued = false;
            }
        });
    }

    private async Task PumpAsync()
    {
        await _pumpGate.WaitAsync().ConfigureAwait(false);
        try
        {
            while (!_disposed)
            {
                DownloadTask? next;
                lock (_gate)
                {
                    int limit = _settings.Download.MaxConcurrentTasks;
                    if (_activeTasks >= limit) return;
                    next = _tasks.FirstOrDefault(t => t.State == DownloadState.Pending);
                    if (next is null) return;
                    next.State = DownloadState.Running;
                    next.Message = "准备中";
                }

                RaiseTask(next);
                RaiseChanged();
                _ = Task.Run(() => RunTaskAsync(next));
            }
        }
        finally
        {
            _pumpGate.Release();
        }
    }

    private async Task RunTaskAsync(DownloadTask task)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_cancellations.ContainsKey(task.Id)) return;
            cts = new CancellationTokenSource();
            _cancellations[task.Id] = cts;
            _activeTasks++;
        }

        CancellationToken token = cts.Token;
        try
        {
            await ExecuteAsync(task, token).ConfigureAwait(false);

            lock (_gate)
            {
                task.State = DownloadState.Completed;
                task.Message = "已完成";
                task.FinishedUtc = DateTime.UtcNow.ToString("O");
                task.TotalBytes = FolderSize(task.FolderPath);
                _archive.RemoveAll(e => e.Gid == task.Gid);
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
                task.State = DownloadState.Failed;
                task.Message = ex.Message;
                SaveLocked();
            }

            RaiseTask(task);
            RaiseChanged();
        }
        finally
        {
            lock (_gate)
            {
                _cancellations.Remove(task.Id);
                _activeTasks--;
            }

            cts.Dispose();
            SchedulePump();
        }
    }

    private async Task ExecuteAsync(DownloadTask task, CancellationToken token)
    {
        Directory.CreateDirectory(RootPath);
        if (task.FolderPath.Length == 0)
        {
            task.FolderPath = BuildFolderPath(task, RootPath);
        }

        Directory.CreateDirectory(task.FolderPath);

        task.Message = "获取页面列表";
        RaiseTask(task);

        IReadOnlyList<EhPageRef> pages = await _client
            .GetAllPagesAsync(task.Gid, task.Token, task.PageCount, null, token)
            .ConfigureAwait(false);

        if (pages.Count == 0)
        {
            throw new EhException("页面列表为空");
        }

        lock (_gate)
        {
            task.PageCount = pages.Count;
            SaveLocked();
        }

        int already = CountExistingPages(task.FolderPath);
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
                if (HasPageFile(task.FolderPath, page.Page)) return;

                string imageUrl = await _client
                    .GetImageUrlAsync(page, _settings.Download.DownloadOriginal, innerToken)
                    .ConfigureAwait(false);

                string extension = GuessExtension(imageUrl);
                string target = Path.Combine(task.FolderPath, page.Page.ToString("D8", CultureInfo.InvariantCulture) + extension);
                await _client.DownloadToFileAsync(imageUrl, pageUrl, target, null, innerToken).ConfigureAwait(false);

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

        lock (_gate)
        {
            task.FailedPages = failed.OrderBy(p => p).ToList();
            task.DownloadedPages = CountExistingPages(task.FolderPath);
            SaveLocked();
        }

        if (failed.Count > 0)
        {
            throw new EhException($"{failed.Count} 页下载失败（可重试）");
        }

        await WriteMetadataAsync(task, token).ConfigureAwait(false);
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
                if (File.Exists(path) && new FileInfo(path).Length > 0) return true;
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

    private void TryDeleteFolder(string folder)
    {
        try
        {
            if (folder.Length == 0 || !Directory.Exists(folder)) return;
            string root = Path.GetFullPath(RootPath);
            string target = Path.GetFullPath(folder);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
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
                _tasks.Add(task);
            }

            _archive.AddRange(file.Archive);
        }
    }

    private void SaveLocked()
    {
        var file = new DownloadFile
        {
            Tasks = _tasks.ToList(),
            Archive = _archive.ToList(),
        };

        if (!JsonFileStore.Save(_paths.DownloadTasksFile, file, StorageJsonContext.Default.DownloadFile, out string? error) &&
            error is not null)
        {
            _log?.Invoke("Download state save failed: " + error);
        }
    }

    private void RaiseTask(DownloadTask task) => TaskChanged?.Invoke(this, task);

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

        _pumpGate.Dispose();
    }
}

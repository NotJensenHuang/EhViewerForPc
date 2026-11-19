using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Core.Download;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Services;

namespace EhViewerForPc.ViewModels;

/// <summary>
/// Download queue page. The engine is independent from the library; this view
/// model only mirrors its events on the UI thread and exposes the user actions
/// (pause/resume/retry/remove/open folder).
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly MainViewModel _library;
    private readonly Dictionary<string, DownloadTaskViewModel> _byId = new(StringComparer.Ordinal);
    private string _statusText = string.Empty;
    private bool _disposed;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DownloadTask> _dirtyTasks = new();
    private int _updateQueued;
    private readonly DispatcherTimer _libraryTimer;
    private readonly Dictionary<string, DownloadTask> _libraryUpdates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Pages, string Folder, int Total, bool Complete)> _imported = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _reconciledUtc = new(StringComparer.Ordinal);
    private bool _importing;

    public DownloadsViewModel(AppServices services, MainViewModel library)
    {
        _services = services;
        _library = library;
        _libraryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _libraryTimer.Tick += OnLibraryTick;

        _services.Downloads.TaskChanged += OnTaskChanged;
        _services.Downloads.Changed += OnChanged;
        _services.Downloads.TaskCompleted += OnTaskCompleted;

        SyncAll();
    }

    public ObservableCollection<DownloadTaskViewModel> Tasks { get; } = [];
    /// <summary>站点确认不可用的任务：不再自动重试，单独成页，文件与进度保留。</summary>
    public ObservableCollection<DownloadTaskViewModel> UnavailableTasks { get; } = [];
    public IReadOnlyList<DownloadArchiveViewModel> Archive { get; private set; } = [];
    public ObservableCollection<ArchiveViewModel> ArchiveTransfers { get; } = [];
    public event EventHandler<ArchiveViewModel>? ArchiveRequested;

    /// <summary>Opens the reader for a task's own folder (independent of the library).</summary>
    public event EventHandler<DownloadReadRequest>? ReadRequested;

    /// <summary>User-facing toast text (the shell owns the toast surface).</summary>
    public event EventHandler<string>? NotifyRequested;

    public ArchiveViewModel GetArchiveSession(EhGalleryDetails details, IOnlineHost host)
    {
        var existing = ArchiveTransfers.FirstOrDefault(a => a.Details.Gid == details.Gid);
        if (existing is not null) return existing;
        var created = new ArchiveViewModel(_services, host, details);
        ArchiveTransfers.Add(created);
        return created;
    }
    [RelayCommand] private void ShowArchive(ArchiveViewModel? session) { if (session is not null) ArchiveRequested?.Invoke(this, session); }

    /// <summary>
    /// Reads the files that are actually on disk for this task. The library index
    /// is not required: a paused, partially downloaded or non-imported task can
    /// be read up to its last valid page.
    /// </summary>
    [RelayCommand]
    private async Task ReadTaskAsync(DownloadTaskViewModel? item)
    {
        if (item is null) return;
        var (info, error) = await Task.Run(() =>
        {
            GalleryInfo? gallery = DownloadReader.TryBuildGalleryInfo(item.Task, out string? failure);
            return (gallery, failure);
        });
        if (_disposed) return;
        if (info is null)
        {
            NotifyRequested?.Invoke(this, error ?? "无法打开该下载任务");
            return;
        }

        ReadRequested?.Invoke(this, new DownloadReadRequest(item.Task, _library.FindLocalCopy(info.FolderPath) ?? info));
    }

    public string RootPath => _services.Downloads.RootPath;
    public string RootPathDisplay => RootPath;

    public bool HasTasks => Tasks.Count > 0;
    public bool HasUnavailable => UnavailableTasks.Count > 0;
    public bool HasArchive => Archive.Count > 0;

    public string UnavailableTabTitle => UnavailableTasks.Count == 0
        ? "画廊不可用"
        : $"画廊不可用 ({UnavailableTasks.Count})";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ActiveText => _services.Downloads.ActiveCount > 0
        ? $"{_services.Downloads.ActiveCount} 个任务下载中 · {DownloadTask.FormatSize((long)Tasks.Sum(t => t.Task.BytesPerSecond))}/s"
        : "空闲";

    public void RefreshStatus()
    {
        int running = Tasks.Count(t => t.IsRunning);
        int pending = Tasks.Count(t => t.IsPending);
        int failed = Tasks.Count(t => t.IsFailed);
        int done = Tasks.Count(t => t.IsCompleted);
        string unavailable = UnavailableTasks.Count > 0 ? $" · 不可用 {UnavailableTasks.Count}" : string.Empty;
        StatusText = $"队列 {Tasks.Count} · 下载 {running} · 等待 {pending} · 失败 {failed} · 完成 {done}{unavailable}";
        OnPropertyChanged(nameof(ActiveText));
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(HasUnavailable));
        OnPropertyChanged(nameof(UnavailableTabTitle));
        OnPropertyChanged(nameof(HasArchive));
        OnPropertyChanged(nameof(RootPathDisplay));
    }

    public void SyncAll(bool reconcileLocal = false)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (DownloadTask task in _services.Downloads.Tasks)
        {
            QueueLibraryUpdate(task);
            if (task.State == DownloadState.Completed) continue;
            if (reconcileLocal) QueueLocalReconcile(task);
            ids.Add(task.Id);
            ApplyTask(task, newestFirst: false);
        }

        foreach (DownloadTaskViewModel stale in Tasks.Concat(UnavailableTasks).Where(t => !ids.Contains(t.Task.Id)).ToArray())
        {
            Tasks.Remove(stale);
            UnavailableTasks.Remove(stale);
            _byId.Remove(stale.Task.Id);
        }

        ApplyArchive(_services.Downloads.Archive);

        RefreshStatus();
        _library.ApplyDownloadStates(_services.Downloads.Tasks);
    }

    /// <summary>
    /// 失败 / 暂停任务可能已经被用户在阅读器或外部补齐。进入下载页时按目录
    /// 实际页文件核对一次，避免继续显示已经不存在的失败页；同一任务 15 秒内
    /// 只核对一次，目录枚举与容器校验都在后台线程完成。
    /// </summary>
    private void QueueLocalReconcile(DownloadTask task)
    {
        if (task.State is not (DownloadState.Failed or DownloadState.Paused)) return;
        if (task.PageCount <= 0 || task.FolderPath.Length == 0) return;
        if (task.DownloadedPages >= task.PageCount && task.FailedPages.Count == 0) return;
        if (_reconciledUtc.TryGetValue(task.Id, out DateTime last) &&
            DateTime.UtcNow - last < TimeSpan.FromSeconds(15)) return;
        _reconciledUtc[task.Id] = DateTime.UtcNow;
        _ = ReconcileAsync(task);
    }

    private async Task ReconcileAsync(DownloadTask task)
    {
        try
        {
            await _services.Downloads.RefreshLocalProgressAsync(task).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 应用退出或任务被移除；下次进入下载页会重新核对。
        }
        catch (Exception error)
        {
            _services.Log.Warn("Reconcile download pages: " + error.Message);
        }
    }

    /// <summary>
    /// Keeps one view-model per task and moves it between the queue and the
    /// unavailable page without recreating it (progress and command state stay).
    /// </summary>
    private void ApplyTask(DownloadTask task, bool newestFirst)
    {
        bool unavailable = task.GalleryUnavailable;
        if (!_byId.TryGetValue(task.Id, out DownloadTaskViewModel? existing))
        {
            var created = new DownloadTaskViewModel(task, this) { InUnavailable = unavailable };
            _byId[task.Id] = created;
            var destination = unavailable ? UnavailableTasks : Tasks;
            if (newestFirst) destination.Insert(0, created);
            else destination.Add(created);
            return;
        }

        if (existing.InUnavailable == unavailable)
        {
            existing.Refresh();
            return;
        }

        (existing.InUnavailable ? UnavailableTasks : Tasks).Remove(existing);
        var target = unavailable ? UnavailableTasks : Tasks;
        if (newestFirst) target.Insert(0, existing);
        else target.Add(existing);
        existing.InUnavailable = unavailable;
        existing.Refresh();
    }

    internal void ApplyArchive(IEnumerable<DownloadArchiveEntry> entries)
    {
        Archive = entries.Select(entry => new DownloadArchiveViewModel(entry, this)).ToArray();
        OnPropertyChanged(nameof(Archive));
        OnPropertyChanged(nameof(HasArchive));
    }

    private void OnTaskChanged(object? sender, DownloadTask task)
    {
        _dirtyTasks[task.Id] = task;
        if (Interlocked.Exchange(ref _updateQueued, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            Dispatcher.UIThread.Post(() =>
            {
                Volatile.Write(ref _updateQueued, 0);
                if (_disposed) return;
                foreach (var pair in _dirtyTasks.ToArray())
                {
                    if (!_dirtyTasks.TryRemove(pair.Key, out var changed) || !_services.Downloads.IsQueued(changed)) continue;
                    if (changed.State == DownloadState.Completed)
                    {
                        if (_byId.Remove(changed.Id, out var finished))
                        {
                            Tasks.Remove(finished);
                            UnavailableTasks.Remove(finished);
                        }
                        QueueLibraryUpdate(changed);
                        continue;
                    }
                    ApplyTask(changed, newestFirst: true);
                    QueueLibraryUpdate(changed);
                }
                RefreshStatus();
                _library.ApplyDownloadStates(_services.Downloads.Tasks);
            }, DispatcherPriority.Background);
        });
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            SyncAll();
        }, DispatcherPriority.Background);
    }

    private void OnTaskCompleted(object? sender, DownloadTask task)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            QueueLibraryUpdate(task);
            _library.ApplyDownloadStates(_services.Downloads.Tasks);
        }, DispatcherPriority.Background);
    }

    private void QueueLibraryUpdate(DownloadTask task)
    {
        if ((!_services.Settings.Download.AddAsSource && !task.FillMissingOnly) ||
            (!_services.Settings.Download.RefreshLibraryOnComplete && !task.FillMissingOnly) || task.DownloadedPages == 0) return;
        var stamp = (task.DownloadedPages, task.FolderPath, task.PageCount, Complete: task.State == DownloadState.Completed);
        bool known = _imported.TryGetValue(task.Id, out var previous);
        if (known && previous == stamp) return;
        _libraryUpdates[task.Id] = task;
        if (_importing) return;
        if (!known || stamp.Complete) _ = ImportPendingLibraryUpdatesAsync();
        else _libraryTimer.Start();
    }

    private void OnLibraryTick(object? sender, EventArgs e) => _ = ImportPendingLibraryUpdatesAsync();

    private async Task ImportPendingLibraryUpdatesAsync()
    {
        _libraryTimer.Stop();
        if (_disposed || _importing) return;
        _importing = true;
        try
        {
            var pending = _libraryUpdates.Values.ToArray();
            _libraryUpdates.Clear();
            foreach (var task in pending)
            {
                if (_disposed || (!_services.Downloads.IsQueued(task) && task.State != DownloadState.Completed)) continue;
                var stamp = (task.DownloadedPages, task.FolderPath, task.PageCount, task.State == DownloadState.Completed);
                if (_imported.TryGetValue(task.Id, out var previous) && previous == stamp) continue;
                try
                {
                    if (await _library.ImportDownloadAsync(task) && !_disposed) _imported[task.Id] = stamp;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _services.Log.Warn("Live library update failed", ex); }
            }
        }
        finally
        {
            _importing = false;
            if (!_disposed && _libraryUpdates.Count > 0) _libraryTimer.Start();
        }
    }

    [RelayCommand]
    private Task StartAllAsync() => Task.Run(_services.Downloads.StartAll);

    [RelayCommand]
    private Task PauseAllAsync() => Task.Run(_services.Downloads.PauseAll);

    [RelayCommand]
    private Task ClearFinishedAsync() => Task.Run(_services.Downloads.ClearFinished);

    [RelayCommand]
    private Task ClearArchiveAsync() => Task.Run(_services.Downloads.ClearArchive);

    [RelayCommand]
    private void OpenRoot() => OpenFolder(RootPath);

    public Task PauseTask(DownloadTaskViewModel item) => Task.Run(() => _services.Downloads.Pause(item.Task));

    public async Task ResumeTask(DownloadTaskViewModel item)
    {
        try { await Task.Run(() => _services.Downloads.Resume(item.Task)); }
        catch (IOException error) { NotifyRequested?.Invoke(this, error.Message); }
    }

    public Task RemoveTask(DownloadTaskViewModel item) => Task.Run(() => _services.Downloads.Remove(item.Task, deleteFiles: false));

    public Task RemoveWithFiles(DownloadTaskViewModel item) => Task.Run(() => _services.Downloads.Remove(item.Task, deleteFiles: true));

    /// <summary>撤销「画廊不可用」标记，任务回到下载队列并可继续 / 重试。</summary>
    public Task UnmarkUnavailable(DownloadTaskViewModel item) => Task.Run(() => _services.Downloads.ClearGalleryUnavailable(item.Task));

    public void OpenTaskFolder(DownloadTaskViewModel item) => OpenFolder(item.Task.FolderPath);

    public void OpenArchiveFolder(DownloadArchiveViewModel item) => OpenFolder(item.FolderPath);

    private void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            EhViewerForPc.Services.PlatformLauncher.Open(path);
        }
        catch (Exception ex)
        {
            _services.Log.Warn("Open download folder failed: " + path, ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _libraryTimer.Stop();
        _libraryUpdates.Clear();
        foreach (var session in ArchiveTransfers) session.Dispose();
        _services.Downloads.TaskChanged -= OnTaskChanged;
        _services.Downloads.Changed -= OnChanged;
        _services.Downloads.TaskCompleted -= OnTaskCompleted;
    }
}

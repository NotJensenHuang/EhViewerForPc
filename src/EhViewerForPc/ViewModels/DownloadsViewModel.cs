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
    private readonly Dictionary<string, (int Pages, string Folder, DownloadState State)> _imported = new(StringComparer.Ordinal);
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
    private void ReadTask(DownloadTaskViewModel? item)
    {
        if (item is null) return;
        GalleryInfo? info = DownloadReader.TryBuildGalleryInfo(item.Task, out string? error);
        if (info is null)
        {
            NotifyRequested?.Invoke(this, error ?? "无法打开该下载任务");
            return;
        }

        ReadRequested?.Invoke(this, new DownloadReadRequest(item.Task, info));
    }

    public string RootPath => _services.Downloads.RootPath;
    public string RootPathDisplay => RootPath;

    public bool HasTasks => Tasks.Count > 0;
    public bool HasArchive => Archive.Count > 0;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ActiveText => _services.Downloads.ActiveCount > 0
        ? $"{_services.Downloads.ActiveCount} 个任务下载中"
        : "空闲";

    public void RefreshStatus()
    {
        int running = Tasks.Count(t => t.IsRunning);
        int pending = Tasks.Count(t => t.IsPending);
        int failed = Tasks.Count(t => t.IsFailed);
        int done = Tasks.Count(t => t.IsCompleted);
        StatusText = $"队列 {Tasks.Count} · 下载 {running} · 等待 {pending} · 失败 {failed} · 完成 {done}";
        OnPropertyChanged(nameof(ActiveText));
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(HasArchive));
        OnPropertyChanged(nameof(RootPathDisplay));
    }

    public void SyncAll()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (DownloadTask task in _services.Downloads.Tasks)
        {
            QueueLibraryUpdate(task);
            ids.Add(task.Id);
            if (_byId.TryGetValue(task.Id, out DownloadTaskViewModel? existing))
            {
                existing.Refresh();
                continue;
            }

            var vm = new DownloadTaskViewModel(task, this);
            _byId[task.Id] = vm;
            Tasks.Add(vm);
        }

        foreach (DownloadTaskViewModel stale in Tasks.Where(t => !ids.Contains(t.Task.Id)).ToList())
        {
            Tasks.Remove(stale);
            _byId.Remove(stale.Task.Id);
        }

        ApplyArchive(_services.Downloads.Archive);

        RefreshStatus();
        _library.ApplyDownloadStates(_services.Downloads.Tasks);
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
                    if (!_dirtyTasks.TryRemove(pair.Key, out var changed) || !_services.Downloads.IsQueued(changed.Gid)) continue;
                    if (_byId.TryGetValue(changed.Id, out var vm)) vm.Refresh();
                    else { vm = new DownloadTaskViewModel(changed, this); _byId[changed.Id] = vm; Tasks.Insert(0, vm); }
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
        if (!_services.Settings.Download.AddAsSource || !_services.Settings.Download.RefreshLibraryOnComplete || task.DownloadedPages == 0) return;
        var stamp = (task.DownloadedPages, task.FolderPath, task.State);
        if (_imported.TryGetValue(task.Id, out var previous) && previous == stamp) return;
        _libraryUpdates[task.Id] = task;
        if (!_importing) _libraryTimer.Start();
    }

    private async void OnLibraryTick(object? sender, EventArgs e)
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
                if (_disposed || !_services.Downloads.IsQueued(task.Gid)) continue;
                var stamp = (task.DownloadedPages, task.FolderPath, task.State);
                try
                {
                    await _library.ImportDownloadAsync(task);
                    if (!_disposed) _imported[task.Id] = stamp;
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

    public Task ResumeTask(DownloadTaskViewModel item) => Task.Run(() => _services.Downloads.Resume(item.Task));

    public Task RemoveTask(DownloadTaskViewModel item) => Task.Run(() => _services.Downloads.Remove(item.Task, deleteFiles: false));

    public Task RemoveWithFiles(DownloadTaskViewModel item) => Task.Run(() => _services.Downloads.Remove(item.Task, deleteFiles: true));

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

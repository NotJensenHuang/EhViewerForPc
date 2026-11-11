using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerReader.Core.Download;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Online;
using EhViewerReader.Services;

namespace EhViewerReader.ViewModels;

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

    public DownloadsViewModel(AppServices services, MainViewModel library)
    {
        _services = services;
        _library = library;

        _services.Downloads.TaskChanged += OnTaskChanged;
        _services.Downloads.Changed += OnChanged;
        _services.Downloads.TaskCompleted += OnTaskCompleted;

        SyncAll();
    }

    public ObservableCollection<DownloadTaskViewModel> Tasks { get; } = [];
    public IReadOnlyList<DownloadArchiveViewModel> Archive { get; private set; } = [];
    public ObservableCollection<ArchiveViewModel> ArchiveTransfers { get; } = [];
    public event EventHandler<ArchiveViewModel>? ArchiveRequested;
    public ArchiveViewModel GetArchiveSession(EhGalleryDetails details, IOnlineHost host)
    {
        var existing = ArchiveTransfers.FirstOrDefault(a => a.Details.Gid == details.Gid);
        if (existing is not null) return existing;
        var created = new ArchiveViewModel(_services, host, details);
        ArchiveTransfers.Add(created);
        return created;
    }
    [RelayCommand] private void ShowArchive(ArchiveViewModel? session) { if (session is not null) ArchiveRequested?.Invoke(this, session); }
    [RelayCommand] private void ReadTask(DownloadTaskViewModel? item)
    {
        if (item is not null && _library.TryGetLocalCopy(item.Task.Gid, out var local) && local is not null)
            _library.OpenGalleryCommand.Execute(local);
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

        Archive = _services.Downloads.Archive.Select(entry => new DownloadArchiveViewModel(entry, this)).ToArray();
        OnPropertyChanged(nameof(Archive));

        RefreshStatus();
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
                }
                RefreshStatus();
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
        if (!_services.Settings.Download.RefreshLibraryOnComplete) return;
        Dispatcher.UIThread.Post(async () =>
        {
            if (_disposed) return;
            try
            {
                await _library.ImportDownloadAsync(task).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _services.Log.Warn("Library refresh after download failed", ex);
            }
        }, DispatcherPriority.Background);
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
            EhViewerReader.Services.PlatformLauncher.Open(path);
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
        foreach (var session in ArchiveTransfers) session.Dispose();
        _services.Downloads.TaskChanged -= OnTaskChanged;
        _services.Downloads.Changed -= OnChanged;
        _services.Downloads.TaskCompleted -= OnTaskCompleted;
    }
}

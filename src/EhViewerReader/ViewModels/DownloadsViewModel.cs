using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerReader.Core.Download;
using EhViewerReader.Core.Models;
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
    public ObservableCollection<DownloadArchiveViewModel> Archive { get; } = [];

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

        Archive.Clear();
        foreach (DownloadArchiveEntry entry in _services.Downloads.Archive)
        {
            Archive.Add(new DownloadArchiveViewModel(entry, this));
        }

        RefreshStatus();
    }

    private void OnTaskChanged(object? sender, DownloadTask task)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            if (_byId.TryGetValue(task.Id, out DownloadTaskViewModel? vm))
            {
                vm.Refresh();
            }
            else
            {
                var created = new DownloadTaskViewModel(task, this);
                _byId[task.Id] = created;
                Tasks.Insert(0, created);
            }

            RefreshStatus();
        }, DispatcherPriority.Background);
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
                await _library.LoadLibraryAsync(forceRefresh: false, useCacheFirst: true).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _services.Log.Warn("Library refresh after download failed", ex);
            }
        }, DispatcherPriority.Background);
    }

    [RelayCommand]
    private void StartAll() => _services.Downloads.StartAll();

    [RelayCommand]
    private void PauseAll() => _services.Downloads.PauseAll();

    [RelayCommand]
    private void ClearFinished() => _services.Downloads.ClearFinished();

    [RelayCommand]
    private void ClearArchive() => _services.Downloads.ClearArchive();

    [RelayCommand]
    private void OpenRoot() => OpenFolder(RootPath);

    public void PauseTask(DownloadTaskViewModel item)
    {
        _services.Downloads.Pause(item.Task);
    }

    public void ResumeTask(DownloadTaskViewModel item)
    {
        _services.Downloads.Resume(item.Task);
    }

    public void RemoveTask(DownloadTaskViewModel item)
    {
        _services.Downloads.Remove(item.Task, deleteFiles: false);
    }

    public void RemoveWithFiles(DownloadTaskViewModel item)
    {
        _services.Downloads.Remove(item.Task, deleteFiles: true);
    }

    public void OpenTaskFolder(DownloadTaskViewModel item) => OpenFolder(item.Task.FolderPath);

    public void OpenArchiveFolder(DownloadArchiveViewModel item) => OpenFolder(item.FolderPath);

    private void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + path + "\"",
                UseShellExecute = true,
            });
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
        _services.Downloads.TaskChanged -= OnTaskChanged;
        _services.Downloads.Changed -= OnChanged;
        _services.Downloads.TaskCompleted -= OnTaskCompleted;
    }
}

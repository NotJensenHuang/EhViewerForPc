using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.Services;

namespace EhViewerForPc.ViewModels;

public sealed partial class MangaTranslationSettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly MangaTranslationBackendService _backend;
    private readonly Func<string, IProgress<MangaTranslationInstallProgress>, CancellationToken, MangaTranslationModelOption, Task> _installResources;
    private MangaTranslationModelOption? _selectedModel;
    private MangaTranslationLanguageOption _selectedTargetLanguage;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _installation;
    private Task? _cancelCallbacks;
    private bool _disposed;
    private bool _nativeLibrariesAvailable = true;
    private string _modelDirectory;
    private string _savedDirectory;
    private bool _isBusy;
    private bool _isReady;
    private bool _isInstalling;
    private bool _isCancelling;
    private bool _offerRetry;
    private string _operationStatus = "正在检查本地资源…";
    private string _directoryError = "";
    private double _progress = -1;

    internal MangaTranslationSettingsViewModel(AppServices services)
        : this(services, services.MangaTranslationBackend.InstallAsync, initialModel: null)
    {
    }

    internal MangaTranslationSettingsViewModel(AppServices services,
        Func<string, IProgress<MangaTranslationInstallProgress>, CancellationToken, Task> installResources,
        MangaTranslationModelOption initialModel)
        : this(services, (directory, progress, token, _) => installResources(directory, progress, token), initialModel)
    {
    }

    private MangaTranslationSettingsViewModel(AppServices services,
        Func<string, IProgress<MangaTranslationInstallProgress>, CancellationToken, MangaTranslationModelOption, Task> installResources,
        MangaTranslationModelOption? initialModel)
    {
        _services = services;
        _backend = services.MangaTranslationBackend;
        _installResources = installResources;
        MangaTranslationPreferences preferences = services.GetMangaTranslationPreferences();
        _selectedModel = initialModel ?? preferences.Model;
        if (_selectedModel is null) _operationStatus = "请先选择一个本地翻译候选模型。";
        _selectedTargetLanguage = preferences.Language;
        string configured = preferences.ModelDirectory ?? "";
        _savedDirectory = _modelDirectory = configured.Length == 0 ? _backend.DefaultModelDirectory : configured;
    }

    public IReadOnlyList<MangaTranslationModelOption> Models => MangaTranslationModelOption.Choices;
    public IReadOnlyList<MangaTranslationLanguageOption> TargetLanguages => _selectedModel is { } model
        ? MangaTranslationLanguageOption.Choices.Where(model.Supports).ToArray()
        : MangaTranslationLanguageOption.Choices;

    public MangaTranslationModelOption? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (!CanSelectModel || value is null || MangaTranslationModelOption.Find(value.Id) != value || !SetProperty(ref _selectedModel, value)) return;
            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(TargetLanguages));
            _services.SaveTranslationModel(value);
            IsReady = false;
            OperationStatus = "正在检查所选模型…";
            _ = CheckDirectoryAsync(_services.Settings.MangaTranslationModelDirectory, save: false, preserveInput: true);
        }
    }

    public MangaTranslationLanguageOption SelectedTargetLanguage
    {
        get => _selectedTargetLanguage;
        set
        {
            if (_disposed || value is null || _selectedModel?.Supports(value) == false ||
                !SetProperty(ref _selectedTargetLanguage, value)) return;
            _services.SaveTranslationLanguage(value);
            if (!IsBusy && _selectedModel is not null)
                _ = CheckDirectoryAsync(_services.Settings.MangaTranslationModelDirectory, save: false, preserveInput: true);
        }
    }

    public string ModelDirectory
    {
        get => _modelDirectory;
        set
        {
            if (!SetProperty(ref _modelDirectory, value ?? "")) return;
            DirectoryError = "";
            UpdateActions();
        }
    }

    public string DirectoryError
    {
        get => _directoryError;
        private set
        {
            if (SetProperty(ref _directoryError, value)) OnPropertyChanged(nameof(HasDirectoryError));
        }
    }

    public bool HasDirectoryError => DirectoryError.Length != 0;
    public bool HasUnsavedDirectory => !PathIdentity.Comparer.Equals(ModelDirectory.Trim(), _savedDirectory);
    public bool CanEditDirectory => !_disposed && !IsBusy;
    public bool CanSaveDirectory => CanEditDirectory && HasUnsavedDirectory;
    public bool CanSelectModel => CanEditDirectory && Models.Count > 0;
    public bool CanInstall => SelectedModel is not null && CanEditDirectory && _nativeLibrariesAvailable && !HasUnsavedDirectory && !HasDirectoryError;
    public bool CanCheckResources => CanEditDirectory && !HasUnsavedDirectory;
    public bool CanCancel => IsInstalling && !_isCancelling;
    public string InstallLabel => _offerRetry ? "重试准备模型" : IsReady ? "校验并补齐资源" : "下载并准备模型";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) UpdateActions();
        }
    }

    public bool IsReady
    {
        get => _isReady;
        private set
        {
            if (SetProperty(ref _isReady, value)) OnPropertyChanged(nameof(InstallLabel));
        }
    }

    public bool IsInstalling
    {
        get => _isInstalling;
        private set
        {
            if (SetProperty(ref _isInstalling, value)) OnPropertyChanged(nameof(CanCancel));
        }
    }

    public string OperationStatus
    {
        get => _operationStatus;
        private set => SetProperty(ref _operationStatus, value);
    }

    public double Progress
    {
        get => _progress;
        private set
        {
            if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(IsProgressIndeterminate));
        }
    }

    public bool IsProgressIndeterminate => Progress < 0;

    [RelayCommand]
    internal Task CheckResourcesAsync() => CanCheckResources
        ? CheckDirectoryAsync(_services.Settings.MangaTranslationModelDirectory, save: false)
        : Task.CompletedTask;

    [RelayCommand]
    private Task SaveDirectoryAsync() => CanSaveDirectory
        ? CheckDirectoryAsync(ModelDirectory.Trim(), save: true)
        : Task.CompletedTask;

    [RelayCommand]
    private Task RestoreDefaultDirectoryAsync() => CanEditDirectory
        ? CheckDirectoryAsync("", save: true)
        : Task.CompletedTask;

    internal Task PickDirectoryAsync(string path) => CanEditDirectory
        ? CheckDirectoryAsync(path, save: true)
        : Task.CompletedTask;

    internal void ReportDirectoryPickerFailure()
    {
        if (_disposed) return;
        DirectoryError = "无法打开目录选择器，请直接输入本机目录路径。";
        UpdateActions();
    }

    private async Task CheckDirectoryAsync(string configured, bool save, bool preserveInput = false)
    {
        IsBusy = true;
        DirectoryError = "";
        if (save && configured.Length > 0) ModelDirectory = configured;
        var model = SelectedModel;
        string[] protectedRoots = SnapshotProtectedRoots();
        try
        {
            var result = await Task.Run(() =>
            {
                string directory = _backend.GetModelDirectory(configured);
                EnsureOutsideLibrary(directory, protectedRoots);
                bool native = MangaTranslationBackendService.HasNativeLibraries();
                return (Directory: directory, Native: native, Ready: native && _backend.IsReady(configured, model));
            }, _lifetime.Token);
            if (_disposed) return;

            if (save)
            {
                EnsureOutsideLibrary(result.Directory, SnapshotProtectedRoots());
                _services.SaveMangaTranslationModelDirectory(configured.Length == 0 ? "" : result.Directory);
            }

            bool restoreSavedDirectory = !preserveInput || !HasUnsavedDirectory;
            _savedDirectory = result.Directory;
            if (restoreSavedDirectory) ModelDirectory = result.Directory;
            _nativeLibrariesAvailable = result.Native;
            IsReady = result.Ready && model?.Supports(SelectedTargetLanguage) == true;
            SetRetryLabel(false);
            OperationStatus = model is null ? "请先选择一个本地翻译候选模型。" :
                !model.Supports(SelectedTargetLanguage) ? "当前模型只支持简体中文，请选择该目标语言。" : result.Ready ? "本地资源已就绪，可以在阅读器翻译本页。" : result.Native
                ? "本地模型尚未准备完整。" : MangaTranslationBackendService.MissingNativeLibrariesMessage;
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (MangaTranslationException ex)
        {
            if (!_disposed) DirectoryError = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (!_disposed) DirectoryError = "无法使用该模型目录，请检查路径和访问权限。";
        }
        finally
        {
            if (!_disposed) IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (!CanInstall || SelectedModel is not { } model) return;
        string configured = _services.Settings.MangaTranslationModelDirectory;
        string[] protectedRoots = SnapshotProtectedRoots();
        using var job = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _installation = job;
        IsBusy = true;
        IsInstalling = true;
        SetRetryLabel(false);
        Progress = -1;
        OperationStatus = "正在检查本地模型…";
        var progress = new Progress<MangaTranslationInstallProgress>(update =>
        {
            if (_disposed || _installation != job || job.IsCancellationRequested) return;
            Progress = double.IsFinite(update.Fraction) && update.Fraction >= 0 ? Math.Min(update.Fraction, 1) : -1;
            OperationStatus = update.Message;
        });
        try
        {
            await Task.Run(async () =>
            {
                EnsureOutsideLibrary(_backend.GetModelDirectory(configured), protectedRoots);
                await _installResources(configured, progress, job.Token, model).ConfigureAwait(false);
            }, job.Token);
            if (_disposed) return;

            bool ready = await Task.Run(() => _backend.IsReady(configured, model), job.Token);
            job.Token.ThrowIfCancellationRequested();
            if (!_disposed)
            {
                bool supportsLanguage = model.Supports(SelectedTargetLanguage);
                IsReady = ready && supportsLanguage;
                if (IsReady) _services.NotifyTranslationResourcesReady();
                SetRetryLabel(!ready);
                OperationStatus = ready
                    ? supportsLanguage ? "本地资源已就绪，可以在阅读器翻译本页。" : "当前模型只支持简体中文，请选择该目标语言。"
                    : "资源仍未准备完整，请重新尝试。";
            }
        }
        catch (OperationCanceledException) when (job.IsCancellationRequested)
        {
            if (!_disposed)
            {
                SetRetryLabel(true);
                OperationStatus = "已取消准备。已校验完整的模型会保留，可点击重试。";
            }
        }
        catch (MangaTranslationException ex)
        {
            if (!_disposed)
            {
                SetRetryLabel(true);
                OperationStatus = ex.Message;
            }
        }
        catch (Exception ex)
        {
            _services.Log.Warn("漫画翻译资源准备失败：" + ex.GetType().Name);
            if (!_disposed)
            {
                SetRetryLabel(true);
                OperationStatus = "无法准备本地模型，请检查网络、磁盘空间和目录权限后重试。";
            }
        }
        finally
        {
            _installation = null;
            if (_cancelCallbacks is { } cancelCallbacks)
            {
                _cancelCallbacks = null;
                try { await cancelCallbacks; }
                catch (AggregateException ex) { _services.Log.Warn("取消漫画翻译资源准备失败：" + ex.GetType().Name); }
            }
            _isCancelling = false;
            if (!_disposed)
            {
                IsInstalling = false;
                IsBusy = false;
            }
        }
    }

    [RelayCommand]
    private void CancelInstall()
    {
        if (!CanCancel || _installation is null) return;
        _isCancelling = true;
        OnPropertyChanged(nameof(CanCancel));
        Progress = -1;
        OperationStatus = "正在取消，等待当前下载或校验收尾…";
        // 下载、磁盘和进程注册的取消回调可能需要排队，不占用界面线程。
        _cancelCallbacks = _installation.CancelAsync();
    }

    private void SetRetryLabel(bool value)
    {
        if (_offerRetry == value) return;
        _offerRetry = value;
        OnPropertyChanged(nameof(InstallLabel));
    }

    private string[] SnapshotProtectedRoots() => _services.Settings.Sources.Select(source => source.RootPath)
        .Append(_services.Downloads.RootPath)
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .ToArray();

    internal static void EnsureOutsideLibrary(string directory, IEnumerable<string> protectedRoots)
    {
        string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        foreach (string protectedRoot in protectedRoots)
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedRoot));
            if (PathIdentity.Comparer.Equals(root, candidate) || PathIdentity.IsDescendantOf(candidate, root) ||
                PathIdentity.IsDescendantOf(root, candidate))
            {
                throw new MangaTranslationException("模型目录需要与图库和下载目录分开，请选择专用的本机目录。");
            }
        }
    }

    private void UpdateActions()
    {
        OnPropertyChanged(nameof(HasUnsavedDirectory));
        OnPropertyChanged(nameof(CanEditDirectory));
        OnPropertyChanged(nameof(CanSaveDirectory));
        OnPropertyChanged(nameof(CanSelectModel));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanCheckResources));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = CancelLifetimeAsync();
    }

    private async Task CancelLifetimeAsync()
    {
        try { await _lifetime.CancelAsync().ConfigureAwait(false); }
        catch (AggregateException ex) { _services.Log.Warn("结束漫画翻译资源准备失败：" + ex.GetType().Name); }
        finally { _lifetime.Dispose(); }
    }
}

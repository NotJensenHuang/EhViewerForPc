using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Services;

namespace EhViewerForPc.ViewModels;

public sealed partial class LocalCopyDeletionViewModel(AppServices services) : ObservableObject, IDisposable
{
    private bool _disposed;
    private readonly CancellationTokenSource _lifetime = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    [NotifyPropertyChangedFor(nameof(HasConfirmation))]
    [NotifyPropertyChangedFor(nameof(FolderPath))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private GalleryInfo? _pending;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    private string _statusText = string.Empty;
    public bool HasConfirmation => Pending is not null;
    public bool IsVisible => HasConfirmation || StatusText.Length > 0;
    public string FolderPath => Pending?.FolderPath ?? string.Empty;
    public bool CanConfirm => Pending is not null && !IsBusy && !_lifetime.IsCancellationRequested;
    public bool CanCancel => !IsBusy;
    public void Request(GalleryInfo info)
    {
        if (IsBusy) return;
        Pending = info;
        StatusText = "将此副本移入回收站，保留其他版本、收藏和阅读进度。";
    }
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        Pending = null;
        StatusText = string.Empty;
    }
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        if (!CanConfirm || Pending is not { } copy) return;
        IsBusy = true;
        StatusText = "正在停止下载并等待在途文件关闭，然后移入回收站…";
        try
        {
            await services.DeleteLocalCopyAsync(copy, _lifetime.Token);
            Pending = null;
            StatusText = "该副本已移入回收站";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { StatusText = "未完成删除：" + error.Message; }
        finally { IsBusy = false; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Services;

namespace EhViewerForPc.ViewModels;

public sealed partial class LocalGalleryDetailsViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _initialized;
    private bool _disposed;
    private string _token;
    public GalleryItemViewModel Item { get; }
    public GalleryDetailsViewModel Presentation { get; }
    public event EventHandler<string>? TagSearchRequested;
    public bool CanRefreshMetadata => Item.Gid is > 0 && _token.Length > 0;
    public bool CanContinueDownload => !_disposed && Item.CanContinueDownload && _services.CanContinueLocalDownload(Item.Info);
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDownloadStatus))]
    private string _downloadStatus = string.Empty;
    public bool HasDownloadStatus => DownloadStatus.Length > 0;

    [RelayCommand(CanExecute = nameof(CanContinueDownload))]
    private async Task ContinueDownloadAsync()
    {
        if (!CanContinueDownload) return;
        DownloadStatus = "正在加入补页任务…";
        try
        {
            var task = await _services.ContinueLocalDownloadAsync(Item.Info, _lifetime.Token);
            if (!_lifetime.IsCancellationRequested) DownloadStatus = "已加入补页任务 · " + task.FolderPath;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_lifetime.IsCancellationRequested) DownloadStatus = "无法继续下载：" + error.Message; }
    }

    public LocalGalleryDetailsViewModel(GalleryItemViewModel item, AppServices services)
    {
        Item = item;
        _services = services;
        Presentation = new(services, term => TagSearchRequested?.Invoke(this, term));
        item.PropertyChanged += OnItemChanged;
        var cached = services.Works.Get(item.WorkId);
        _token = item.Info.Token.Length > 0 ? item.Info.Token : cached?.Token ?? string.Empty;
        var work = cached ?? new WorkRecord { WorkId = item.WorkId, Gid = item.Gid, Title = item.Title, PageCount = item.Info.ExpectedPages ?? item.PageCount };
        Presentation.Apply(work, item.CoverRequest, item.PageCountText,
            item.Info.Languages.Select(l => "language:" + l.ToLowerInvariant()).Concat(item.Info.Tags).Distinct(StringComparer.OrdinalIgnoreCase));
        _initialized = cached?.Metadata is not null;
        if (!CanRefreshMetadata && Presentation.Tags.Count == 0) Presentation.StatusText = "本地文件未保存标签";
    }

    public Task InitializeAsync() => _initialized ? Task.CompletedTask : LoadMetadataAsync();
    [RelayCommand(CanExecute = nameof(CanRefreshMetadata))]
    private Task RefreshMetadataAsync() => LoadMetadataAsync();

    private async Task LoadMetadataAsync()
    {
        if (!CanRefreshMetadata || Presentation.IsLoading || _lifetime.IsCancellationRequested) return;
        _initialized = true;
        Presentation.IsLoading = true;
        Presentation.StatusText = "读取标签…";
        try
        {
            var details = await _services.Online.GetDetailsAsync(Item.Gid!.Value, _token, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            var work = WorkRecord.FromDetails(details);
            _services.Works.Upsert(work);
            Presentation.Apply(work, Item.CoverRequest, Item.PageCountText);
            Presentation.StatusText = Presentation.Tags.Count == 0 ? "暂无标签" : string.Empty;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            Presentation.StatusText = "标签更新失败，可稍后重试";
            _services.Log.Warn("Local gallery metadata failed", ex);
        }
        finally { Presentation.IsLoading = false; }
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        ContinueDownloadCommand.NotifyCanExecuteChanged();
        if (e.PropertyName != nameof(GalleryItemViewModel.Info)) return;
        Presentation.PagesText = Item.PageCountText;
        Presentation.CoverRequest = Item.CoverRequest;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Item.PropertyChanged -= OnItemChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

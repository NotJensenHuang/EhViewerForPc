using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Services;

namespace EhViewerForPc.ViewModels;

public sealed partial class LocalGalleryDetailsViewModel : IDisposable
{
    private readonly AppServices _services;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _initialized;
    private string _token;
    public GalleryItemViewModel Item { get; }
    public GalleryDetailsViewModel Presentation { get; }
    public event EventHandler<string>? TagSearchRequested;
    public bool CanRefreshMetadata => Item.Gid is > 0 && _token.Length > 0;

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
        if (e.PropertyName != nameof(GalleryItemViewModel.Info)) return;
        Presentation.PagesText = Item.PageCountText;
        Presentation.CoverRequest = Item.CoverRequest;
    }

    public void Dispose()
    {
        Item.PropertyChanged -= OnItemChanged;
        _lifetime.Cancel();
    }
}

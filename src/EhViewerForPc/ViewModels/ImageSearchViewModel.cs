using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Core.Models;
using EhViewerForPc.ImageSearch;
using EhViewerForPc.Services;
using SkiaSharp;

namespace EhViewerForPc.ViewModels;

/// <summary>Presents a physical matching page with lazy thumbnail decoding.</summary>
public sealed class ImageSearchResultItem
{
    /// <summary>Initializes a result using the exact indexed file.</summary>
    public ImageSearchResultItem(LocalImageSearchHit hit, AppServices services)
    {
        Hit = hit;
        Thumbnail = new PageImageRequest([new PageCandidate { FilePath = hit.FilePath, FileName = Path.GetFileName(hit.FilePath), Extension = Path.GetExtension(hit.FilePath), Length = hit.Length, MtimeTicks = hit.MtimeTicks }], services.Images);
    }
    /// <summary>Gets the verified backend mapping.</summary>
    public LocalImageSearchHit Hit { get; }
    /// <summary>Gets the owning gallery title.</summary>
    public string Title => Hit.Gallery.DisplayTitle;
    /// <summary>Gets the concrete source and original page number.</summary>
    public string Subtitle => $"{Hit.Gallery.SourceName} · 第 {Hit.PageNumber} 页";
    /// <summary>Gets the raw matching score without a probability suffix.</summary>
    public string Score => $"匹配分 {Hit.Score:F1}";
    /// <summary>Gets a budgeted thumbnail request for the matching page.</summary>
    public IImageRequest Thumbnail { get; }
}

/// <summary>Coordinates image input, background index jobs, search cancellation and explicit page reading.</summary>
public sealed partial class ImageSearchViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly Func<GalleryInfo[]> _snapshot;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _queryCancellation;
    private byte[]? _input;
    private int _requestVersion;
    private bool _disposed;
    private IndexBuildProgress? _indexProgress;
    public bool IsProgressIndeterminate => !IsBuilding || _indexProgress is not { Total: > 0 };
    public double ProgressFraction => _indexProgress?.Fraction ?? 0;
    [ObservableProperty] private string _statusText = "正在读取索引状态…";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isBuilding;
    [ObservableProperty] private bool _hasIndex;
    [ObservableProperty] private int _indexedPages;
    [ObservableProperty] private int _processedPages;
    [ObservableProperty] private int _totalPages;
    [ObservableProperty] private bool _hasPendingBuild;
    [ObservableProperty] private Bitmap? _queryPreview;
    [ObservableProperty] private string _inputName = "选择、拖入或粘贴图片";
    [ObservableProperty] private int _cropX;
    [ObservableProperty] private int _cropY;
    [ObservableProperty] private int _cropWidth;
    [ObservableProperty] private int _cropHeight;
    [ObservableProperty] private bool _useCrop;
    private int _imageWidth, _imageHeight;

    /// <summary>Initializes a page using a UI-thread snapshot provider.</summary>
    public ImageSearchViewModel(AppServices services, Func<GalleryInfo[]> snapshot)
    {
        _services = services;
        _snapshot = snapshot;
        services.ImageSearch.BuildStateChanged += OnBuildStateChanged;
        ApplyBuildState();
    }

    private void OnBuildStateChanged(object? sender, EventArgs e)
    {
        bool wasBuilding = IsBuilding;
        ApplyBuildState();
        if (!_disposed && wasBuilding && !IsBuilding) _ = RefreshAsync();
    }

    private void ApplyBuildState()
    {
        if (_disposed) return;
        bool wasBuilding = IsBuilding;
        IsBuilding = _services.ImageSearch.IsBuilding;
        if (IsBuilding || wasBuilding) IsBusy = IsBuilding;
        if (_services.ImageSearch.BuildStatus is { } status)
        {
            StatusText = status.Message;
            ProcessedPages = status.Processed;
            TotalPages = status.Total;
            _indexProgress = status.IndexProgress;
            OnPropertyChanged(nameof(IsProgressIndeterminate));
            OnPropertyChanged(nameof(ProgressFraction));
            if (!IsBuilding) HasPendingBuild = status.CanResume;
        }
        NotifyAvailability();
    }
    /// <summary>Raised to open the existing settings section.</summary>
    public event EventHandler? SettingsRequested;
    /// <summary>Raised to read a particular source copy and original page number.</summary>
    public event EventHandler<LocalPageReadRequest>? ReadRequested;
    /// <summary>Gets the candidate page results.</summary>
    public ObservableCollection<ImageSearchResultItem> Results { get; } = [];
    /// <summary>Gets whether the persisted feature setting is enabled.</summary>
    public bool Enabled => _services.Settings.ImageSearch.Enabled;
    /// <summary>Gets whether a query image has been selected.</summary>
    public bool HasInput => _input is not null;
    /// <summary>Gets whether crop editing can change the pending query.</summary>
    public bool CanEditCrop => HasInput && !IsBusy;
    /// <summary>Gets whether searching can start.</summary>
    public bool CanSearch => Enabled && HasIndex && HasInput && !IsBusy;
    /// <summary>Gets whether a new index build can start.</summary>
    public bool CanBuild => Enabled && !IsBusy && !_services.ImageSearch.IsBuilding;
    /// <summary>Gets whether any current result cards exist.</summary>
    public bool HasResults => Results.Count > 0;
    /// <summary>Gets the orientation-correct original image width.</summary>
    public int ImageWidth => _imageWidth;
    /// <summary>Gets the orientation-correct original image height.</summary>
    public int ImageHeight => _imageHeight;
    /// <summary>Gets the short index coverage label.</summary>
    public string Coverage => HasIndex ? $"已索引 {IndexedPages:N0} 页" : "尚未建立图片索引";
    public string IndexActionLabel => HasPendingBuild ? "继续建立索引" : "建立 / 更新索引";
    partial void OnHasPendingBuildChanged(bool value) => OnPropertyChanged(nameof(IndexActionLabel));

    partial void OnIsBusyChanged(bool value) => NotifyAvailability();
    partial void OnIsBuildingChanged(bool value) => OnPropertyChanged(nameof(IsProgressIndeterminate));
    partial void OnHasIndexChanged(bool value) => NotifyAvailability();
    partial void OnIndexedPagesChanged(int value) => OnPropertyChanged(nameof(Coverage));
    private void NotifyAvailability()
    {
        OnPropertyChanged(nameof(CanBuild)); OnPropertyChanged(nameof(CanSearch)); OnPropertyChanged(nameof(Enabled)); OnPropertyChanged(nameof(Coverage));
        OnPropertyChanged(nameof(CanEditCrop));
    }

    /// <summary>Refreshes settings and index state when a retained Frame page is reactivated.</summary>
    public async Task RefreshAsync()
    {
        if (_disposed) return;
        if (_services.ImageSearch.IsBuilding) { ApplyBuildState(); return; }
        if (IsBuilding) ApplyBuildState();
        if (IsBusy) return;
        try
        {
            var status = await _services.ImageSearch.GetStatusAsync(_lifetime.Token);
            if (_disposed) return;
            if (_services.ImageSearch.IsBuilding) { ApplyBuildState(); return; }
            StatusText = Enabled ? _services.ImageSearch.BuildStatus?.Message ?? status.Message : status.Message;
            IndexedPages = status.Indexed; HasIndex = status.Indexed > 0; NotifyAvailability();
            HasPendingBuild = status.CanResume;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { SetError(error); }
    }

    /// <summary>Loads an image file in the background without retaining a source file handle.</summary>
    public async Task LoadFileAsync(string path)
    {
        try
        {
            var bytes = await Task.Run(() =>
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length is < 1 or > 50 * 1024 * 1024) throw new InvalidDataException("图片为空或超过 50 MiB。");
                return File.ReadAllBytes(path);
            }, _lifetime.Token);
            await LoadBytesAsync(bytes, Path.GetFileName(path));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { SetError(error); }
    }

    /// <summary>Loads owned clipboard or file bytes and validates decode budgets before showing a preview.</summary>
    public async Task LoadBytesAsync(byte[] bytes, string name)
    {
        CancelQuery(); int version = ++_requestVersion;
        try
        {
            var prepared = await Task.Run(() =>
            {
                var gray = GrayImage.Decode(bytes);
                using var data = SKData.CreateCopy(bytes); using var codec = SKCodec.Create(data)!;
                bool swap = (int)codec.EncodedOrigin >= 5;
                return (Png: gray.EncodePng(), Width: swap ? codec.Info.Height : codec.Info.Width, Height: swap ? codec.Info.Width : codec.Info.Height);
            }, _lifetime.Token);
            if (_disposed || version != _requestVersion) return;
            Bitmap? old = QueryPreview;
            using var previewStream = new MemoryStream(prepared.Png);
            QueryPreview = new Bitmap(previewStream); old?.Dispose();
            _input = bytes; _imageWidth = prepared.Width; _imageHeight = prepared.Height; InputName = name;
            UseCrop = false; CropX = CropY = 0; CropWidth = ImageWidth; CropHeight = ImageHeight;
            Results.Clear(); OnPropertyChanged(nameof(HasResults)); OnPropertyChanged(nameof(HasInput));
            StatusText = HasIndex ? "图片已就绪" : "请先建立图片索引"; NotifyAvailability();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (version == _requestVersion) SetError(error); }
    }

    [RelayCommand]
    private Task BuildIndexAsync() => BuildIndexCoreAsync(rebuild: false);
    [RelayCommand]
    private Task RebuildIndexAsync() => BuildIndexCoreAsync(rebuild: true);
    private async Task BuildIndexCoreAsync(bool rebuild)
    {
        if (!CanBuild) return;
        IsBusy = IsBuilding = true;
        try
        {
            var galleries = _snapshot().Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
            // 建库归应用服务持有；离开或释放页面只取消查询，不取消索引任务。
            var status = await _services.ImageSearch.BuildAsync(galleries, null, CancellationToken.None, rebuild);
            if (_disposed) return;
            StatusText = status.Message; IndexedPages = status.Indexed; HasIndex = status.Indexed > 0;
        }
        catch (OperationCanceledException) { if (!_disposed) StatusText = "已取消建库，原有索引已保留"; }
        catch (Exception error) { SetError(error); }
        finally
        {
            if (!_disposed)
            {
                IsBuilding = IsBusy = false;
                HasPendingBuild = _services.ImageSearch.BuildStatus?.CanResume ?? false;
                NotifyAvailability();
            }
        }
    }

    [RelayCommand]
    private async Task CleanupIndexAsync()
    {
        if (!CanBuild) return;
        IsBusy = true;
        try { int removed = await _services.ImageSearch.CleanupAsync(_lifetime.Token); if (!_disposed) StatusText = $"已清理 {removed} 个旧索引，保留当前索引、未完成任务和在用数据"; }
        catch (OperationCanceledException) { }
        catch (Exception error) { SetError(error); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (!CanSearch || _input is null) return;
        CancelQuery(); int version = ++_requestVersion;
        _queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _queryCancellation.CancelAfter(TimeSpan.FromSeconds(30)); var cancellation = _queryCancellation;
        IsBusy = true; StatusText = "正在搜索本地图片…";
        try
        {
            var copies = _snapshot();
            ImageCrop? crop = UseCrop ? new(CropX, CropY, CropWidth, CropHeight) : null;
            var hits = await _services.ImageSearch.SearchAsync(_input, crop, copies, cancellation.Token);
            if (_disposed || version != _requestVersion || cancellation.IsCancellationRequested) return;
            Results.Clear(); foreach (var hit in hits) Results.Add(new(hit, _services));
            OnPropertyChanged(nameof(HasResults)); StatusText = hits.Length == 0 ? "未找到候选匹配，可调整裁剪或检索范围" : $"找到 {hits.Length} 个候选页";
        }
        catch (OperationCanceledException) { if (!_disposed && version == _requestVersion) StatusText = "搜索已取消或超时"; }
        catch (Exception error) { if (version == _requestVersion) SetError(error); }
        finally { if (version == _requestVersion) IsBusy = false; cancellation.Dispose(); if (ReferenceEquals(_queryCancellation, cancellation)) _queryCancellation = null; }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsBuilding) { _services.ImageSearch.Cancel(); StatusText = "正在取消建库…"; }
        else { CancelQuery(); StatusText = "搜索已取消"; }
    }
    /// <summary>Cancels only this page's active query and invalidates its late response.</summary>
    public void CancelQuery() { _requestVersion++; _queryCancellation?.Cancel(); if (!IsBuilding) IsBusy = false; }
    [RelayCommand] private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void ResetCrop() { UseCrop = false; CropX = CropY = 0; CropWidth = ImageWidth; CropHeight = ImageHeight; }
    [RelayCommand]
    private void ReadHit(ImageSearchResultItem? result)
    {
        if (result is null) return;
        if (!_services.ImageSearch.CanRead(result.Hit)) { StatusText = "命中页已不在该副本中，或来源已停用"; return; }
        ReadRequested?.Invoke(this, new(result.Hit.Gallery, result.Hit.PageNumber));
    }
    private void SetError(Exception error) { if (!_disposed) { StatusText = error is ArgumentOutOfRangeException ? "裁剪区域无效，请重新选择" : error.Message; _services.Log.Warn("Image search: " + error.GetType().Name); } }
    /// <summary>Cancels late queries and releases the owned preview when this Frame document is removed.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _services.ImageSearch.BuildStateChanged -= OnBuildStateChanged;
        _lifetime.Cancel();
        CancelQuery();
        QueryPreview?.Dispose();
        QueryPreview = null;
    }
}

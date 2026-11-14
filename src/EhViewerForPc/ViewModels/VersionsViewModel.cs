using EhViewerForPc.Core.Models;
using EhViewerForPc.Services;
using EhViewerForPc.Core.Format;
using EhViewerForPc.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EhViewerForPc.ViewModels;

/// <summary>
/// 副本管理与逐目录缺页检测；所有版本保留自己的页数、诊断结果和默认状态。
/// </summary>
public sealed partial class VersionsViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private readonly Action<GalleryInfo>? _copyRefreshed;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckPagesCommand))]
    private bool _isChecking;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusText = string.Empty;
    public bool HasStatus => StatusText.Length > 0;

    public VersionsViewModel(GalleryItemViewModel item, AppServices services, Action<GalleryInfo>? copyRefreshed = null)
    {
        _services = services;
        _copyRefreshed = copyRefreshed;
        Item = item;
        Title = item.Title;
        GidText = item.Gid is long gid ? $"#{gid}" : "无 GID";
        Versions = item.Versions
            .Select(info => new GalleryVersionViewModel(info, PathIdentity.Comparer.Equals(info.FolderPath, item.FolderPath)))
            .ToList();
        SummaryText = DefaultHint = DifferHint = string.Empty;
        RefreshHints();
        Item.PropertyChanged += OnItemChanged;
    }

    public GalleryItemViewModel Item { get; }
    public string Title { get; }
    public string GidText { get; }
    public string SummaryText { get; private set; }
    public string DefaultHint { get; private set; }
    public string DifferHint { get; private set; }
    public IReadOnlyList<GalleryVersionViewModel> Versions { get; private set; }

    public GalleryInfo? ReadVersion { get; set; }
    public GalleryInfo? DefaultVersion { get; set; }

    public bool CanCheckPages => !_disposed && !IsChecking;

    partial void OnIsCheckingChanged(bool value)
    {
        foreach (var version in Versions) version.CanCheckPages = !value && !_disposed;
    }

    [RelayCommand(CanExecute = nameof(CanCheckPages))]
    private Task CheckPagesAsync() => CheckAsync(null);

    public Task CheckVersionAsync(GalleryVersionViewModel version) => CheckAsync(version);

    private async Task CheckAsync(GalleryVersionViewModel? selected)
    {
        if (!CanCheckPages) return;
        IsChecking = true;
        int failed = 0;
        var token = _lifetime.Token;
        try
        {
            foreach (var version in selected is null ? Versions.ToArray() : new[] { selected })
            {
                version.IsChecking = true;
                try
                {
                    var original = version.Info;
                    var source = _services.Settings.Sources.FirstOrDefault(s => s.Id == original.SourceId && s.Enabled);
                    if (source is null || !PathIdentity.IsDescendantOf(original.FolderPath, source.RootPath)) throw new IOException("数据源不可用，请检查目录后重试");
                    int? knownPages = original.ExpectedPages;
                    if (knownPages is null && _services.Works.Get(original.WorkId)?.PageCount is > 0 and var count) knownPages = count;
                    string root = source.RootPath, sourceId = source.Id, sourceName = source.DisplayName;
                    bool qualify = _services.Settings.ActiveSources.Count > 1;
                    var updated = await Task.Run(() =>
                    {
                        var result = _services.Scanner.Scan(root, null, true, null, token,
                            sourceId, sourceName, qualify, original.FolderPath);
                        var info = result.Galleries.Single();
                        int? expected = info.ExpectedPages ?? knownPages;
                        if (expected is > 0 && info.ExpectedPages != expected)
                        {
                            var scan = PageFileParser.Scan(info.FolderPath, new SpiderHeader { HasPages = true, Pages = expected.Value }, out string? error);
                            if (error is not null) throw new IOException(error);
                            info.ExpectedPages = expected;
                            info.IsIncomplete = scan.IsIncomplete;
                            info.MissingRanges = scan.MissingRanges;
                            info.HasHeaderPageCountOutdated = scan.HeaderPageCountOutdated;
                        }
                        if (!Directory.Exists(info.FolderPath) || (info.PageCount == 0 && info.HeaderError is not null))
                            throw new IOException(info.HeaderError ?? "原副本目录不可用");
                        return info;
                    }, token);
                    if (_disposed) return;
                    version.ApplyInfo(updated, version.IsPrimary, detected: true);
                    _copyRefreshed?.Invoke(updated);
                }
                catch (OperationCanceledException) when (_disposed) { return; }
                catch (Exception error)
                {
                    if (_disposed) return;
                    failed++;
                    version.SetDetectionError(error.Message);
                }
                finally { if (!_disposed) version.IsChecking = false; }
            }
            RefreshHints();
            StatusText = failed == 0 ? "缺页检测完成" : $"{failed} 个版本检测失败，可单独重试";
        }
        finally { if (!_disposed) IsChecking = false; }
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_disposed || e.PropertyName != nameof(GalleryItemViewModel.Info)) return;
        var previous = Versions;
        Versions = Item.Versions.Select(info =>
        {
            var row = previous.FirstOrDefault(v => PathIdentity.Comparer.Equals(v.FolderPath, info.FolderPath))
                ?? new GalleryVersionViewModel(info, false);
            row.ApplyInfo(info, PathIdentity.Comparer.Equals(Item.FolderPath, info.FolderPath));
            row.CanCheckPages = !IsChecking;
            return row;
        }).ToArray();
        if (!previous.SequenceEqual(Versions)) OnPropertyChanged(nameof(Versions));
        RefreshHints();
    }

    private void RefreshHints()
    {
        SummaryText = $"{Versions.Count} 个版本 · {Item.SourceCount} 个数据源";
        DefaultHint = "默认打开：" + Item.SourceName + (Item.IsPrimaryVersionPreferred ? "（已手动指定）" : "（自动选择）");
        int best = Versions.Max(v => v.Info.PageCount), least = Versions.Min(v => v.Info.PageCount);
        DifferHint = best == least ? "各版本页数一致；阅读进度与收藏按作品共享。" : $"页数不一致：最多 {best} 页，最少 {least} 页；阅读进度按作品共享。";
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(DefaultHint));
        OnPropertyChanged(nameof(DifferHint));
    }

    [RelayCommand]
    private async Task ContinueDownloadAsync(GalleryVersionViewModel? version)
    {
        if (_disposed || version is null || !version.CanContinueDownload) return;
        StatusText = "正在加入补页任务…";
        try
        {
            var task = await _services.ContinueLocalDownloadAsync(version.Info, _lifetime.Token);
            if (!_lifetime.IsCancellationRequested) StatusText = "已加入补页任务 · " + task.FolderPath;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_lifetime.IsCancellationRequested) StatusText = "无法继续下载：" + error.Message; }
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

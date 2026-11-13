using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerReader.Core.Online;

namespace EhViewerReader.ViewModels;

public sealed partial class OnlineFeedViewModel
{
    private IReadOnlyList<EhRankingKind> _rankingKinds = [];
    private FeedOption? _selectedRankingKind;
    private bool _showRankingOverview = true;
    /// <summary>Gets whether this is the dedicated ranking navigation section.</summary>
    public bool IsRankings => _mode == OnlineFeedMode.Toplist;
    /// <summary>Gets whether the upstream ranking overview is displayed.</summary>
    public bool ShowRankingOverview => IsRankings && _showRankingOverview && !_searchMode;
    /// <summary>Gets the seven leaderboard kinds supported by the upstream overview.</summary>
    public ObservableCollection<FeedOption> RankingKinds { get; } = [];
    /// <summary>Gets the selected leaderboard's four period lists.</summary>
    public ObservableCollection<EhRankingPeriod> RankingSections { get; } = [];
    /// <summary>Gets or sets the displayed leaderboard kind.</summary>
    public FeedOption? SelectedRankingKind
    {
        get => _selectedRankingKind;
        set { if (SetProperty(ref _selectedRankingKind, value)) UpdateRankingSections(); }
    }
    [RelayCommand] private async Task ShowRankingHomeAsync()
    {
        _showRankingOverview = true; SetSearchMode(false);
        _visitedUrls.Clear(); _urlIndex = 0; _nextUrl = string.Empty; HasMore = false;
        OnPropertyChanged(nameof(ShowRankingOverview)); OnPropertyChanged(nameof(ShowToplistSelector));
        await LoadAsync(reset: true);
    }
    [RelayCommand] private async Task BrowseRankingPeriodAsync(EhRankingPeriod? period)
    {
        if (period is null) return;
        _showRankingOverview = false; _toplistIndex = period.GalleryListIndex; SetSearchMode(false);
        OnPropertyChanged(nameof(ShowRankingOverview)); OnPropertyChanged(nameof(ShowToplistSelector)); OnPropertyChanged(nameof(SelectedToplistOption));
        await LoadAsync(reset: true);
    }
    [RelayCommand] private void OpenRanking(EhRankingEntry? entry)
    {
        if (entry is null || !Uri.TryCreate(entry.Href, UriKind.Absolute, out var target) || target.Scheme != "https") return;
        if (target.Host is "e-hentai.org" or "exhentai.org")
        {
            if (EhUrls.TryParseGalleryHref(entry.Href, out long gid, out string token))
                _host.OpenGallery(new() { Gid = gid, Token = token, Title = entry.Label });
            else if (target.AbsolutePath.StartsWith("/uploader/", StringComparison.Ordinal))
            {
                ApplyRequest(new() { Mode = EhSearchMode.Uploader, Query = Uri.UnescapeDataString(target.AbsolutePath[10..]).Replace('+', ' ') });
                _ = SearchAsync();
            }
            else _host.OpenExternal(entry.Href);
        }
        else if (target.Host == "forums.e-hentai.org") _host.OpenExternal(entry.Href);
    }
    private async Task LoadRankingOverviewAsync()
    {
        int generation = ++_requestGeneration;
        _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = new CancellationTokenSource(); var token = _cancellation.Token;
        IsBusy = true; StatusText = "正在读取排行榜…";
        try
        {
            var html = await _services.Online.GetStringAsync(EhUrls.EHentaiBase + "/toplist.php", null, token);
            token.ThrowIfCancellationRequested(); if (generation != _requestGeneration) return;
            ApplyRankingOverview(EhRankingParser.Parse(html));
            if (RankingKinds.Count == 0) StatusText = EhParser.DetectMessage(html) ?? "无法解析排行榜。";
        }
        catch (OperationCanceledException) { if (generation == _requestGeneration) StatusText = "已取消或请求超时。"; }
        catch (Exception error) when (error is EhException or HttpRequestException)
        { if (generation == _requestGeneration) StatusText = error.Message; }
        finally { if (generation == _requestGeneration) IsBusy = false; }
    }
    internal void ApplyRankingOverview(IReadOnlyList<EhRankingKind> kinds)
    {
        _rankingKinds = kinds; int previous = int.TryParse(SelectedRankingKind?.Key, out int selected) ? selected : 0;
        RankingKinds.Clear();
        for (int i = 0; i < kinds.Count; i++) RankingKinds.Add(new(i.ToString(), kinds[i].Label));
        SelectedRankingKind = RankingKinds.ElementAtOrDefault(previous) ?? RankingKinds.FirstOrDefault();
        HasMore = false; PageLabel = "排行榜总览"; StatusText = "排行榜由 E-Hentai 提供。";
        OnPropertyChanged(nameof(ShowEmpty));
    }
    private void UpdateRankingSections()
    {
        RankingSections.Clear();
        if (int.TryParse(SelectedRankingKind?.Key, out int index) && index >= 0 && index < _rankingKinds.Count)
            foreach (var period in _rankingKinds[index].Periods) RankingSections.Add(period);
    }
}

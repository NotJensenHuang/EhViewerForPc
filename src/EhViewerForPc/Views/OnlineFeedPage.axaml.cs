using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.ComponentModel;
using EhViewerForPc.Services;
using EhViewerForPc.ViewModels;

namespace EhViewerForPc.Views;

public partial class OnlineFeedPage : UserControl
{
    private OnlineFeedViewModel? _observed;
    private bool _loadCheckQueued;
    private int _listRevision = -1;

    public OnlineFeedPage()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            SearchOptionsScroll.MaxHeight = Math.Clamp(Bounds.Height - 210, 100, 400);
            QueueLoadCheck();
        };
        Loaded += (_, _) => ObserveViewModel();
        DataContextChanged += (_, _) => { if (IsLoaded) ObserveViewModel(); };
        Unloaded += (_, _) =>
        {
            if (_observed is not null) _observed.PropertyChanged -= OnListingChanged;
            _observed = null;
        };
    }

    private void ObserveViewModel()
    {
        if (_observed is not null) _observed.PropertyChanged -= OnListingChanged;
        _observed = DataContext as OnlineFeedViewModel;
        if (_observed is not null) _observed.PropertyChanged += OnListingChanged;
        UpdateListPosition();
        QueueLoadCheck();
    }

    private void UpdateListPosition()
    {
        if (_observed is null || _listRevision == _observed.ListRevision) return;
        _listRevision = _observed.ListRevision;
        GalleryScroll.Offset = default;
    }

    private void OnListingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OnlineFeedViewModel.ListRevision)) UpdateListPosition();
        if (e.PropertyName is nameof(OnlineFeedViewModel.CanAutoLoadMore) or nameof(OnlineFeedViewModel.ListRevision)) QueueLoadCheck();
    }

    private void OnGalleryScrollChanged(object? sender, ScrollChangedEventArgs e) => QueueLoadCheck();

    private void QueueLoadCheck()
    {
        if (!IsLoaded || _loadCheckQueued) return;
        _loadCheckQueued = true;
        Dispatcher.UIThread.Post(async () =>
        {
            _loadCheckQueued = false;
            if (!IsLoaded || _observed is not { CanAutoLoadMore: true } vm || !GalleryScroll.IsEffectivelyVisible) return;
            double viewport = GalleryScroll.Viewport.Height;
            if (viewport <= 0 || GalleryScroll.Extent.Height - GalleryScroll.Offset.Y - viewport > Math.Max(300, viewport / 2)) return;
            try { await vm.LoadMoreCommand.ExecuteAsync(null); }
            catch (Exception error) { AppServices.Current.Log.Error("Lazy gallery load failed", error); }
        }, DispatcherPriority.Background);
    }

    /// <summary>Ctrl+F lands here: focus the merged search box.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OnlineFeedViewModel viewModel) return;
        if (sender is Button { Tag: FeedOption option })
        {
            viewModel.SelectedTab = option;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is OnlineFeedViewModel viewModel)
        {
            viewModel.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnFavoriteSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is OnlineFeedViewModel viewModel)
        {
            viewModel.SearchFavoritesCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OnlineFeedViewModel viewModel) return;
        if (sender is Button { Tag: string entry })
        {
            viewModel.UseHistoryCommand.Execute(entry);
        }
    }

    private void OnSavedClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OnlineFeedViewModel viewModel) return;
        if (sender is Button { Tag: string name })
        {
            viewModel.UseSavedSearchCommand.Execute(name);
        }
    }

    private void OnSavedRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OnlineFeedViewModel viewModel) return;
        if (sender is Button { Tag: string name })
        {
            viewModel.RemoveSavedSearchCommand.Execute(name);
        }
    }

    private async void OnSelectSearchImage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OnlineFeedViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider) return;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择在线搜索图片", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("图片") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.gif", "*.bmp"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await vm.LoadSearchImageAsync(path);
    }
    private void OnInsertTag(object? sender, RoutedEventArgs e)
    { if (DataContext is OnlineFeedViewModel vm && sender is Button { Tag: TagLookupItem item }) vm.InsertTagCommand.Execute(item); }
    private void OnTagKeywordKeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Enter && DataContext is OnlineFeedViewModel vm) { vm.LookupTagsCommand.Execute(null); e.Handled = true; } }
    private void OnRankingPeriod(object? sender, RoutedEventArgs e)
    { if (DataContext is OnlineFeedViewModel vm && sender is Button { Tag: EhViewerForPc.Core.Online.EhRankingPeriod period }) vm.BrowseRankingPeriodCommand.Execute(period); }
    private void OnRankingEntry(object? sender, RoutedEventArgs e)
    { if (DataContext is OnlineFeedViewModel vm && sender is Button { Tag: EhViewerForPc.Core.Online.EhRankingEntry entry }) vm.OpenRankingCommand.Execute(entry); }
}

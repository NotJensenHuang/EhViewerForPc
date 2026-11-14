using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EhViewerForPc.Core.Models;
using EhViewerForPc.ViewModels;

namespace EhViewerForPc.Views;

/// <summary>
/// Local library page (offline, read-only). Requests document navigation from the shell.
/// </summary>
public partial class LibraryPage : UserControl
{
    private MainViewModel? _viewModel;
    private Vector _libraryOffset;
    private bool _restoringScroll;

    public LibraryPage()
    {
        InitializeComponent();
    }

    public event EventHandler<GalleryItemViewModel>? LocalDetailsRequested;
    public event EventHandler<GalleryItemViewModel>? VersionsRequested;
    /// <summary>Requests the image search document inside the current Frame.</summary>
    public event EventHandler? ImageSearchRequested;
    private void OnImageSearch(object? sender, RoutedEventArgs e) => ImageSearchRequested?.Invoke(this, EventArgs.Empty);

    public ScrollViewer Scroller => LibraryScroll;

    /// <summary>Exposed for the UI smoke test.</summary>
    public Control StatusBarControl => StatusBar;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Attach(DataContext as MainViewModel);
    }

    private void Attach(MainViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel)) return;
        if (_viewModel is not null)
        {
            _viewModel.GalleryListReplaced -= OnGalleryListReplaced;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.GalleryListRebalanced -= OnGalleryListRebalanced;
            _viewModel.OpenDetailsRequested -= OnOpenDetailsRequested;
            _viewModel.OpenVersionsRequested -= OnOpenVersionsRequested;
            _viewModel.ChooseFolderRequested -= OnChooseFolderRequested;
        }

        _viewModel = viewModel;
        if (viewModel is null) return;

        viewModel.GalleryListReplaced += OnGalleryListReplaced;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.GalleryListRebalanced += OnGalleryListRebalanced;
        viewModel.OpenDetailsRequested += OnOpenDetailsRequested;
        viewModel.OpenVersionsRequested += OnOpenVersionsRequested;
        viewModel.ChooseFolderRequested += OnChooseFolderRequested;
    }

    // -------------------------------------------------------------- paging

    private void OnGalleryListReplaced(object? sender, EventArgs e)
    {
        _libraryOffset = default;
        LibraryScroll.Offset = default;
        InvalidateRepeaterMeasure();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Galleries)) InvalidateRepeaterMeasure();
    }

    private void InvalidateRepeaterMeasure()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(this) is null) return;
            LibraryRepeater.InvalidateMeasure();
        }, DispatcherPriority.Loaded);
    }

    private void OnGalleryListRebalanced(object? sender, EventArgs e)
    {
        Vector target = _libraryOffset;
        if (target.Y <= 0.5) return;

        RestoreLibraryOffset(target, DispatcherPriority.Loaded);
        RestoreLibraryOffset(target, DispatcherPriority.Background);
    }

    private void RestoreLibraryOffset(Vector target, DispatcherPriority priority)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Math.Abs(LibraryScroll.Offset.Y - target.Y) < 0.5) return;
            _restoringScroll = true;
            LibraryScroll.Offset = target;
            _restoringScroll = false;
        }, priority);
    }

    private void OnLibraryScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_restoringScroll) return;
        _libraryOffset = LibraryScroll.Offset;
    }

    // ----------------------------------------------------------- commands

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void ClearSearch()
    {
        if (_viewModel is null) return;
        _viewModel.SearchText = string.Empty;
    }

    public void Refresh() => _viewModel?.RefreshCommand.Execute(null);

    private void OnClearSearch(object? sender, RoutedEventArgs e)
    {
        ClearSearch();
        SearchBox.Focus();
    }

    private void OnFilterClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        if (sender is Button { Tag: FilterOption option })
        {
            _viewModel.SelectFilter(option);
        }
    }

    private void OnCardTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual visual && (visual is Button || visual.FindAncestorOfType<Button>() is not null)) return;
        if (_viewModel is null) return;
        if (sender is Control { DataContext: GalleryItemViewModel item })
        {
            _viewModel.OpenDetailsCommand.Execute(item);
            e.Handled = true;
        }
    }

    /// <summary>A cover click opens the same details as the rest of the card.</summary>
    private void OnCoverTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is null) return;
        if (sender is Control { DataContext: GalleryItemViewModel item })
        {
            _viewModel.OpenDetailsCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnReadCard(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: GalleryItemViewModel item }) _viewModel?.OpenGalleryCommand.Execute(item);
        e.Handled = true;
    }

    private async void OnContinueDownload(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control { DataContext: GalleryItemViewModel item } && _viewModel is not null)
            await _viewModel.ContinueDownloadCommand.ExecuteAsync(item);
    }

    private void OnCardKeyDown(object? sender, KeyEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender)) return;
        if (_viewModel is null || e.Key is not (Key.Enter or Key.Space)) return;
        if (sender is Control { DataContext: GalleryItemViewModel item })
        {
            _viewModel.OpenDetailsCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnCardContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_viewModel is null) return;
        if (sender is not Control { DataContext: GalleryItemViewModel item } control) return;

        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem("阅读", () => _viewModel.OpenGalleryCommand.Execute(item)));
        menu.Items.Add(CreateMenuItem("查看详情", () => _viewModel.OpenDetailsCommand.Execute(item)));
        if (item.IsIncomplete)
        {
            var resume = CreateMenuItem("继续下载缺页", () => _viewModel.ContinueDownloadCommand.Execute(item));
            resume.IsEnabled = item.CanContinueDownload;
            menu.Items.Add(resume);
        }
        if (item.HasMultipleVersions)
        {
            menu.Items.Add(CreateMenuItem($"版本管理（{item.Versions.Count}）",
                () => _viewModel.OpenVersionsCommand.Execute(item)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem(item.IsFavorite ? "取消收藏" : "收藏",
            () => _viewModel.ToggleFavoriteCommand.Execute(item)));
        menu.Items.Add(CreateMenuItem("标记已读", () => _viewModel.MarkReadCommand.Execute(item)));
        menu.Items.Add(CreateMenuItem("标记未读", () => _viewModel.MarkUnreadCommand.Execute(item)));
        menu.Items.Add(CreateMenuItem("清除进度", () => _viewModel.ClearProgressCommand.Execute(item)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("打开文件夹", () => _viewModel.OpenFolderCommand.Execute(item)));

        control.ContextMenu = menu;
        menu.Open(control);
        e.Handled = true;
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private async void OnChooseFolderRequested(object? sender, EventArgs e) => await PickFolderAsync();

    private async void OnChooseFolder(object? sender, RoutedEventArgs e) => await PickFolderAsync();

    private async Task PickFolderAsync()
    {
        if (_viewModel is null) return;
        try
        {
            TopLevel? top = TopLevel.GetTopLevel(this);
            if (top is null) return;

            IReadOnlyList<IStorageFolder> folders = await top.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = "选择 EhViewer 下载目录",
                    AllowMultiple = false,
                });

            if (folders.Count == 0) return;
            string? path = folders[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;

            string? error = await _viewModel.SetRootAsync(path);
            if (error is not null)
            {
                _viewModel.ReportError("切换资料库失败：" + error);
            }
        }
        catch (Exception ex)
        {
            _viewModel.ReportError("选择目录失败：" + ex.Message);
        }
    }

    private void OnVersionsClick(object? sender, TappedEventArgs e)
    {
        if (_viewModel is null) return;
        if (sender is Control { DataContext: GalleryItemViewModel item })
        {
            _viewModel.OpenVersionsCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnOpenVersionsRequested(object? sender, GalleryItemViewModel item) => VersionsRequested?.Invoke(this, item);
    private void OnOpenDetailsRequested(object? sender, GalleryItemViewModel item) => LocalDetailsRequested?.Invoke(this, item);
}

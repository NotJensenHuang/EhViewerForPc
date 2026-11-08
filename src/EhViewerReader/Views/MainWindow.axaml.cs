using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EhViewerReader.Core.Models;
using EhViewerReader.ViewModels;

namespace EhViewerReader.Views;

public partial class MainWindow : Window
{
    private readonly List<ReaderWindow> _readers = [];
    private MainViewModel? _viewModel;
    private Vector _libraryOffset;
    private bool _restoringScroll;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        Icon = AppIcons.Window;
        Avalonia.Media.RenderOptions.SetTextRenderingMode(this, Avalonia.Media.TextRenderingMode.Antialias);
        KeyDown += OnWindowKeyDown;
        Closing += OnWindowClosing;
    }

    public MainWindow(MainViewModel viewModel) : this()
    {
        Attach(viewModel);
    }

    public void Attach(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        AppSettings settings = viewModel.Services.Settings;
        Width = Math.Clamp(settings.WindowWidth, 960, 10000);
        Height = Math.Clamp(settings.WindowHeight, 640, 10000);
        if (settings.WindowMaximized) WindowState = WindowState.Maximized;

        viewModel.GalleryListReplaced += OnGalleryListReplaced;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.GalleryListRebalanced += OnGalleryListRebalanced;
        viewModel.OpenGalleryRequested += OnOpenGalleryRequested;
        viewModel.OpenDetailsRequested += OnOpenDetailsRequested;
        viewModel.OpenVersionsRequested += OnOpenVersionsRequested;
        viewModel.OpenSettingsRequested += OnOpenSettingsRequested;
        viewModel.ChooseFolderRequested += OnChooseFolderRequested;
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        _closing = true;
        if (_viewModel is null) return;
        AppSettings settings = _viewModel.Services.Settings;
        settings.WindowWidth = Width;
        settings.WindowHeight = Height;
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        _viewModel.Services.TrySaveSettings(out _);
        _viewModel.Services.Progress.Save(out _);

        // Owned reader windows would otherwise keep the process alive.
        foreach (ReaderWindow reader in _readers.ToArray())
        {
            reader.Close();
        }
    }

    // The library list is replaced wholesale on user-driven filter/search/sort
    // changes; those intentionally jump to the top.
    private void OnGalleryListReplaced(object? sender, EventArgs e)
    {
        _libraryOffset = default;
        LibraryScroll.Offset = default;
        InvalidateRepeaterMeasure();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // A scan replaces the whole projection too; recycled containers must be
        // re-measured or a badge that was collapsed for the previous item can
        // stay at zero width.
        if (e.PropertyName == nameof(MainViewModel.Galleries)) InvalidateRepeaterMeasure();
    }

    /// <summary>
    /// Recycled ItemsRepeater containers can keep a TextBlock measured at zero
    /// width when a collapsed badge becomes visible (page count / incomplete
    /// chip). One measure pass after the swap re-runs the whole subtree,
    /// including elements that were collapsed while the old item was shown.
    /// </summary>
    private void InvalidateRepeaterMeasure()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_closing) return;
            LibraryRepeater.InvalidateMeasure();
        }, DispatcherPriority.Loaded);
    }

    // Progress-driven rebuilds (e.g. returning from the reader with the "在读"
    // filter active) keep the viewport where the user left it.
    private void OnGalleryListRebalanced(object? sender, EventArgs e)
    {
        Vector target = _libraryOffset;
        if (target.Y <= 0.5) return;

        RestoreLibraryOffset(target, DispatcherPriority.Loaded);
        // ItemsRepeater may publish its final extent one frame later.
        RestoreLibraryOffset(target, DispatcherPriority.Background);
    }

    private void RestoreLibraryOffset(Vector target, DispatcherPriority priority)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_closing) return;
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

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5 && _viewModel is not null)
        {
            _viewModel.RefreshCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _viewModel is not null && _viewModel.SearchText.Length > 0)
        {
            _viewModel.SearchText = string.Empty;
            e.Handled = true;
        }
    }

    private void OnClearSearch(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.SearchText = string.Empty;
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
        if (_viewModel is null) return;
        if (sender is Control { DataContext: GalleryItemViewModel item })
        {
            _viewModel.OpenGalleryCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnCardKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || e.Key is not (Key.Enter or Key.Space)) return;
        if (sender is Control { DataContext: GalleryItemViewModel item })
        {
            _viewModel.OpenGalleryCommand.Execute(item);
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
            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
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

    private void OnOpenGalleryRequested(object? sender, GalleryItemViewModel item)
    {
        if (_viewModel is null) return;
        try
        {
            // Non-modal on purpose: the library stays browsable while a gallery
            // is open. Re-opening the same gallery activates the existing window
            // instead of creating a second writer for the same progress record.
            ReaderWindow? existing = _readers.FirstOrDefault(r =>
                string.Equals(r.GalleryId, item.StableId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (existing.WindowState == WindowState.Minimized)
                {
                    existing.WindowState = WindowState.Normal;
                }

                existing.Activate();
                return;
            }

            var readerViewModel = new ReaderViewModel(_viewModel.Services, item.Info);
            var window = new ReaderWindow(readerViewModel)
            {
                GalleryId = item.StableId,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };

            _readers.Add(window);
            window.Closed += (_, _) =>
            {
                _readers.Remove(window);
                if (_closing) return;
                _viewModel?.RefreshItemProgress(item);
            };

            window.Show(this);
        }
        catch (Exception ex)
        {
            _viewModel.ReportError("打开阅读器失败：" + ex.Message);
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

    private async void OnOpenVersionsRequested(object? sender, GalleryItemViewModel item)
    {
        if (_viewModel is null) return;
        try
        {
            var window = new VersionsWindow(new VersionsViewModel(item, _viewModel.Services))
            {
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            await window.ShowDialog(this);

            if (window.ReadVersion is { } read)
            {
                _viewModel.SetPreferredSource(item, read);
                _viewModel.OpenGalleryCommand.Execute(item);
            }
            else if (window.DefaultVersion is { } preferred)
            {
                _viewModel.SetPreferredSource(item, preferred);
            }
        }
        catch (Exception ex)
        {
            _viewModel.ReportError("打开版本管理失败：" + ex.Message);
        }
    }

    private async void OnOpenDetailsRequested(object? sender, GalleryItemViewModel item)
    {
        if (_viewModel is null) return;
        try
        {
            var window = new GalleryDetailsWindow(item, _viewModel.Services);
            await window.ShowDialog(this);
            _viewModel.RefreshItemProgress(item);
        }
        catch (Exception ex)
        {
            _viewModel.ReportError("打开详情失败：" + ex.Message);
        }
    }

    private async void OnOpenSettingsRequested(object? sender, EventArgs e)
    {
        if (_viewModel is null) return;
        try
        {
            var window = new SettingsWindow(new SettingsViewModel(_viewModel.Services), _viewModel);
            await window.ShowDialog(this);
            _viewModel.ApplyFilter();
        }
        catch (Exception ex)
        {
            _viewModel.ReportError("打开设置失败：" + ex.Message);
        }
    }
}

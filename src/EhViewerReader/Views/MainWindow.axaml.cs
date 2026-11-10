using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Online;
using EhViewerReader.Services;
using EhViewerReader.ViewModels;

namespace EhViewerReader.Views;

/// <summary>
/// Navigation shell. Page views are created once and cached per view model, so
/// switching tabs keeps scroll position and avoids rebuilding lists.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Dictionary<object, Control> _views = [];
    private readonly List<ReaderWindow> _readers = [];
    private ShellViewModel? _shell;
    private LibraryPage? _libraryPage;
    private SettingsPage? _settingsPage;
    private OnlineGalleryWindow? _galleryWindow;
    private TorrentWindow? _torrentWindow;
    private ArchiveWindow? _archiveWindow;
    private WebLoginWindow? _loginWindow;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        Icon = AppIcons.Window;
        Avalonia.Media.TextOptions.SetTextRenderingMode(this, Avalonia.Media.TextRenderingMode.Antialias);
        KeyDown += OnWindowKeyDown;
        Closing += OnWindowClosing;
    }

    public MainWindow(ShellViewModel shell) : this()
    {
        Attach(shell);
    }

    /// <summary>Exposed for the UI smoke test.</summary>
    public ScrollViewer? LibraryScroll => _libraryPage?.Scroller;

    public Control? LibraryStatusBar => _libraryPage?.StatusBarControl;

    public ShellViewModel? Shell => _shell;

    /// <summary>Exposed for the UI smoke test (web login window, when open).</summary>
    public Window? LoginWindow => _loginWindow;

    public void Attach(ShellViewModel shell)
    {
        _shell = shell;
        DataContext = shell;

        AppSettings settings = AppServices.Current.Settings;
        Width = Math.Clamp(settings.WindowWidth, 1000, 10000);
        Height = Math.Clamp(settings.WindowHeight, 620, 10000);
        if (settings.WindowMaximized) WindowState = WindowState.Maximized;

        shell.PropertyChanged += OnShellPropertyChanged;
        shell.GalleryRequested += OnGalleryRequested;
        shell.WebLoginRequested += OnWebLoginRequested;
        ShowPage(shell.CurrentPage);
    }

    private void OnShellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentPage)) ShowPage(_shell?.CurrentPage);
    }

    private void ShowPage(object? page)
    {
        if (page is null) return;

        if (!_views.TryGetValue(page, out Control? view))
        {
            view = CreateView(page);
            view.DataContext = page;
            _views[page] = view;
        }

        ContentHost.Content = view;
    }

    private Control CreateView(object page)
    {
        switch (page)
        {
            case MainViewModel:
                _libraryPage = new LibraryPage();
                _libraryPage.SettingsRequested += (_, _) => _shell?.Navigate("settings");
                return _libraryPage;
            case OnlineFeedViewModel:
                return new OnlineFeedPage();
            case SearchPageViewModel:
                return new SearchPage();
            case HistoryViewModel:
                return new HistoryPage();
            case DownloadsViewModel:
                return new DownloadsPage();
            case SettingsViewModel:
                _settingsPage = new SettingsPage();
                _settingsPage.SettingsApplied += OnSettingsApplied;
                _settingsPage.WebLoginRequested += OnWebLoginRequested;
                return _settingsPage;
            default:
                return new TextBlock { Text = "未实现的页面", Margin = new Thickness(24) };
        }
    }

    private async void OnSettingsApplied(object? sender, bool sourcesChanged)
    {
        if (_shell is null) return;
        App.ApplyTheme(AppServices.Current.Settings.Theme);
        if (sourcesChanged)
        {
            await _shell.Library.ReloadSourcesAsync();
        }
        else
        {
            _shell.Library.ApplyFilter();
        }

        _shell.RaiseAccount();
    }

    private void OnWebLoginRequested(object? sender, EventArgs e)
    {
        if (_shell is null) return;

        if (_loginWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        var viewModel = new WebLoginViewModel(AppServices.Current);
        var window = new WebLoginWindow(viewModel)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        viewModel.Completed += (_, result) =>
        {
            _shell?.ApplyLoginResult(result);
            window.Close();
        };

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_loginWindow, window)) _loginWindow = null;
        };

        _loginWindow = window;
        window.Show(this);
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        _closing = true;
        if (_shell is null) return;

        AppSettings settings = AppServices.Current.Settings;
        settings.WindowWidth = Width;
        settings.WindowHeight = Height;
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        _shell.Settings.Apply();
        AppServices.Current.TrySaveSettings(out _);
        AppServices.Current.Progress.Save(out _);

        foreach (ReaderWindow reader in _readers.ToArray()) reader.Close();
        _torrentWindow?.Close();
        _archiveWindow?.Close();
        _galleryWindow?.Close();
        _loginWindow?.Close();
    }

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if (_shell is null) return;
        if (sender is Button { Tag: NavItem item })
        {
            _shell.SelectedNav = item;
        }
    }

    private void OnToggleSite(object? sender, RoutedEventArgs e)
    {
        _shell?.ToggleSiteCommand.Execute(null);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        bool control = (e.KeyModifiers & KeyModifiers.Control) != 0;

        if (e.Key == Key.F && control)
        {
            _shell?.Navigate("search");
            Dispatcher.UIThread.Post(() =>
            {
                if (ContentHost.Content is SearchPage page) page.FocusQuery();
            }, DispatcherPriority.Loaded);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5 && _shell is not null)
        {
            switch (_shell.CurrentPage)
            {
                case OnlineFeedViewModel feed:
                    feed.RefreshCommand.Execute(null);
                    break;
                case SearchPageViewModel search:
                    search.SearchCommand.Execute(null);
                    break;
                case DownloadsViewModel downloads:
                    downloads.SyncAll();
                    break;
                case MainViewModel library:
                    library.RefreshCommand.Execute(null);
                    break;
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _shell?.CurrentPage is MainViewModel main && main.SearchText.Length > 0)
        {
            main.SearchText = string.Empty;
            e.Handled = true;
        }
    }

    private void OnGalleryRequested(object? sender, EhGallerySummary summary)
    {
        if (_shell is null) return;
        try
        {
            _galleryWindow?.Close();

            var viewModel = new OnlineGalleryViewModel(AppServices.Current, _shell, summary);
            var window = new OnlineGalleryWindow(viewModel)
            {
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };

            viewModel.ReadRequested += OnOnlineReadRequested;
            viewModel.TagSearchRequested += OnTagSearchRequested;
            viewModel.TorrentsRequested += OnTorrentsRequested;
            viewModel.ArchiveRequested += OnArchiveRequested;
            window.Closed += (_, _) =>
            {
                viewModel.ReadRequested -= OnOnlineReadRequested;
                viewModel.TagSearchRequested -= OnTagSearchRequested;
                viewModel.TorrentsRequested -= OnTorrentsRequested;
                viewModel.ArchiveRequested -= OnArchiveRequested;
                if (ReferenceEquals(_galleryWindow, window)) _galleryWindow = null;
            };

            _galleryWindow = window;
            window.Show(this);
        }
        catch (Exception ex)
        {
            _shell.Notify("打开画廊失败：" + ex.Message);
        }
    }

    private async void OnOnlineReadRequested(object? sender, EhGalleryDetails details)
    {
        if (_shell is null) return;
        try
        {
            _shell.Notify("正在读取页面列表…");
            IReadOnlyList<EhPageRef> pages = await AppServices.Current.Online
                .GetAllPagesAsync(details.Gid, details.Token, details.Pages, null, CancellationToken.None)
                .ConfigureAwait(true);

            if (pages.Count == 0)
            {
                _shell.Notify("没有可阅读的页面");
                return;
            }

            ReaderWindow? existing = _readers.FirstOrDefault(r =>
                string.Equals(r.GalleryId, "online-" + details.Gid, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Activate();
                return;
            }

            var source = new Services.OnlineReaderSource(AppServices.Current, details, pages);
            var readerViewModel = new ReaderViewModel(AppServices.Current, source);
            var window = new ReaderWindow(readerViewModel)
            {
                GalleryId = source.StableId,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };

            _readers.Add(window);
            window.Closed += (_, _) =>
            {
                _readers.Remove(window);
                if (!_closing) _shell?.RaiseAccount();
            };

            window.Show(this);
            _shell.Notify($"在线阅读：{pages.Count} 页");
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            _shell.Notify("在线读取失败：" + ex.Message);
        }
    }

    private void OnTagSearchRequested(object? sender, string term)
    {
        _galleryWindow?.Close();
        _shell?.SearchTag(term);
    }

    private void OnTorrentsRequested(object? sender, EhGalleryDetails details)
    {
        if (_shell is null) return;
        _torrentWindow?.Close();

        var viewModel = new TorrentViewModel(AppServices.Current, details);
        var window = new TorrentWindow(viewModel) { WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_torrentWindow, window)) _torrentWindow = null;
        };

        _torrentWindow = window;
        window.Show(this);
    }

    private void OnArchiveRequested(object? sender, EhGalleryDetails details)
    {
        if (_shell is null) return;
        _archiveWindow?.Close();

        var viewModel = new ArchiveViewModel(AppServices.Current, _shell, details);
        var window = new ArchiveWindow(viewModel) { WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_archiveWindow, window)) _archiveWindow = null;
        };

        _archiveWindow = window;
        window.Show(this);
    }
}

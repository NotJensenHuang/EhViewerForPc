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
    private ShellViewModel? _shell;
    private LibraryPage? _libraryPage;
    private OnlineFeedPage? _onlinePage;
    private SettingsPage? _settingsPage;
    private readonly List<DocumentPage> _documents = [];
    private WebLoginPage? _loginPage;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _openingReaders = new(StringComparer.Ordinal);
    private bool _closing;
    private bool? _sidebarOverride;
    private bool? _lastNarrow;

    public MainWindow()
    {
        InitializeComponent();
        Icon = AppIcons.Window;
        Avalonia.Media.TextOptions.SetTextRenderingMode(this, Avalonia.Media.TextRenderingMode.Antialias);
        KeyDown += OnWindowKeyDown;
        Closing += OnWindowClosing;
        SizeChanged += (_, _) => UpdateAdaptiveLayout();
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
    public DocumentPage? LoginWindow => _loginPage;
    public ReaderPage? ActiveReader => _documents.LastOrDefault() as ReaderPage;

    public void Attach(ShellViewModel shell)
    {
        _shell = shell;
        DataContext = shell;

        AppSettings settings = AppServices.Current.Settings;
        Width = Math.Clamp(settings.WindowWidth, 540, 10000);
        Height = Math.Clamp(settings.WindowHeight, 420, 10000);
        if (settings.WindowMaximized) WindowState = WindowState.Maximized;

        shell.PropertyChanged += OnShellPropertyChanged;
        shell.GalleryRequested += OnGalleryRequested;
        shell.WebLoginRequested += OnWebLoginRequested;
        shell.Downloads.ArchiveRequested += (_, vm) => PushDocument(new ArchivePage(vm) { Title = vm.Title });
        shell.LocalReadRequested += OnLocalReadRequested;
        // The library view model outlives its page, so reading a local copy must
        // work even when the library page has never been shown.
        shell.Library.OpenGalleryRequested += OnLocalReadRequested;
        ShowPage(shell.CurrentPage);
        UpdateAdaptiveLayout();
    }

    private void OnShellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentPage))
        {
            ClearDocuments();
            ShowPage(_shell?.CurrentPage);
        }
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
                _libraryPage.LocalDetailsRequested += (_, item) => OpenLocalDetails(item);
                _libraryPage.VersionsRequested += (_, item) => OpenVersions(item);
                _libraryPage.SettingsRequested += (_, _) => _shell?.Navigate("settings");
                return _libraryPage;
            case OnlineFeedViewModel online:
                var onlineView = new OnlineFeedPage();
                if (ReferenceEquals(online, _shell?.Online)) _onlinePage = onlineView;
                return onlineView;
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

    private async Task OnSettingsApplied(bool sourcesChanged)
    {
        if (_shell is null) return;
        App.ApplyTheme(AppServices.Current.Settings.Theme);
        if (sourcesChanged)
        {
            if (_shell.Settings.LastMovedRoot is { } oldRoot)
                await _shell.Library.ReloadAfterRelocationAsync(oldRoot, AppServices.Current.Downloads.RootPath);
            else if (_shell.Settings.PreviousDownloadRoot is { } previous)
                await _shell.Library.ReloadAfterRelocationAsync(previous, AppServices.Current.Downloads.RootPath, movedFiles: false);
            else await _shell.Library.ReloadSourcesAsync();
        }
        else
        {
            _shell.Library.ApplyFilter();
        }

        _shell.RaiseAccount();
    }

    private void OnWebLoginRequested(object? sender, EventArgs e)
    {
        if (_shell is null || _shell.Settings.IsSaving) return;
        if (_loginPage is not null && _documents.Contains(_loginPage)) { ShowDocument(_loginPage); return; }
        var vm = new WebLoginViewModel(AppServices.Current);
        var page = new WebLoginPage(vm) { Title = "登录账号" };
        _loginPage = page;
        vm.Completed += (_, result) => { _shell?.ApplyLoginResult(result); if (_documents.LastOrDefault() == page) PopDocument(); };
        PushDocument(page);
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shell?.Settings.IsSaving == true) { e.Cancel = true; _shell.Notify("请等待目录迁移完成，或在设置页取消迁移后退出"); return; }
        _closing = true;
        if (_shell is null) return;

        AppSettings settings = AppServices.Current.Settings;
        settings.WindowWidth = Width;
        settings.WindowHeight = Height;
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        AppServices.Current.TrySaveSettings(out _);
        AppServices.Current.Progress.Save(out _);

        _lifetime.Cancel();
        ClearDocuments();
    }

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if (_shell is null) return;
        if (sender is Button { Tag: NavItem item })
        {
            ClearDocuments();
            _shell.SelectedNav = item;
            ShowPage(item.Page);
        }
    }

    private void OnToggleSite(object? sender, RoutedEventArgs e)
    {
        _shell?.ToggleSiteCommand.Execute(null);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        bool control = (e.KeyModifiers & (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control)) != 0;
        if ((e.Key == Key.Left && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) || (e.Key == Key.Escape && _documents.Count > 0))
        {
            PopDocument(); e.Handled = true; return;
        }

        if (e.Key == Key.F && control)
        {
            if (ContentHost.Content is LibraryPage libraryPage) libraryPage.FocusSearch();
            else if (ContentHost.Content is OnlineFeedPage onlinePage) onlinePage.FocusSearch();
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
        if (_shell is null || _shell.Settings.IsSaving) return;
        var vm = new OnlineGalleryViewModel(AppServices.Current, _shell, summary);
        vm.ReadRequested += OnOnlineReadRequested;
        vm.TagSearchRequested += (_, term) => _shell.SearchTag(term);
        vm.TorrentsRequested += (_, details) => PushDocument(new TorrentPage(new TorrentViewModel(AppServices.Current, details)) { Title = "种子" });
        vm.ArchiveRequested += (_, details) => PushDocument(new ArchivePage(_shell.Downloads.GetArchiveSession(details, _shell)) { Title = "归档下载" });
        PushDocument(new OnlineGalleryPage(vm) { Title = summary.Title });
    }

    private async void OnLocalReadRequested(object? sender, GalleryItemViewModel item)
    {
        if (_shell?.Settings.IsSaving == true) return;
        if (_documents.LastOrDefault() is ReaderPage existing && existing.ViewModel.Source.ProgressKey == item.WorkId) return;
        if (!_openingReaders.Add(item.WorkId)) return;
        try
        {
            var source = await Task.Run(() => new LocalReaderSource(AppServices.Current, item.Info), _lifetime.Token);
            if (!_closing && _shell?.Settings.IsSaving != true) PushDocument(new ReaderPage(new ReaderViewModel(AppServices.Current, source)) { Title = item.Title });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _shell?.Notify("打开本地阅读失败：" + ex.Message); }
        finally { _openingReaders.Remove(item.WorkId); }
    }

    private async void OnOnlineReadRequested(object? sender, OnlineReadRequest request)
    {
        if (_shell is null || _shell.Settings.IsSaving) return;
        string workId = request.Details.Gid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_documents.LastOrDefault() is ReaderPage current && current.ViewModel.Source.ProgressKey == workId) return;
        if (!_openingReaders.Add(workId)) return;
        try
        {
            _shell.Notify("正在读取页面列表…");
            var pages = await AppServices.Current.Online.GetAllPagesAsync(request.Details.Gid, request.Details.Token, request.Details.Pages, null, _lifetime.Token);
            if (_closing || _shell.Settings.IsSaving) return;
            if (pages.Count == 0) { _shell.Notify("没有可阅读的页面"); return; }
            var source = new OnlineReaderSource(AppServices.Current, request.Details, pages, request.Quality);
            PushDocument(new ReaderPage(new ReaderViewModel(AppServices.Current, source)) { Title = request.Details.DisplayTitle });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _shell.Notify("在线读取失败：" + ex.Message); }
        finally { _openingReaders.Remove(workId); }
    }

    private void OpenLocalDetails(GalleryItemViewModel item)
    {
        var page = new GalleryDetailsPage(item, AppServices.Current) { Title = item.Title };
        page.ReadRequested += OnLocalReadRequested;
        PushDocument(page);
    }

    private void OpenVersions(GalleryItemViewModel item)
    {
        var vm = new VersionsViewModel(item, AppServices.Current);
        var page = new VersionsPage(vm) { Title = "本地副本" };
        page.CloseRequested += (_, _) =>
        {
            if (vm.ReadVersion is { } read) { _shell!.Library.SetPreferredSource(item, read); OnLocalReadRequested(this, item); }
            else if (vm.DefaultVersion is { } selected) _shell!.Library.SetPreferredSource(item, selected);
        };
        PushDocument(page);
    }

    private void PushDocument(DocumentPage page)
    {
        if (page is ReaderPage reader) reader.ImmersiveChanged += OnReaderImmersiveChanged;
        _documents.Add(page);
        page.CloseRequested += OnDocumentClose;
        ShowDocument(page);
    }
    private void OnDocumentClose(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _documents.LastOrDefault())) PopDocument();
    }
    private void ShowDocument(DocumentPage page)
    {
        ContentHost.Content = page;
        DocumentToolbar.IsVisible = page is not ReaderPage;
        DocumentTitle.Text = page.Title;
        UpdateAdaptiveLayout();
    }
    private void PopDocument()
    {
        if (_documents.Count == 0) return;
        var page = _documents[^1];
        _documents.RemoveAt(_documents.Count - 1);
        page.CloseRequested -= OnDocumentClose;
        if (page is ReaderPage reader) reader.ImmersiveChanged -= OnReaderImmersiveChanged;
        page.Dispose();
        UpdateAdaptiveLayout();
        if (ReferenceEquals(page, _loginPage)) _loginPage = null;
        _shell?.Library.RefreshAllProgress();
        _shell?.History.Reload();
        if (_documents.Count > 0) ShowDocument(_documents[^1]);
        else { DocumentToolbar.IsVisible = false; ShowPage(_shell?.CurrentPage); }
    }
    private void ClearDocuments()
    {
        foreach (var page in _documents) { page.CloseRequested -= OnDocumentClose; if (page is ReaderPage reader) reader.ImmersiveChanged -= OnReaderImmersiveChanged; page.Dispose(); }
        _documents.Clear(); _loginPage = null;
        UpdateAdaptiveLayout();
        DocumentToolbar.IsVisible = false;
        _shell?.Library.RefreshAllProgress();
    }
    private void OnGoBack(object? sender, RoutedEventArgs e) => PopDocument();
    internal void ShowDiagnosticPage(DocumentPage page) => PushDocument(page);
    private void OnReaderImmersiveChanged(object? sender, bool immersive) => UpdateAdaptiveLayout();
    private void OnToggleSidebar(object? sender, RoutedEventArgs e)
    {
        bool compact = _sidebarOverride ?? (AppServices.Current.Settings.SidebarCollapsed || Bounds.Width < 920);
        _sidebarOverride = !compact;
        AppServices.Current.Settings.SidebarCollapsed = !compact;
        AppServices.Current.TrySaveSettings(out _);
        UpdateAdaptiveLayout();
    }
    private void UpdateAdaptiveLayout()
    {
        bool narrow = Bounds.Width < 920;
        if (_lastNarrow != narrow) { _sidebarOverride = null; _lastNarrow = narrow; }
        bool immersive = ActiveReader?.IsImmersive == true;
        bool compact = _sidebarOverride ?? (AppServices.Current.Settings.SidebarCollapsed || Bounds.Width < 920);
        NavigationRail.IsVisible = !immersive;
        LayoutRoot.ColumnDefinitions[0].Width = new GridLength(immersive ? 0 : compact ? 64 : 208);
        Classes.Set("compact", compact);
    }
}

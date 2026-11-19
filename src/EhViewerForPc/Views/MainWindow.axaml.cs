using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Services;
using EhViewerForPc.ViewModels;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Navigation;
using FluentAvalonia.UI.Media.Animation;

namespace EhViewerForPc.Views;

/// <summary>
/// Navigation shell. Page views are created once and cached per view model, so
/// switching tabs keeps scroll position and avoids rebuilding lists.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Dictionary<object, Control> _views = [];
    private ShellViewModel? _shell;
    private LibraryPage? _libraryPage;
    private ImageSearchPage? _imageSearchPage;
    private long _imageReadSequence;
    private OnlineFeedPage? _onlinePage;
    private SettingsPage? _settingsPage;
    private readonly Dictionary<Control, NavigationTarget> _targets = [];
    private readonly NavigationPageFactory _navigationFactory = new();
    private WebLoginPage? _loginPage;
    private FAContentDialog? _bookmarksDialog;
    internal FAContentDialog? BookmarksDialog => _bookmarksDialog;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _openingReaders = new(StringComparer.Ordinal);
    private bool _closing;
    private bool? _lastNarrow;
    private bool _syncingNavigation;
    private ReaderViewModel? _displayedReader;

    public MainWindow()
    {
        InitializeComponent();
        ContentHost.NavigationPageFactory = _navigationFactory;
        ContentHost.Navigated += OnNavigated;
        ContentHost.Navigating += (_, e) =>
        {
            e.Cancel = _shell?.Settings.IsSaving == true;
            _navigationFactory.RestoreTarget = e.NavigationMode switch
            {
                FANavigationMode.Back => ContentHost.BackStack.LastOrDefault()?.Context as NavigationTarget,
                FANavigationMode.Forward => ContentHost.ForwardStack.LastOrDefault()?.Context as NavigationTarget,
                _ => null,
            };
        };
        ContentHost.NavigationFailed += (_, e) =>
        {
            AppServices.Current.Log.Error("Navigation failed", e.Exception);
            _shell?.Notify("页面打开失败，请重试");
        };
        Navigation.PropertyChanged += (sender, e) =>
        {
            if (e.Property != FANavigationView.IsPaneOpenProperty) return;
            // Only the account area reacts here. Persisting the pane preference
            // is done on close: the control also flips IsPaneOpen while it
            // switches display modes, which must not look like a user choice.
            UpdateAccountArea();
        };
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

    /// <summary>Exposed for the layout audit: the bottom account bar.</summary>
    public Control AccountLineControl => AccountBar;

    /// <summary>True when either account entry point occupies the pane area.</summary>
    internal bool IsAccountAreaVisible => AccountBar.IsVisible || AccountCompactButton.IsVisible;

    /// <summary>Exposed for the layout audit: whether the navigation pane is open.</summary>
    public bool IsPaneOpenForAudit => Navigation.IsPaneOpen;

    /// <summary>Exposed for the layout audit so both pane states are captured
    /// deterministically instead of depending on the persisted preference.</summary>
    internal void SetPaneOpenForAudit(bool open) => Navigation.IsPaneOpen = open;

    public ShellViewModel? Shell => _shell;

    /// <summary>Exposed for the UI smoke test (web login window, when open).</summary>
    public DocumentPage? LoginWindow => _loginPage;
    public ReaderPage? ActiveReader => ContentHost.Content as ReaderPage;
    /// <summary>Exposed for the tray regression: the retained local image search document.</summary>
    internal ImageSearchPage? ActiveImageSearch => _imageSearchPage;
    internal Func<WindowClosingEventArgs, bool>? BackgroundCloseHandler { get; set; }
    internal FAFrame NavigationFrame => ContentHost;

    public void Attach(ShellViewModel shell)
    {
        _shell = shell;
        DataContext = shell;

        AppSettings settings = AppServices.Current.Settings;
        Width = Math.Clamp(settings.WindowWidth, 540, 10000);
        Height = Math.Clamp(settings.WindowHeight, 420, 10000);
        if (settings.WindowMaximized) WindowState = WindowState.Maximized;

        shell.NavigationRequested += OnNavigationRequested;
        shell.BookmarksRequested += OnBookmarksRequested;
        shell.BookmarksDismissRequested += OnBookmarksDismissRequested;
        shell.GalleryRequested += OnGalleryRequested;
        shell.WebLoginRequested += OnWebLoginRequested;
        shell.Downloads.ArchiveRequested += (_, vm) => PushDocument(new ArchivePage(vm) { Title = vm.Title });
        shell.Downloads.ReadRequested += OnDownloadReadRequested;
        shell.Downloads.NotifyRequested += (_, message) => _shell?.Notify(message);
        shell.LocalReadRequested += OnLocalReadRequested;
        shell.Settings.ImageSearchRequested += OnImageSearchRequested;
        // The library view model outlives its page, so reading a local copy must
        // work even when the library page has never been shown.
        shell.Library.OpenGalleryRequested += OnLocalReadRequested;
        ShowPage(shell.CurrentPage);
        UpdateAdaptiveLayout();
    }

    private void OnNavigationRequested(object? sender, NavItem item)
    {
        if (!_syncingNavigation) { _imageReadSequence++; ShowPage(item.Page); }
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

        NavigateTo(view, _shell!.NavItems.First(n => ReferenceEquals(n.Page, page)));
    }

    private Control CreateView(object page)
    {
        switch (page)
        {
            case MainViewModel:
                _libraryPage = new LibraryPage();
                _libraryPage.LocalDetailsRequested += (_, item) => OpenLocalDetails(item);
                _libraryPage.VersionsRequested += (_, item) => OpenVersions(item);
                _libraryPage.ImageSearchRequested += OnImageSearchRequested;
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
                _settingsPage.RescanRequested += OnLibraryRescanRequested;
                _settingsPage.CancelRescanRequested += (_, _) => _shell?.Library.CancelScanNow();
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
        if (_loginPage is not null && _targets.ContainsKey(_loginPage)) { NavigateTo(_loginPage, _targets[_loginPage].Section); return; }
        var vm = new WebLoginViewModel(AppServices.Current);
        var page = new WebLoginPage(vm) { Title = "登录账号" };
        _loginPage = page;
        vm.Completed += (_, result) => { _shell?.ApplyLoginResult(result); if (ContentHost.Content == page) CloseDocument(page); };
        PushDocument(page);
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (BackgroundCloseHandler?.Invoke(e) == true)
        {
            SaveWindowState();
            return;
        }
        if (e.CloseReason != WindowCloseReason.OSShutdown && _shell?.Settings.IsSaving == true) { e.Cancel = true; _shell.Notify("请等待目录迁移完成，或在设置页取消迁移后退出"); return; }
        _closing = true;
        SaveWindowState();
        _lifetime.Cancel();
        ClearDocuments();
    }

    private void SaveWindowState()
    {
        if (_shell is null) return;

        AppSettings settings = AppServices.Current.Settings;
        settings.WindowWidth = Width;
        settings.WindowHeight = Height;
        if (WindowState != WindowState.Minimized) settings.WindowMaximized = WindowState == WindowState.Maximized;
        // The pane preference only means something in the wide (rail-capable)
        // layout; closing a narrow window must not overwrite it.
        if (Width >= 920) settings.SidebarCollapsed = !Navigation.IsPaneOpen;
        AppServices.Current.TrySaveSettings(out _);
        AppServices.Current.Progress.Save(out _);
    }

    private void OnNavigationItemInvoked(object? sender, FANavigationViewItemInvokedEventArgs e)
    {
        if (e.InvokedItemContainer?.DataContext is NavItem item) _shell?.Navigate(item.Key);
    }

    private void OnToggleSite(object? sender, RoutedEventArgs e)
    {
        _shell?.ToggleSiteCommand.Execute(null);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        bool control = (e.KeyModifiers & (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control)) != 0;
        if (e.Key == Key.Right && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            if (_shell?.Settings.IsSaving != true) ContentHost.GoForward();
            e.Handled = true; return;
        }
        if ((e.Key == Key.Left && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) || (e.Key == Key.Escape && ContentHost.Content is DocumentPage))
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
                    // F5 re-applies the current filter/projection only. A full
                    // rescan lives in settings so a key press cannot trigger a
                    // multi-minute disk scan.
                    library.ApplyFilter();
                    _shell.Notify("列表已刷新；重新扫描全部来源请在设置 → 本地库中操作");
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
        vm.FavoriteChanged += (_, _) => _shell?.Favorites.RefreshAfterFavoriteChange();
        vm.TagSearchRequested += (_, term) => _shell.SearchTag(term);
        vm.TorrentsRequested += (_, details) => PushDocument(new TorrentPage(new TorrentViewModel(AppServices.Current, details)) { Title = "种子" });
        vm.ArchiveRequested += (_, details) => PushDocument(new ArchivePage(_shell.Downloads.GetArchiveSession(details, _shell)) { Title = "归档下载" });
        PushDocument(new OnlineGalleryPage(vm) { Title = summary.Title });
    }

    private async void OnDownloadReadRequested(object? sender, DownloadReadRequest request)
    {
        _imageReadSequence++;
        if (_shell?.Settings.IsSaving == true) return;
        if (ActiveReader is { } existing &&
            string.Equals(existing.ViewModel.Source.StableId, request.Info.StableId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_openingReaders.Add(request.Info.StableId)) return;
        try
        {
            var source = await Task.Run(
                () => new LocalReaderSource(AppServices.Current, request.Info), _lifetime.Token);
            if (!_closing && _shell?.Settings.IsSaving != true)
            {
                PushDocument(new ReaderPage(new ReaderViewModel(AppServices.Current, source))
                {
                    Title = request.Info.DisplayTitle,
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _shell?.Notify("打开下载阅读失败：" + ex.Message); }
        finally { _openingReaders.Remove(request.Info.StableId); }
    }

    private async void OnLocalReadRequested(object? sender, GalleryItemViewModel item)
    {
        _imageReadSequence++;
        if (_shell?.Settings.IsSaving == true) return;
        if (ActiveReader is { } existing && existing.ViewModel.Source.ProgressKey == item.WorkId) return;
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

    private void OnImageSearchRequested(object? sender, EventArgs e) => OpenImageSearch();

    /// <summary>Opens or reactivates the retained local image search document.</summary>
    internal void OpenImageSearch()
    {
        if (_shell is null || _shell.Settings.IsSaving) return;
        if (_imageSearchPage is null)
        {
            var viewModel = new ImageSearchViewModel(AppServices.Current,
                () => _shell.Library.SnapshotAllWorks().SelectMany(item => item.Versions).DistinctBy(g => (g.SourceId, g.StableId)).ToArray());
            viewModel.ReadRequested += OnImageHitReadRequested;
            viewModel.SettingsRequested += OnImageSearchSettingsRequested;
            _imageSearchPage = new ImageSearchPage(viewModel);
        }
        PushDocument(_imageSearchPage);
    }

    private void OnImageSearchSettingsRequested(object? sender, EventArgs e)
    {
        if (_shell is null) return;
        _shell.Settings.SelectedCategoryIndex = 5;
        _shell.Navigate("settings");
    }

    private async void OnImageHitReadRequested(object? sender, LocalPageReadRequest request)
    {
        if (_shell is null || _shell.Settings.IsSaving) return;
        long sequence = ++_imageReadSequence;
        try
        {
            var existing = _targets.Keys.OfType<ReaderPage>().FirstOrDefault(page => page.ViewModel.Source is LocalReaderSource local
                && local.Gallery.SourceId == request.Gallery.SourceId && local.Gallery.StableId == request.Gallery.StableId);
            if (existing is not null)
            {
                if (!existing.ViewModel.TryNavigateToPageNumber(request.PageNumber)) throw new FileNotFoundException("命中页已不在该副本中。");
                RemoveOtherReaders(request.Gallery.WorkId, existing);
                PushDocument(existing); return;
            }
            var source = await Task.Run(() => new LocalReaderSource(AppServices.Current, request.Gallery), _lifetime.Token);
            if (_closing || sequence != _imageReadSequence || _shell.Settings.IsSaving) return;
            var reader = new ReaderPage(new ReaderViewModel(AppServices.Current, source, request.PageNumber)) { Title = request.Gallery.DisplayTitle };
            RemoveOtherReaders(request.Gallery.WorkId, reader); PushDocument(reader);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _shell.Notify("打开命中页失败：" + error.Message); }
    }

    private void RemoveOtherReaders(string workId, ReaderPage keep)
    {
            foreach (var prior in _targets.Keys.OfType<ReaderPage>().Where(page => !ReferenceEquals(page, keep) && page.ViewModel.Source.ProgressKey == workId).ToArray())
            {
                if (ReferenceEquals(ContentHost.Content, prior)) CloseDocument(prior);
                foreach (var stack in new[] { ContentHost.BackStack, ContentHost.ForwardStack })
                    for (int i = stack.Count - 1; i >= 0; i--)
                        if (stack[i].Context is NavigationTarget target && ReferenceEquals(target.View, prior)) stack.RemoveAt(i);
            }
            ReleaseUnusedDocuments();
    }

    private async void OnOnlineReadRequested(object? sender, OnlineReadRequest request)
    {
        _imageReadSequence++;
        if (_shell is null || _shell.Settings.IsSaving) return;
        string workId = request.Details.Gid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (ActiveReader is { } current && current.ViewModel.Source.ProgressKey == workId) return;
        if (!_openingReaders.Add(workId)) return;
        try
        {
            EhGalleryDetails details = request.Details;
            OnlinePageIndex index = OnlinePageIndex.ForClient(
                AppServices.Current.Online, details.Gid, details.Token, details.Pages);

            // The detail page normally carries the page count. Only when it does
            // not is a single preview request awaited — never the whole list.
            if (details.Pages <= 0)
            {
                _shell.Notify("正在获取页数…");
                await index.PrimeAsync(_lifetime.Token).ConfigureAwait(true);
                if (_closing || _shell.Settings.IsSaving)
                {
                    index.Dispose();
                    return;
                }
            }

            if (index.PageCount <= 0)
            {
                index.Dispose();
                _shell.Notify("没有可阅读的页面");
                return;
            }

            // Navigation happens before any page image is requested: the reader
            // shows logical page slots immediately and resolves pages on demand.
            var source = new OnlineReaderSource(AppServices.Current, details, index, request.Quality);
            PushDocument(new ReaderPage(new ReaderViewModel(AppServices.Current, source)) { Title = details.DisplayTitle });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _shell.Notify("在线读取失败：" + ex.Message); }
        finally { _openingReaders.Remove(workId); }
    }

    /// <summary>
    /// Runs the full rescan from settings. The library toolbar no longer offers
    /// this (and F5 never starts it), so a scan is always explicit.
    /// </summary>
    private async void OnLibraryRescanRequested(object? sender, EventArgs e)
    {
        if (_shell is null) return;
        if (_shell.Library.IsScanning)
        {
            _shell.Settings.CompleteRescan("已有扫描正在进行");
            return;
        }

        try
        {
            await _shell.Library.RescanAllAsync();
            bool cancelled = _shell.Library.StatusText.Contains("取消", StringComparison.Ordinal);
            _shell.Settings.CompleteRescan(cancelled ? "已取消扫描" : null);
            if (!cancelled) _shell.Notify("本地库重新扫描完成");
        }
        catch (OperationCanceledException)
        {
            _shell.Settings.CompleteRescan("已取消扫描");
        }
        catch (Exception ex)
        {
            _shell.Settings.CompleteRescan(ex.Message);
        }
    }

    private void OpenLocalDetails(GalleryItemViewModel item)
    {
        var page = new GalleryDetailsPage(item, AppServices.Current) { Title = item.Title };
        page.ReadRequested += OnLocalReadRequested;
        page.VersionsRequested += (_, selected) => OpenVersions(selected);
        page.TagSearchRequested += (_, term) => _shell?.SearchTag(term);
        PushDocument(page);
    }

    private void OpenVersions(GalleryItemViewModel item)
    {
        var vm = new VersionsViewModel(item, AppServices.Current, _shell!.Library.ApplyScannedCopy);
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
        if (!_targets.ContainsKey(page))
        {
            if (page is ReaderPage reader) reader.ImmersiveChanged += OnReaderImmersiveChanged;
            page.CloseRequested += OnDocumentClose;
        }
        NavigateTo(page, _shell!.SelectedNav!);
    }
    private void OnDocumentClose(object? sender, EventArgs e)
    {
        if (sender is DocumentPage page && ReferenceEquals(page, ContentHost.Content)) CloseDocument(page);
    }
    private void NavigateTo(Control view, NavItem section)
    {
        if (_shell?.Settings.IsSaving == true || ReferenceEquals(ContentHost.Content, view)) return;
        if (!_targets.TryGetValue(view, out var target)) _targets[view] = target = new(view, section);
        ContentHost.NavigateFromObject(target, new FAFrameNavigationOptions
        {
            IsNavigationStackEnabled = true,
            TransitionInfoOverride = StartupOptions.Current.UiSmoke ? new FASuppressNavigationTransitionInfo() : new FAEntranceNavigationTransitionInfo(),
        });
        // Bound retained document resources while preserving ordinary back/forward history.
        while (ContentHost.BackStack.Count > 24) ContentHost.BackStack.RemoveAt(0);
        ReleaseUnusedDocuments();
    }
    private void OnNavigated(object sender, FANavigationEventArgs e)
    {
        _navigationFactory.RestoreTarget = null;
        if (e.Content is not Control view || !_targets.TryGetValue(view, out var target)) return;
        _syncingNavigation = true;
        try { _shell!.SelectedNav = target.Section; }
        finally { _syncingNavigation = false; }
        DocumentTitle.Text = view is ReaderPage ? "阅读" : view is OnlineGalleryPage or GalleryDetailsPage ? "详情"
            : view is DocumentPage document ? document.Title : target.Section.Label;
        UpdateHeaderForContent();
        var previousReader = _displayedReader;
        _displayedReader = (view as ReaderPage)?.ViewModel;
        if (previousReader is not null && !ReferenceEquals(previousReader, _displayedReader))
        {
            previousReader.SaveProgress();
            _shell?.Library.RefreshWorkProgress(previousReader.Source.ProgressKey);
            _shell?.History.Reload();
        }
        // 从阅读器返回下载页时 SelectedNav 没有变化，ActivatePage 不会被调用；
        // 这里补一次同步，让刚补齐或重下过的页立刻从「缺页」里消失。
        if (view is DownloadsPage && previousReader is not null && target.Section.Page is DownloadsViewModel downloads) downloads.SyncAll();
        UpdateAdaptiveLayout();
    }

    /// <summary>
    /// The reader owns the shell header: the work title and the reading commands
    /// replace the generic section title, so the page body can start at the
    /// canvas without a second title bar of its own.
    /// </summary>
    private void UpdateHeaderForContent()
    {
        bool reader = ContentHost.Content is ReaderPage;
        NavigationHeader.IsVisible = !reader;
        ReaderHeader.IsVisible = reader;
        ReaderHeader.DataContext = reader ? (ContentHost.Content as ReaderPage)?.DataContext : null;
    }
    private void PopDocument()
    {
        _imageReadSequence++;
        if (_shell?.Settings.IsSaving == true) return;
        ContentHost.GoBack();
    }
    private void CloseDocument(DocumentPage page)
    {
        PopDocument();
        for (int i = ContentHost.ForwardStack.Count - 1; i >= 0; i--)
            if (ContentHost.ForwardStack[i].Context is NavigationTarget target && ReferenceEquals(target.View, page))
                ContentHost.ForwardStack.RemoveAt(i);
        ReleaseUnusedDocuments();
    }
    private void ReleaseUnusedDocuments()
    {
        var retained = ContentHost.BackStack.Concat(ContentHost.ForwardStack)
            .Select(e => (e.Context as NavigationTarget)?.View).ToHashSet();
        if (ContentHost.Content is Control current) retained.Add(current);
        foreach (var page in _targets.Keys.OfType<DocumentPage>().Where(p => !retained.Contains(p)).ToArray())
        {
            page.CloseRequested -= OnDocumentClose;
            if (page is ReaderPage reader) reader.ImmersiveChanged -= OnReaderImmersiveChanged;
            page.Dispose();
            _targets.Remove(page);
            if (ReferenceEquals(page, _loginPage)) _loginPage = null;
            if (ReferenceEquals(page, _imageSearchPage)) _imageSearchPage = null;
        }
    }
    private void ClearDocuments()
    {
        _bookmarksDialog?.Hide();
        _displayedReader = null;
        ContentHost.Content = null;
        ContentHost.BackStack.Clear();
        ContentHost.ForwardStack.Clear();
        ReleaseUnusedDocuments();
        if (_shell is not null)
        {
            _shell.NavigationRequested -= OnNavigationRequested;
            _shell.BookmarksRequested -= OnBookmarksRequested;
            _shell.BookmarksDismissRequested -= OnBookmarksDismissRequested;
        }
    }

    private async void OnBookmarksRequested(object? sender, EventArgs e)
    {
        if (_shell is null || _closing || _bookmarksDialog is not null) return;
        var view = new BookmarksView
        {
            DataContext = _shell.Bookmarks,
            Width = Math.Clamp(Bounds.Width - 112, 280, 780),
            MaxHeight = Math.Max(140, Bounds.Height - 200),
        };
        var dialog = new FAContentDialog
        {
            Title = "书签栏", Content = view, CloseButtonText = "关闭",
            FontSize = 13, FontWeight = Avalonia.Media.FontWeight.Normal,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 860d;
        _bookmarksDialog = dialog;
        try { await dialog.ShowAsync(this); }
        catch (Exception error) { _shell.Notify("书签栏打开失败：" + error.Message); }
        finally { _bookmarksDialog = null; }
    }

    private void OnBookmarksDismissRequested(object? sender, EventArgs e) => _bookmarksDialog?.Hide();
    private void OnGoBack(object? sender, FANavigationViewBackRequestedEventArgs e) => PopDocument();
    internal void ShowDiagnosticPage(DocumentPage page) => PushDocument(page);
    private void OnReaderImmersiveChanged(object? sender, bool immersive) => UpdateAdaptiveLayout();

    // ------------------------------------------------- reader header commands
    private async void OnReaderSingleMode(object? sender, RoutedEventArgs e)
    {
        if (ActiveReader is not { } reader) return;
        await reader.SetSingleModeAsync();
        if (sender is Avalonia.Controls.Primitives.ToggleButton button)
            button.SetCurrentValue(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, reader.ViewModel.IsSingleMode);
    }
    private async void OnReaderContinuousMode(object? sender, RoutedEventArgs e) { if (ActiveReader is { } r) await r.ToggleContinuousModeAsync(); }
    private async void OnReaderHorizontalMode(object? sender, RoutedEventArgs e) { if (ActiveReader is { } r) await r.ToggleHorizontalModeAsync(); }
    private void OnReaderImmersive(object? sender, RoutedEventArgs e) => ActiveReader?.ToggleImmersive();
    private void OnReaderFullscreen(object? sender, RoutedEventArgs e) => ActiveReader?.ToggleFullscreen();
    private void OnReaderLeftToRight(object? sender, RoutedEventArgs e) => ActiveReader?.SetDirection(false);
    private void OnReaderRightToLeft(object? sender, RoutedEventArgs e) => ActiveReader?.SetDirection(true);
    private void OnReaderBackground(object? sender, RoutedEventArgs e) => ActiveReader?.ToggleBackground();
    private void OnReaderHelp(object? sender, RoutedEventArgs e) => ActiveReader?.ShowHelpFlyout();
    private void OnReaderClose(object? sender, RoutedEventArgs e) => ActiveReader?.Close();

    private void UpdateAdaptiveLayout()
    {
        if (_bookmarksDialog?.Content is BookmarksView bookmarks)
        {
            bookmarks.Width = Math.Clamp(Bounds.Width - 112, 280, 780);
            bookmarks.MaxHeight = Math.Max(140, Bounds.Height - 200);
        }
        bool narrow = Bounds.Width < 920;
        bool immersive = ActiveReader?.IsImmersive == true;
        Navigation.IsPaneVisible = !immersive;
        Navigation.IsPaneToggleButtonVisible = !immersive;
        Navigation.IsBackButtonVisible = !immersive;
        Navigation.AlwaysShowHeader = !immersive;
        Navigation.Header = immersive ? null : HeaderHost;
        if (!immersive) UpdateHeaderForContent();
        // Narrow headers keep icons only so the command row always fits;
        // the labels stay available as tooltips.
        ReaderCommands.Classes.Set("compact", narrow);
        foreach (var command in ReaderCommands.Children.OfType<Button>())
        {
            command.MinWidth = narrow ? 36 : 0;
            command.Width = narrow ? 36 : double.NaN;
            command.Padding = new Thickness(narrow ? 6 : 8, 0);
        }
        if (_lastNarrow != narrow)
        {
            Navigation.IsPaneOpen = !narrow && !AppServices.Current.Settings.SidebarCollapsed;
            _lastNarrow = narrow;
        }
        UpdateAccountArea();
    }

    /// <summary>
    /// One account row when the pane is expanded, one icon when it is a rail.
    /// In minimal mode a closed pane is off-canvas, so nothing is drawn there;
    /// the hamburger reveals the full row.
    /// </summary>
    private void UpdateAccountArea()
    {
        bool open = Navigation.IsPaneOpen;
        bool immersive = ActiveReader?.IsImmersive == true;
        bool minimal = Bounds.Width < Navigation.CompactModeThresholdWidth;

        AccountBar.Width = open ? Navigation.OpenPaneLength : Navigation.CompactPaneLength;
        AccountRow.IsVisible = open;
        AccountCompactButton.Width = Navigation.CompactPaneLength;
        // Both forms must respect immersive: the pane is off-canvas, so the
        // compact button would otherwise float over the reader's bottom bar.
        AccountCompactButton.IsVisible = !immersive && !open && !minimal;
        AccountBar.IsVisible = !immersive && (open || !minimal);
    }
}

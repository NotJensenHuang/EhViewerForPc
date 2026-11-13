using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.Services;
using EhViewerForPc.ViewModels;
using EhViewerForPc.Views;

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public sealed class OnlineParityIntegrationTests
{
    [AvaloniaFact]
    public void EmptyFilteredBatchesPauseAutomaticLoadsAndRepeatedContinuationStops()
    {
        using var scope = new Fixture(); using var shell = new ShellViewModel(scope.Services);
        scope.Services.Settings.Online.Filters.Add(new FilterRule { Target = FilterTarget.Title, Text = "only this absent title", Include = true });
        for (int page = 0; page < 3; page++)
        {
            var batch = new EhGalleryListPage { HasMore = true, NextUrl = $"https://e-hentai.org/?page={page + 1}" };
            batch.Items.Add(new() { Gid = page + 1, Token = "fixture", Title = "Filtered" });
            shell.Online.ApplyPage(batch, $"https://e-hentai.org/?page={page}", reset: page == 0);
        }
        Assert.Empty(shell.Online.Items);
        Assert.True(shell.Online.AutoLoadPaused);
        Assert.False(shell.Online.CanAutoLoadMore);
        Assert.True(shell.Online.ShowLoadMoreButton);
        Assert.False(shell.Online.ShowEmpty);
        shell.Online.ApplyPage(new() { HasMore = true, NextUrl = "https://e-hentai.org/?page=3" }, "https://e-hentai.org/?page=3", reset: false);
        Assert.False(shell.Online.HasMore);
    }

    [AvaloniaFact]
    public async Task ScrollingNearEndLoadsMoreWithoutEagerlyCreatingEveryCard()
    {
        using var scope = new Fixture(); using var shell = new ShellViewModel(scope.Services); using var server = new ListServer();
        await shell.Online.LoadAsync(reset: true);
        var first = new EhGalleryListPage { HasMore = true, NextUrl = server.Address + "?page=1" };
        first.Items.Add(new() { Gid = 100, Token = "token", Title = "First" });
        for (int i = 0; i < 24; i++) first.Items.Add(new() { Gid = 1000 + i, Token = "token", Title = "Existing " + i });
        shell.Online.ApplyPage(first, server.Address, reset: true);
        var page = new OnlineFeedPage { DataContext = shell.Online };
        var window = new Window { Width = 1000, Height = 650, Content = page };
        try
        {
            window.Show(); await Task.Delay(100); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Empty(server.Requests);
            var scroll = page.FindControl<ScrollViewer>("GalleryScroll")!;
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            for (int i = 0; i < 100 && shell.Online.HasMore; i++)
            {
                scroll.Offset = new Vector(0, scroll.Extent.Height);
                await Task.Delay(20); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            }
            Assert.False(shell.Online.HasMore, $"Requests {server.Requests.Count}, count {shell.Online.Items.Count}, offset {scroll.Offset}, extent {scroll.Extent}, viewport {scroll.Viewport}, status {shell.Online.StatusText}");
            Assert.Equal(28, shell.Online.Items.Count);
            Assert.Equal(3, server.Requests.Count);
            Assert.True(page.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("gallery-card")) < shell.Online.Items.Count);
            Assert.DoesNotContain(page.GetVisualDescendants().OfType<Button>(), b => b.Content is "上一页" or "下一页");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ResetCancelsInflightAppendAndLateResponseCannotPolluteNewFeed()
    {
        using var scope = new Fixture(); using var shell = new ShellViewModel(scope.Services); using var server = new ListServer(delayFirst: true);
        var first = new EhGalleryListPage { HasMore = true, NextUrl = server.Address + "?page=1" };
        first.Items.Add(new() { Gid = 100, Token = "token", Title = "Old feed" });
        shell.Online.ApplyPage(first, server.Address, reset: true);
        Task append = shell.Online.LoadMoreCommand.ExecuteAsync(null);
        await server.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await shell.Online.LoadMoreCommand.ExecuteAsync(null);
        Assert.Single(server.Requests);
        shell.Online.SelectedTab = shell.Online.Tabs.Single(t => t.Key == "popular");
        server.ReleaseFirst.TrySetResult(true);
        await append;
        for (int i = 0; i < 100 && shell.Online.IsBusy; i++) await Task.Delay(10);
        Assert.Empty(shell.Online.Items);
        Assert.Equal("popular", shell.Online.SelectedTab?.Key);
        Assert.False(shell.Online.LoadMoreFailed);
    }

    [AvaloniaFact]
    public async Task LazyLoadingAppendsServerUrlsDeduplicatesAndStopsAtTheEnd()
    {
        using var scope = new Fixture(); using var shell = new ShellViewModel(scope.Services); using var server = new ListServer();
        var first = new EhGalleryListPage { HasMore = true, NextUrl = server.Address + "?page=1" };
        first.Items.Add(new() { Gid = 100, Token = "token", Title = "Page 0" });
        shell.Online.ApplyPage(first, server.Address + "?page=0", reset: true);
        await shell.Online.LoadMoreCommand.ExecuteAsync(null);
        await shell.Online.LoadMoreCommand.ExecuteAsync(null);
        await shell.Online.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(new long[] { 100, 101, 102, 103 }, shell.Online.Items.Select(i => i.Gid));
        Assert.False(shell.Online.HasMore);
        await shell.Online.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "/?page=1", "/?page=2", "/?page=3" }, server.Requests);
    }

    [AvaloniaFact]
    public async Task LazyLoadFailureRetainsItemsAndRetriesTheSameUrl()
    {
        using var scope = new Fixture(); using var shell = new ShellViewModel(scope.Services); using var server = new ListServer(failFirst: true);
        var first = new EhGalleryListPage { HasMore = true, NextUrl = server.Address + "?page=1" };
        first.Items.Add(new() { Gid = 100, Token = "token", Title = "Loaded" });
        shell.Online.ApplyPage(first, server.Address, reset: true);
        await shell.Online.LoadMoreCommand.ExecuteAsync(null);
        Assert.True(shell.Online.LoadMoreFailed);
        Assert.False(shell.Online.CanAutoLoadMore);
        Assert.Equal(100, Assert.Single(shell.Online.Items).Gid);
        await shell.Online.LoadMoreCommand.ExecuteAsync(null);
        Assert.False(shell.Online.LoadMoreFailed);
        Assert.Equal(new long[] { 100, 101 }, shell.Online.Items.Select(i => i.Gid));
        Assert.Equal(new[] { "/?page=1", "/?page=1" }, server.Requests);
    }

    [AvaloniaFact]
    public async Task MergedFeedTabsRetainOnePageAndBookmarksOpenWithoutNavigation()
    {
        using var scope = new Fixture(); using var shell = new ShellViewModel(scope.Services);
        var window = new MainWindow(shell);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(shell.NavItems, n => n.Key is "subscriptions" or "rankings");
            Assert.Equal(new[] { "首页", "热门", "订阅", "排行" }, shell.Online.Tabs.Select(t => t.Label));
            Assert.DoesNotContain(shell.NavItems, n => n.Key == "bookmarks");
            shell.Online.SelectedTab = shell.Online.Tabs.Single(t => t.Key == "watched");
            var subscribed = Assert.IsType<OnlineFeedPage>(window.NavigationFrame.Content);
            Assert.Same(shell.Online, subscribed.DataContext); Assert.Contains("/watched", shell.Online.BuildUrl(0));
            Assert.Equal("Subscription", shell.Online.SelectedSearchMode?.Key);
            shell.Online.SelectedTab = shell.Online.Tabs.Single(t => t.Key == "toplist"); Assert.True(shell.Online.ShowRankingOverview);
            await shell.Online.LoadAsync(reset: true);
            shell.Online.ApplyRankingOverview([new("画廊", [new("昨日", 15, [new("1. Fixture gallery", "https://e-hentai.org/g/42/token/")], true)])]);
            Capture(window, "navigation-rankings.png");
            await shell.Online.BrowseRankingPeriodCommand.ExecuteAsync(new EhRankingPeriod("本月", 13, [], true));
            Assert.False(shell.Online.ShowRankingOverview); Assert.True(shell.Online.ShowToplistSelector);
            Assert.Equal("https://e-hentai.org/toplist.php?tl=13&p=2", shell.Online.BuildUrl(2));
            shell.Online.SelectedTab = shell.Online.Tabs.Single(t => t.Key == "watched"); Assert.Same(subscribed, window.NavigationFrame.Content);
            shell.Online.ShowAdvanced = false;
            int backCount = window.NavigationFrame.BackStack.Count;
            shell.Online.OpenBookmarksCommand.Execute(null);
            Assert.NotNull(window.BookmarksDialog);
            Assert.Same(subscribed, window.NavigationFrame.Content);
            Assert.Equal(backCount, window.NavigationFrame.BackStack.Count);
            Assert.False(shell.Online.ShowAdvanced);
            await Task.Delay(100); Dispatcher.UIThread.RunJobs();
            Capture(window, "navigation-bookmarks.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FullSearchOptionsSurviveBookmarkAddEditRestoreExportAndImport()
    {
        using var scope = new Fixture(); using var shell = new ShellViewModel(scope.Services);
        var request = CompleteRequest();
        shell.Online.ApplyRequest(request);
        AssertEquivalent(request, shell.Online.BuildRequest());
        var bookmark = SavedSearchEntry.Create("完整搜索", request); scope.Services.Settings.SavedSearches.Add(bookmark);
        shell.Bookmarks.Reload(); shell.Bookmarks.Selected = bookmark;
        shell.Bookmarks.EditName = "修改后的名称"; shell.Bookmarks.EditQuery = "artist:\"new artist$\" chinese";
        shell.Bookmarks.SaveEditCommand.Execute(null);
        Assert.Equal("修改后的名称", bookmark.Name); Assert.True(bookmark.ToRequest().OnlyExpunged);
        shell.Bookmarks.EditOptionsCommand.Execute(null);
        Assert.Same(shell.Online, shell.CurrentPage); Assert.True(shell.Online.IsEditingBookmark);
        shell.Online.SearchTorrentNames = false; shell.Online.SelectedRating = shell.Online.RatingOptions[2];
        shell.Online.SaveEditedBookmarkCommand.Execute(null);
        Assert.Same(shell.Online, shell.CurrentPage); Assert.False(bookmark.ToRequest().SearchTorrentNames);
        Assert.Equal(3, bookmark.ToRequest().MinRating);
        using var output = new MemoryStream(); await shell.Bookmarks.ExportAsync(output, json: false);
        string path = Path.Combine(scope.Root, "android-compatible.db"); File.WriteAllBytes(path, output.ToArray());
        var restored = Assert.Single(EhBookmarkTransfer.Read(path)); AssertEquivalent(bookmark.ToRequest(), restored.ToRequest());
        await shell.Bookmarks.ImportAsync(path); Assert.Single(shell.Bookmarks.Items); Assert.Contains("同名跳过 1", shell.Bookmarks.StatusText);
        scope.Services.Settings.SavedSearches.Clear(); shell.Bookmarks.Reload(); await shell.Bookmarks.ImportAsync(path);
        Assert.Single(shell.Bookmarks.Items);
        shell.Bookmarks.Selected = shell.Bookmarks.Items[0]; shell.Bookmarks.OpenCommand.Execute(null);
        Assert.Same(shell.Online, shell.CurrentPage); AssertEquivalent(restored.ToRequest(), shell.Online.BuildRequest());
        Assert.False(shell.Online.ShowAdvanced);
    }

    [AvaloniaFact]
    public void BookmarkOrderAndTagLookupUseCanonicalTerms()
    {
        using var scope = new Fixture();
        var database = new TagTranslationDatabase(); database.Tags["artist:sample artist"] = "示例画师";
        JsonFileStore.Save(scope.Services.Paths.TagTranslationFile, database, StorageJsonContext.Default.TagTranslationDatabase, out _);
        using var shell = new ShellViewModel(scope.Services);
        shell.Online.TagKeyword = "示例"; shell.Online.LookupTagsCommand.Execute(null);
        var tag = Assert.Single(shell.Online.TagMatches); shell.Online.InsertTagCommand.Execute(tag);
        Assert.Equal("artist:\"sample artist$\"", shell.Online.Query);
        shell.Bookmarks.AddCurrentCommand.Execute(null); var first = shell.Bookmarks.Items[0];
        shell.Bookmarks.AddCurrentCommand.Execute(null); var second = shell.Bookmarks.Items[1];
        shell.Bookmarks.MoveUpCommand.Execute(null); Assert.Same(second, shell.Bookmarks.Items[0]);
        shell.Bookmarks.MoveDownCommand.Execute(null); Assert.Same(first, shell.Bookmarks.Items[0]);
        shell.Bookmarks.DeleteCommand.Execute(null); Assert.Single(shell.Bookmarks.Items);
    }

    [AvaloniaFact]
    public async Task InvalidPageRangeDoesNotStartSearchAndDisablingAdvancedRemovesIt()
    {
        using var scope = new Fixture(); using var shell = new ShellViewModel(scope.Services);
        shell.Online.MinPages = "300"; shell.Online.MaxPages = "20";
        await shell.Online.SearchCommand.ExecuteAsync(null);
        Assert.False(shell.Online.IsSearchMode); Assert.Contains("最少页数", shell.Online.StatusText);
        shell.Online.SelectedLanguage = shell.Online.Languages[1]; shell.Online.AdvancedEnabled = false;
        var request = shell.Online.BuildRequest(); Assert.Equal(0, request.MinPages); Assert.False(request.AdvancedEnabled); Assert.Empty(request.Language);
    }

    [AvaloniaFact]
    public async Task MalformedBackupAndQueryImageReportErrorsWithoutLosingBookmarks()
    {
        using var scope = new Fixture(); using var shell = new ShellViewModel(scope.Services);
        shell.Bookmarks.AddCurrentCommand.Execute(null);
        string path = Path.Combine(scope.Root, "invalid.json"); File.WriteAllText(path, "[{\"name\":\"unsupported\",\"mode\":4}]");
        await shell.Bookmarks.ImportAsync(path); Assert.Single(shell.Bookmarks.Items); Assert.Contains("导入失败", shell.Bookmarks.StatusText);
        File.WriteAllText(path, "not an image"); await shell.Online.LoadSearchImageAsync(path);
        Assert.Contains("读取图片失败", shell.Online.ImageName);
    }

    [AvaloniaTheory]
    [InlineData(1200, 800, false)]
    [InlineData(540, 700, false)]
    [InlineData(1200, 800, true)]
    [InlineData(540, 700, true)]
    public async Task CompleteSearchAndBookmarkSurfacesRenderWithoutNativeWindows(int width, int height, bool light)
    {
        using var scope = new Fixture(); using var shell = new ShellViewModel(scope.Services);
        Application.Current!.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var page = new OnlineFeedPage { DataContext = shell.Online }; shell.Online.ShowAdvanced = true;
        var window = new Window { Width = width, Height = height, Content = page };
        try
        {
            window.Show(); await Task.Delay(100); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var options = page.GetVisualDescendants().OfType<CheckBox>().Where(c => c.Content is string).Select(c => c.Content!.ToString()).ToArray();
            foreach (string text in new[] { "搜索种子文件名", "仅显示有种子的画廊", "搜索低愿力标签", "搜索差评标签", "只显示被删除的画廊", "启用高级选项" }) Assert.Contains(text, options);
            var search = page.FindControl<TextBox>("SearchBox")!;
            Assert.True(search.IsEffectivelyVisible); Assert.True(search.Bounds.Width > 100);
            Capture(window, $"search-{width}-{(light ? "light" : "dark")}.png");
            var bookmarks = new BookmarksView { DataContext = shell.Bookmarks }; window.Content = bookmarks;
            shell.Bookmarks.AddCurrentCommand.Execute(null); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var buttons = bookmarks.GetVisualDescendants().OfType<Button>().ToArray();
            foreach (string id in new[] { "Bookmarks.Add", "Bookmarks.Import", "Bookmarks.Export", "Bookmarks.Open" })
                Assert.Contains(buttons, b => AutomationProperties.GetAutomationId(b) == id && b.IsEffectivelyVisible);
            Capture(window, $"bookmarks-{width}-{(light ? "light" : "dark")}.png");
        }
        finally { window.Close(); }
    }

    private static EhSearchRequest CompleteRequest() => new()
    {
        Mode = EhSearchMode.Subscription, Query = "artist:\"sample$\" chinese", CategoryMask = 6,
        SearchName = false, SearchTags = true, SearchDescription = true, SearchTorrentNames = true,
        OnlyTorrents = true, OnlyExpunged = true, SearchLowPowerTags = true, SearchDownvotedTags = true,
        DisableLanguageFilter = true, DisableUploaderFilter = true, DisableTagFilter = true, MinRating = 4, MinPages = 10, MaxPages = 250,
    };
    private static void AssertEquivalent(EhSearchRequest expected, EhSearchRequest actual) =>
        Assert.Equal(EhUrls.Search(EhSite.EHentai, expected, 0), EhUrls.Search(EhSite.EHentai, actual, 0));
    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.qa/online-parity/ui"));
        Directory.CreateDirectory(root); frame.Save(Path.Combine(root, name), PngBitmapEncoderOptions.Default);
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "EhOnlineParityUI-" + Guid.NewGuid().ToString("N"));
        internal AppServices Services { get; }
        internal Fixture()
        {
            var paths = new AppPaths(Path.Combine(Root, "data")); paths.EnsureCreated();
            var settings = new AppSettings(); settings.Download.RootPath = Path.Combine(Root, "downloads");
            settings.Download.AutoStart = false; settings.Download.AddAsSource = false;
            settings.Network.ProxyMode = ProxyMode.None;
            settings.Network.HostsOverride = "127.0.0.1 e-hentai.org\n127.0.0.1 exhentai.org";
            new SettingsStore(paths).Save(settings, out _); Services = AppServices.Initialize(paths);
        }
        public void Dispose() { Services.Dispose(); Directory.Delete(Root, true); }
    }

    private sealed class ListServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        internal List<string> Requests { get; } = [];
        internal TaskCompletionSource<bool> FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Address { get; }
        internal ListServer(bool failFirst = false, bool delayFirst = false)
        {
            _listener.Start(); Address = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/";
            _worker = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        using var client = await _listener.AcceptTcpClientAsync(_stop.Token); using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        string line = (await reader.ReadLineAsync(_stop.Token))!; string path = line.Split(' ')[1]; Requests.Add(path);
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                        FirstRequest.TrySetResult(true);
                        if (delayFirst && Requests.Count == 1) await ReleaseFirst.Task.WaitAsync(_stop.Token);
                        int page = int.Parse(path[(path.IndexOf('=') + 1)..]);
                        string html = $"<table><tr><td><a href='/g/100/token/'>Repeated</a></td></tr><tr><td><a href='/g/{100 + page}/token/'>Page {page}</a></td></tr></table>";
                        if (page < 3) html += $"<a id='dnext' href='{Address}?page={page + 1}'>Next</a>";
                        bool fail = failFirst && Requests.Count == 1;
                        byte[] body = Encoding.UTF8.GetBytes(html);
                        byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(fail ? "500 Unavailable" : "200 OK")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                        try { await stream.WriteAsync(header, _stop.Token); await stream.WriteAsync(body, _stop.Token); await stream.FlushAsync(_stop.Token); }
                        catch (IOException) { } // 客户端在切换标签时取消旧请求。
                    }
                }
                catch (OperationCanceledException) { }
                catch (SocketException) when (_stop.IsCancellationRequested) { }
            });
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); _worker.GetAwaiter().GetResult(); _stop.Dispose(); }
    }
}

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Storage;
using EhViewerReader.ImageSearch;
using EhViewerReader.Services;
using EhViewerReader.ViewModels;
using EhViewerReader.Views;

namespace EhViewerReader.ImageSearch.Ui.Tests;

public sealed class ImageSearchIntegrationTests
{
    [AvaloniaFact]
    public async Task ClipboardPasteAndPointerCropUseTheHeadlessPlatformOnly()
    {
        using var scope = new Fixture(); using var vm = new ImageSearchViewModel(scope.Services, () => []); using var page = new ImageSearchPage(vm);
        var window = new Window { Width = 1200, Height = 800, Content = page };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            using var bitmap = new Bitmap(new MemoryStream(SyntheticImages.Create(42, 200, 280).EncodePng()));
            await window.Clipboard!.SetBitmapAsync(bitmap);
            var paste = page.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetAutomationId(b) == "ImageSearch.Paste");
            paste.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => vm.HasInput);
            Assert.Equal("剪贴板图片", vm.InputName); Assert.Equal(200, vm.ImageWidth); Assert.Equal(280, vm.ImageHeight);
            var surface = page.FindControl<Grid>("PreviewSurface")!;
            Point origin = surface.TranslatePoint(default, window)!.Value;
            double left = origin.X + (surface.Bounds.Width - 200) / 2;
            window.MouseDown(new Point(left + 25, origin.Y + 35), MouseButton.Left);
            window.MouseMove(new Point(left + 125, origin.Y + 175));
            window.MouseUp(new Point(left + 125, origin.Y + 175), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.UseCrop); Assert.Equal(25, vm.CropX); Assert.Equal(35, vm.CropY);
            Assert.Equal(100, vm.CropWidth); Assert.Equal(140, vm.CropHeight);
            var rectangle = page.FindControl<Border>("CropRectangle")!;
            Assert.True(rectangle.IsVisible); Assert.Equal(100, rectangle.Width, precision: 0); Assert.Equal(140, rectangle.Height, precision: 0);
            vm.ResetCropCommand.Execute(null); Assert.False(rectangle.IsVisible);
        }
        finally { window.Close(); }
    }
    [AvaloniaTheory]
    [InlineData(ReadingMode.Single)]
    [InlineData(ReadingMode.Continuous)]
    [InlineData(ReadingMode.Double)]
    public async Task FrameReadsExactHitReusesReaderSwitchesCopyAndReturnsToResults(ReadingMode mode)
    {
        using var scope = new Fixture(); scope.CreateCopies(named: true); scope.Services.Settings.ImageSearch.Enabled = true;
        scope.Services.Settings.DefaultReadingMode = mode;
        StartupOptions.Current = StartupOptions.Parse(["--ui-smoke"]);
        using var shell = new ShellViewModel(scope.Services);
        await shell.Library.InitializeAsync();
        shell.Navigate("library");
        var window = new MainWindow(shell);
        try
        {
            window.Show(); window.OpenImageSearch(); Dispatcher.UIThread.RunJobs();
            var page = Assert.IsType<ImageSearchPage>(window.NavigationFrame.Content);
            var vm = Assert.IsType<ImageSearchViewModel>(page.DataContext);
            await vm.BuildIndexCommand.ExecuteAsync(null); Assert.True(vm.HasIndex, vm.StatusText);
            var copies = shell.Library.SnapshotAllWorks().SelectMany(item => item.Versions).ToArray();
            Assert.Equal(2, copies.Length);
            await vm.LoadFileAsync(Path.Combine(copies[0].FolderPath, "00000005.png")); await vm.SearchCommand.ExecuteAsync(null);
            var first = vm.Results.First(r => r.Hit.Gallery.SourceId == copies[0].SourceId && r.Hit.PageNumber == 5);
            vm.ReadHitCommand.Execute(first);
            await WaitForAsync(() => window.ActiveReader?.ViewModel.Source.StableId == first.Hit.Gallery.StableId);
            var reader = Assert.IsType<ReaderPage>(window.ActiveReader);
            await Task.Delay(150); Dispatcher.UIThread.RunJobs();
            Assert.Equal("原始第 5 页", reader.ViewModel.OriginalPageLabel);
            window.NavigationFrame.GoBack(); Assert.Same(page, window.NavigationFrame.Content);
            Assert.True(vm.HasResults);
            vm.ReadHitCommand.Execute(first);
            Assert.Same(reader, window.ActiveReader);
            window.NavigationFrame.GoBack();
            var other = vm.Results.First(r => r.Hit.Gallery.SourceId == copies[1].SourceId && r.Hit.PageNumber == 5);
            vm.ReadHitCommand.Execute(other);
            await WaitForAsync(() => window.ActiveReader?.ViewModel.Source.StableId == other.Hit.Gallery.StableId);
            Assert.NotSame(reader, window.ActiveReader);
            Assert.Equal(other.Hit.Gallery.SourceId, Assert.IsType<LocalReaderSource>(window.ActiveReader!.ViewModel.Source).Gallery.SourceId);
            Assert.Empty(scope.Services.Settings.PreferredSourceByWork);
            await Task.Delay(150); Dispatcher.UIThread.RunJobs();
            Assert.Equal("原始第 5 页", window.ActiveReader.ViewModel.OriginalPageLabel);
            window.NavigationFrame.GoBack(); Assert.Same(page, window.NavigationFrame.Content);
            Assert.True(vm.HasResults);
        }
        finally { window.Close(); }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        Assert.True(condition(), "The headless navigation did not finish.");
    }
    [AvaloniaFact]
    public async Task DisabledFeatureAndDraftSettingsNeverCreateAnIndex()
    {
        using var scope = new Fixture();
        Assert.False(scope.Services.Settings.ImageSearch.Enabled);
        var state = await scope.Services.ImageSearch.GetStatusAsync();
        Assert.Contains("尚未启用", state.Message);
        Assert.False(Directory.Exists(scope.Services.ImageSearch.DirectoryPath));
        using var settings = new SettingsViewModel(scope.Services);
        settings.ImageSearchEnabled = true; settings.ImageSearchProbeIndex = 2;
        Assert.False(scope.Services.Settings.ImageSearch.Enabled);
        Assert.Equal(16, scope.Services.Settings.ImageSearch.NProbe);
        settings.Apply();
        Assert.True(scope.Services.Settings.ImageSearch.Enabled); Assert.Equal(64, scope.Services.Settings.ImageSearch.NProbe);
        Assert.False(Directory.Exists(scope.Services.ImageSearch.DirectoryPath));
    }

    [AvaloniaFact]
    public async Task RealServiceBuildMapsDuplicateCopiesAndFiltersDisabledOrChangedSources()
    {
        using var scope = new Fixture();
        GalleryInfo[] copies = scope.CreateCopies(); scope.Services.Settings.ImageSearch.Enabled = true;
        var snapshot = copies.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray();
        var result = await scope.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None);
        Assert.Equal(6, result.Indexed);
        using (var lockedPage = new FileStream(Path.Combine(copies[0].FolderPath, "00000001.png"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var update = await scope.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None);
            Assert.Equal(6, update.Indexed); Assert.Equal(6, update.Reused);
        }
        await scope.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None);
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(scope.Services.ImageSearch.DirectoryPath, "generations")).Length);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Services.ImageSearch.BuildAsync([], null, CancellationToken.None));
        Assert.Equal(6, (await scope.Services.ImageSearch.GetStatusAsync()).Indexed);
        await File.WriteAllTextAsync(Path.Combine(scope.Services.ImageSearch.DirectoryPath, "current.json"), "{broken");
        Assert.Equal(6, (await scope.Services.ImageSearch.GetStatusAsync()).Indexed); // Recover the independently validated prior generation.
        byte[] query = File.ReadAllBytes(Path.Combine(copies[0].FolderPath, "00000005.png"));
        var hits = await scope.Services.ImageSearch.SearchAsync(query, null, copies, CancellationToken.None);
        Assert.Contains(hits, h => h.Gallery.SourceId == copies[0].SourceId && h.PageNumber == 5);
        Assert.Contains(hits, h => h.Gallery.SourceId == copies[1].SourceId && h.PageNumber == 5);
        var saved = hits.First(h => h.Gallery.SourceId == copies[0].SourceId && h.PageNumber == 5);
        scope.Services.Settings.Sources[1].Enabled = false;
        hits = await scope.Services.ImageSearch.SearchAsync(query, null, copies, CancellationToken.None);
        Assert.DoesNotContain(hits, h => h.Gallery.SourceId == copies[1].SourceId);
        File.WriteAllBytes(saved.FilePath, SyntheticImages.Create(999).EncodePng());
        Assert.False(scope.Services.ImageSearch.CanRead(saved));
        hits = await scope.Services.ImageSearch.SearchAsync(query, null, copies, CancellationToken.None);
        Assert.DoesNotContain(hits, h => h.Gallery.SourceId == copies[0].SourceId && h.PageNumber == 5);
    }

    [AvaloniaFact]
    public void ExplicitPageOverridesCompletedHistoryAndRepeatedPageResetsOffsets()
    {
        using var scope = new Fixture(); var gallery = scope.CreateCopies()[0];
        scope.Services.Progress.Set(gallery.WorkId, new ReadingProgress { PageNumber = 3, PageIndex = 1, PageOffset = .7, Completed = true, PageCountAtSave = 5 });
        using var reader = new ReaderViewModel(scope.Services, gallery, initialPageNumber: 5);
        Assert.Equal("原始第 5 页", reader.OriginalPageLabel);
        Assert.False(reader.ShowRestoreHint);
        Assert.Equal(0, reader.PageOffsetForTest);
        Assert.True(reader.TryNavigateToPageNumber(3));
        Assert.False(reader.TryNavigateToPageNumber(2));
        Assert.Equal("原始第 3 页", reader.OriginalPageLabel);
        Assert.True(reader.TryNavigateToPageNumber(3)); Assert.Equal(0, reader.PageOffsetForTest);
        Assert.Throws<FileNotFoundException>(() => new ReaderViewModel(scope.Services, gallery, initialPageNumber: 2));
    }

    [AvaloniaFact]
    public async Task SearchPageUsesRealIndexAndRaisesExactCopyReadRequest()
    {
        using var scope = new Fixture(); var galleries = scope.CreateCopies(); scope.Services.Settings.ImageSearch.Enabled = true;
        using var vm = new ImageSearchViewModel(scope.Services, () => galleries);
        await vm.BuildIndexCommand.ExecuteAsync(null);
        Assert.True(vm.HasIndex, vm.StatusText);
        await vm.LoadFileAsync(Path.Combine(galleries[0].FolderPath, "00000005.png"));
        Assert.True(vm.CanSearch);
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.True(vm.HasResults, vm.StatusText);
        using (var page = new ImageSearchPage(vm))
        {
            var window = new Window { Width = 1200, Height = 800, Content = page };
            try
            {
                window.Show(); Dispatcher.UIThread.RunJobs(); await Task.Delay(150); Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.qa/image-search/ui")); Directory.CreateDirectory(output);
                frame.Save(Path.Combine(output, "search-results-1200.png"), PngBitmapEncoderOptions.Default);
            }
            finally { window.Close(); }
        }
        LocalPageReadRequest? requested = null; vm.ReadRequested += (_, request) => requested = request;
        var hit = vm.Results.First(r => r.Hit.PageNumber == 5 && r.Hit.Gallery.SourceId == galleries[1].SourceId);
        vm.ReadHitCommand.Execute(hit);
        Assert.NotNull(requested); Assert.Same(galleries[1], requested.Gallery); Assert.Equal(5, requested.PageNumber);
    }

    [AvaloniaTheory]
    [InlineData(1200, 800, false)]
    [InlineData(540, 700, false)]
    [InlineData(1200, 800, true)]
    [InlineData(540, 700, true)]
    public async Task SearchDocumentRendersOffscreenWithReachableControls(int width, int height, bool light)
    {
        using var scope = new Fixture();
        Application.Current!.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        using var vm = new ImageSearchViewModel(scope.Services, () => []);
        using var page = new ImageSearchPage(vm);
        var window = new Window { Width = width, Height = height, Content = page };
        try
        {
            window.Show(); // Headless implementation: no native window or focus.
            await vm.RefreshAsync(); Dispatcher.UIThread.RunJobs();
            Assert.NotNull(page.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "ImageSearch.OpenFile"));
            Assert.Contains("尚未启用", vm.StatusText);
            Assert.False(vm.CanBuild); Assert.False(vm.CanSearch);
            window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.qa/image-search/ui"));
            Directory.CreateDirectory(output);
            frame.Save(Path.Combine(output, $"search-{width}-{(light ? "light" : "dark")}.png"), PngBitmapEncoderOptions.Default);
        }
        finally { window.Close(); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "EhSearchUI-" + Guid.NewGuid().ToString("N"));
        internal AppServices Services { get; }
        internal Fixture()
        {
            var paths = new AppPaths(Path.Combine(_root, "data")); paths.EnsureCreated();
            var settings = new AppSettings(); settings.Download.RootPath = Path.Combine(_root, "downloads"); settings.Download.AddAsSource = false; settings.Download.AutoStart = false;
            settings.Network.ProxyMode = ProxyMode.None;
            settings.Network.HostsOverride = "127.0.0.1 e-hentai.org\n127.0.0.1 exhentai.org";
            new SettingsStore(paths).Save(settings, out _); Services = AppServices.Initialize(paths);
        }
        internal GalleryInfo[] CreateCopies(bool named = false)
        {
            var copies = new List<GalleryInfo>();
            for (int i = 0; i < 2; i++)
            {
                string root = Path.Combine(_root, "source-" + i), folder = Path.Combine(root, named ? "42-合成测试" : "gallery"); Directory.CreateDirectory(folder);
                var source = Services.Settings.AddSource(root, "来源 " + i);
                foreach (int page in new[] { 1, 3, 5 }) File.WriteAllBytes(Path.Combine(folder, $"{page:D8}.png"), SyntheticImages.Create(42 + page, 200, 280).EncodePng());
                copies.Add(new GalleryInfo { StableId = "copy-" + i, SourceId = source.Id, SourceName = source.DisplayName, WorkId = "no-gid-work", Title = "合成测试页图", FolderName = "gallery", FolderPath = folder, PageCount = 3, ExpectedPages = 5 });
            }
            return copies.ToArray();
        }
        public void Dispose() { Services.Dispose(); Directory.Delete(_root, true); }
    }
}

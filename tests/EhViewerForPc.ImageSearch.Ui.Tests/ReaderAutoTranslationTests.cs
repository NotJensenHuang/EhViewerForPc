using System.Net;
using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.Services;
using EhViewerForPc.ViewModels;
using EhViewerForPc.Views;
using SkiaSharp;

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public sealed class ReaderAutoTranslationTests
{
    [AvaloniaTheory]
    [InlineData(ReadingMode.Single)]
    [InlineData(ReadingMode.Double)]
    [InlineData(ReadingMode.Continuous)]
    public async Task ToggleUsesTheVisibleDecodedPageAndKeepsTheTranslationPanelClosed(ReadingMode mode)
    {
        using var fixture = new Fixture();
        fixture.Services.Settings.DefaultReadingMode = mode;
        using var page = fixture.CreateReader();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var window = new Window { Width = 540, Height = 420, Content = page };
        try
        {
            window.Show();
            var toggle = page.FindControl<ToggleButton>("AutoTranslationToggle")!;
            Assert.False(toggle.IsChecked);
            Assert.False(fixture.Requests.Reader.TryRead(out _));
            toggle.IsChecked = true;
            var first = await fixture.Requests.Reader.ReadAsync(deadline.Token);
            Assert.Equal("00000001.png", Path.GetFileName(first.Image.Path));
            first.Completion.SetResult(Response("第一页译文"));
            await WaitForAsync(() => page.ViewModel.ActiveTranslationOverlay is not null, deadline.Token);
            Assert.False(page.ViewModel.IsTranslationOpen);
            Assert.Contains("第一页译文", page.ViewModel.TranslationText);
            Assert.True(page.ViewModel.ActiveTranslationOverlay!.Matches(first.Image));
            page.ViewModel.ToggleTranslationImageCommand.Execute(null);
            Assert.Null(page.ViewModel.ActiveTranslationOverlay);
            page.ViewModel.OpenTranslationTextCommand.Execute(null);
            Assert.True(page.ViewModel.IsTranslationOpen);
            page.ViewModel.CloseTranslationPanel();

            Assert.True(page.ViewModel.TryNavigateToPageNumber(2));
            var second = await fixture.Requests.Reader.ReadAsync(deadline.Token);
            Assert.Equal("00000002.png", Path.GetFileName(second.Image.Path));
            Assert.False(page.ViewModel.IsTranslationOpen);
            toggle.IsChecked = false;
            Assert.True(second.Token.IsCancellationRequested);
            second.Completion.SetResult(Response("关闭开关后迟到的译文"));
            await WaitForAsync(() => !page.ViewModel.IsTranslationRunning, deadline.Token);
            Assert.Null(page.ViewModel.ActiveTranslationOverlay);
            Assert.Empty(page.ViewModel.TranslationText);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FastTurnsAndPreferenceChangesCancelPriorWorkWithoutPublishingLateText()
    {
        using var fixture = new Fixture();
        fixture.Services.Settings.DefaultReadingMode = ReadingMode.Single;
        fixture.Services.SaveReaderAutoTranslation(true);
        using var page = fixture.CreateReader();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var window = new Window { Width = 740, Height = 600, Content = page };
        try
        {
            window.Show();
            var first = await fixture.Requests.Reader.ReadAsync(deadline.Token);
            Assert.True(page.ViewModel.TryNavigateToPageNumber(2));
            Assert.True(page.ViewModel.TryNavigateToPageNumber(3));
            Assert.True(first.Token.IsCancellationRequested);
            var current = await fixture.Requests.Reader.ReadAsync(deadline.Token);
            Assert.Equal("00000003.png", Path.GetFileName(current.Image.Path));
            first.Completion.SetResult(Response("上一页迟到"));
            Assert.Empty(page.ViewModel.TranslationText);

            fixture.Services.SaveTranslationLanguage(MangaTranslationLanguageOption.Resolve("en"));
            Assert.True(current.Token.IsCancellationRequested);
            var changed = await fixture.Requests.Reader.ReadAsync(deadline.Token);
            Assert.Equal(current.Image, changed.Image);
            current.Completion.SetResult(Response("旧配置迟到"));
            changed.Completion.SetResult(Response("Current page", "en"));
            await WaitForAsync(() => page.ViewModel.ActiveTranslationOverlay is not null, deadline.Token);
            Assert.Contains("Current page", page.ViewModel.TranslationText);
            Assert.DoesNotContain("迟到", page.ViewModel.TranslationText);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task OnlinePageStartsAutomaticTranslationOnlyAfterTheImageIsDecoded()
    {
        using var fixture = new Fixture();
        fixture.Services.Settings.DefaultReadingMode = ReadingMode.Single;
        fixture.Services.SaveReaderAutoTranslation(true);
        using var page = fixture.CreateOnlineReader();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var window = new Window { Width = 740, Height = 600, Content = page };
        try
        {
            window.Show();
            await fixture.Online.ImageStarted.Task.WaitAsync(deadline.Token);
            await Task.Delay(500, deadline.Token);
            Assert.False(fixture.Requests.Reader.TryRead(out _));
            fixture.Online.ReleaseImage.TrySetResult();
            var request = await fixture.Requests.Reader.ReadAsync(deadline.Token);
            Assert.True(File.Exists(request.Image.Path));
            Assert.True(Path.IsPathFullyQualified(request.Image.Path));
            request.Completion.SetResult(Response("在线页译文"));
            await WaitForAsync(() => page.ViewModel.ActiveTranslationOverlay is not null, deadline.Token);
            Assert.True(page.ViewModel.ActiveTranslationOverlay!.Matches(request.Image));
        }
        finally
        {
            fixture.Online.ReleaseImage.TrySetResult();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SavedTogglePersistsAndIsSharedByReaderInstances()
    {
        using var fixture = new Fixture();
        using var first = fixture.CreateReader();
        using var second = fixture.CreateReader();
        Assert.False(first.ViewModel.AutoTranslateReaderPages);
        first.ViewModel.AutoTranslateReaderPages = true;
        Assert.True(second.ViewModel.AutoTranslateReaderPages);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await WaitForAsync(() => fixture.Services.SettingsStore.Load(out _).AutoTranslateReaderPages, deadline.Token);
        second.ViewModel.AutoTranslateReaderPages = false;
        Assert.False(first.ViewModel.AutoTranslateReaderPages);
        await WaitForAsync(() => !fixture.Services.SettingsStore.Load(out _).AutoTranslateReaderPages, deadline.Token);
    }

    [AvaloniaFact]
    public async Task OcrReviewOnlyPageHasSelectableResultAndNoImageOverlay()
    {
        using var fixture = new Fixture();
        fixture.Services.Settings.DefaultReadingMode = ReadingMode.Single;
        fixture.Services.SaveReaderAutoTranslation(true);
        using var page = fixture.CreateReader();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var window = new Window { Width = 740, Height = 600, Content = page };
        try
        {
            window.Show();
            var request = await fixture.Requests.Reader.ReadAsync(deadline.Token);
            request.Completion.SetResult(new MangaTranslationResponse(1, 160, 240, MangaOcrOrt.Provider, null, [])
            {
                OcrObservations =
                [
                    new MangaOcrObservation(1, [new(5, 6), new(25, 6), new(25, 40), new(5, 40)],
                        0.90f, "サ", 0.21f, "ザ", "ザ", 0.33f,
                        MangaOcrDirection.Vertical, MangaOcrRegionType.Unclassified, true),
                ],
            });

            await WaitForAsync(() => page.ViewModel.HasTranslationText, deadline.Token);
            Assert.Null(page.ViewModel.ActiveTranslationOverlay);
            Assert.False(page.ViewModel.IsTranslationOpen);
            page.ViewModel.OpenTranslationTextCommand.Execute(null);
            Assert.True(page.ViewModel.IsTranslationOpen);
            Assert.Contains("待核对 #1", page.ViewModel.TranslationText);
            Assert.Contains("首次识别 サ", page.ViewModel.TranslationText);
            Assert.Contains("ザ", page.ViewModel.TranslationText);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ExplicitSceneRetryDoesNotLeakToOtherPagesOrCpuModels()
    {
        using var fixture = new Fixture();
        fixture.Services.SaveTranslationModel(MangaTranslationModelOption.Find("qwen3-4b-context")!);
        using var page = fixture.CreateReader();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var image = new ReaderTranslationImage("page.png", 10, DateTime.UnixEpoch);

        page.ViewModel.TranslationScene = "  场景在车站  ";
        var first = page.ViewModel.RetryTranslationWithSceneAsync(image);
        Request retry = await fixture.Requests.Reader.ReadAsync(timeout.Token);
        Assert.Equal(1, retry.Context.Page);
        Assert.Equal("场景在车站", retry.Context.Scene);
        var scoped = Response("译文");
        scoped.Regions[0] = scoped.Regions[0] with
        {
            ContextSources = MangaTranslationContextSources.Scene | MangaTranslationContextSources.PreviousPage,
        };
        retry.Completion.SetResult(scoped);
        await first.WaitAsync(timeout.Token);
        Assert.NotNull(page.ViewModel.ActiveTranslationOverlay);
        Assert.Contains("上一相邻页对白、本页手填场景", page.ViewModel.TranslationText);
        Assert.DoesNotContain("场景在车站", page.ViewModel.TranslationText);

        page.ViewModel.TranslationScene = "";
        Assert.True(page.ViewModel.CanRetryWithTranslationScene);
        var cleared = page.ViewModel.RetryTranslationWithSceneAsync(image);
        Request repeat = await fixture.Requests.Reader.ReadAsync(timeout.Token);
        Assert.Null(repeat.Context.Scene);
        Assert.Equal(retry.Context.Reader, repeat.Context.Reader);
        repeat.Completion.SetResult(Response("新译文"));
        await cleared.WaitAsync(timeout.Token);

        page.ViewModel.TranslationScene = "另一份场景说明";
        page.ViewModel.NextPageCommand.Execute(null);
        Assert.Empty(page.ViewModel.TranslationScene);
        var next = page.ViewModel.TranslateCurrentPageAsync(image);
        Request second = await fixture.Requests.Reader.ReadAsync(timeout.Token);
        Assert.Equal(2, second.Context.Page);
        Assert.Null(second.Context.Scene);
        second.Completion.SetResult(Response("第二页"));
        await next.WaitAsync(timeout.Token);

        fixture.Services.SaveTranslationModel(MangaTranslationModelOption.Find(MangaTranslationModelOption.JMangaId)!);
        Assert.False(page.ViewModel.SupportsTranslationScene);
        page.ViewModel.TranslationScene = "不会送给单气泡模型";
        Assert.False(page.ViewModel.CanRetryWithTranslationScene);
    }

    private static async Task WaitForAsync(Func<bool> predicate, CancellationToken token)
    {
        while (!predicate()) await Task.Delay(10, token);
    }

    private static MangaTranslationResponse Response(string text, string language = "zh-CN") =>
        new(1, 160, 240, "fixture", "fixture", [new("原文", text, 8, 8, 80, 72)]) { TargetLanguage = language };

    private sealed record Request(ReaderTranslationImage Image, MangaTranslationPageContext Context, CancellationToken Token,
        TaskCompletionSource<MangaTranslationResponse> Completion);

    private sealed class Fixture : IDisposable
    {
        private readonly string _gallery;

        public Fixture()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(root.FullName, "Directory.Packages.props")))
                root = root.Parent ?? throw new InvalidOperationException("无法定位工作区。");
            string directory = Path.Combine(root.FullName, ".qa", "reader-auto-translation-tests", Guid.NewGuid().ToString("N"));
            _gallery = Directory.CreateDirectory(Path.Combine(directory, "gallery")).FullName;
            Services = AppServices.Initialize(new AppPaths(Path.Combine(directory, "data")),
                settings => new EhClient(settings, new HttpClient(Online), new HttpClient(Online)));
            using var bitmap = new SKBitmap(160, 240);
            bitmap.Erase(SKColors.Orange);
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            Online.Image = png.ToArray();
            for (int page = 1; page <= 3; page++)
                File.WriteAllBytes(Path.Combine(_gallery, $"{page:D8}.png"), Online.Image);
        }

        public AppServices Services { get; }
        public DelayedImageHandler Online { get; } = new();
        public Channel<Request> Requests { get; } = Channel.CreateUnbounded<Request>();

        public ReaderPage CreateReader()
        {
            var gallery = new GalleryInfo
            {
                StableId = "translation-fixture", WorkId = "translation-fixture", Title = "漫画测试",
                FolderName = "gallery", FolderPath = _gallery, PageCount = 3, MaxPageNumber = 3,
            };
            var source = new LocalReaderSource(Services, gallery);
            return new ReaderPage(new ReaderViewModel(Services, source, 1, TranslateAsync, static () => true));
        }

        public ReaderPage CreateOnlineReader()
        {
            var details = new EhGalleryDetails { Gid = 42, Token = "0123456789", Title = "在线样本", Pages = 1 };
            var index = OnlinePageIndex.ForClient(Services.Online, details.Gid, details.Token, details.Pages);
            var source = new OnlineReaderSource(Services, details, index);
            return new ReaderPage(new ReaderViewModel(Services, source, 1, TranslateAsync, static () => true));
        }

        private Task<MangaTranslationResponse> TranslateAsync(ReaderTranslationImage image, MangaTranslationPageContext context, CancellationToken token)
        {
            var completion = new TaskCompletionSource<MangaTranslationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Requests.Writer.TryWrite(new Request(image, context, token, completion))) throw new InvalidOperationException();
            return completion.Task;
        }

        public void Dispose() => Services.Dispose();
    }

    private sealed class DelayedImageHandler : HttpMessageHandler
    {
        public byte[] Image { get; set; } = [];
        public TaskCompletionSource ImageStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseImage { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Uri uri = request.RequestUri!;
            if (uri.Host == "images.example")
            {
                ImageStarted.TrySetResult();
                await ReleaseImage.Task.WaitAsync(token);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Image) };
            }

            string html = uri.AbsolutePath.StartsWith("/s/", StringComparison.Ordinal)
                ? "<img id='img' src='https://images.example/1.png'>"
                : "<div id='gdt'><a href='https://e-hentai.org/s/1111111111/42-1'><img></a></div>";
            return new(HttpStatusCode.OK) { Content = new StringContent(html) };
        }
    }
}

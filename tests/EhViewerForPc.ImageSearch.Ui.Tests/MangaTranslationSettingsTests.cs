using Avalonia.Headless.XUnit;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.Services;
using EhViewerForPc.ViewModels;
using Xunit;

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public sealed class MangaTranslationSettingsTests
{
    private static readonly string LayoutRoot = Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, "translation-layout");

    [Theory]
    [InlineData("Models")]
    [InlineData("ComicsModels")]
    public void ModelDirectoryCanShareTheSameDiskAndParent(string name)
    {
        MangaTranslationSettingsViewModel.EnsureOutsideLibrary(Path.Combine(LayoutRoot, name),
            [Path.Combine(LayoutRoot, "Comics"), Path.Combine(LayoutRoot, "Downloads")]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Comics")]
    [InlineData("Comics/models")]
    [InlineData("Downloads/models")]
    public void ModelDirectoryMustBeSeparateFromLibraryAndDownloads(string name)
    {
        Assert.Throws<MangaTranslationException>(() => MangaTranslationSettingsViewModel.EnsureOutsideLibrary(
            Path.Combine(LayoutRoot, name), [Path.Combine(LayoutRoot, "Comics"), Path.Combine(LayoutRoot, "Downloads")]));
    }

    [AvaloniaFact]
    public async Task RetiredModelStaysInSettingsButCannotBeSelectedOrInstalled()
    {
        using var fixture = new SettingsFixture();
        fixture.Services.Settings.MangaTranslationModelId = "hy-mt2-7b";
        using var viewModel = new MangaTranslationSettingsViewModel(fixture.Services);
        Assert.Null(viewModel.SelectedModel);
        Assert.Contains(viewModel.Models, model => model.Id == "hy-mt2-manga-zh-hans");
        Assert.DoesNotContain(viewModel.Models, model => model.Id is "hy-mt2-1.8b" or "hy-mt2-7b");
        Assert.False(viewModel.CanInstall);
        await viewModel.CheckResourcesAsync();
        Assert.Contains("选择", viewModel.OperationStatus);
        Assert.False(Directory.Exists(viewModel.ModelDirectory));
        await viewModel.InstallCommand.ExecuteAsync(null);
        Assert.False(Directory.Exists(viewModel.ModelDirectory));
        var unavailable = await Assert.ThrowsAsync<MangaTranslationException>(() =>
            fixture.Services.MangaTranslation.TranslateAsync(default, TestContext.Current.CancellationToken));
        Assert.True(unavailable.RequiresSetup);
        Assert.Equal(MangaTranslationBackendService.NoTranslationModelMessage, unavailable.Message);
        Assert.Equal("zh-CN", viewModel.SelectedTargetLanguage.Id);

        int changes = 0;
        fixture.Services.TranslationPreferencesChanged += (_, _) => changes++;
        viewModel.SelectedTargetLanguage = viewModel.TargetLanguages.Single(language => language.Id == "en");
        var snapshot = fixture.Services.GetMangaTranslationPreferences();
        using var reopened = new MangaTranslationSettingsViewModel(fixture.Services);
        Assert.Equal(snapshot.Model, reopened.SelectedModel);
        Assert.Equal(snapshot.Language, reopened.SelectedTargetLanguage);
        Assert.Equal(1, changes);
        Assert.Equal("hy-mt2-7b", fixture.Services.Settings.MangaTranslationModelId);
        Assert.Equal("en", fixture.Services.Settings.MangaTranslationTargetLanguage);
    }

    [AvaloniaFact]
    public async Task ReportsTheCurrentDownloadAndWaitsForAsyncCancellationBeforeEnablingRetry()
    {
        using var fixture = new SettingsFixture();
        using var finishCancellation = new ManualResetEventSlim();
        var cancelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new MangaTranslationSettingsViewModel(fixture.Services, async (_, reporter, token) =>
        {
            using var registration = token.Register(() =>
            {
                cancelStarted.TrySetResult();
                finishCancellation.Wait(TimeSpan.FromSeconds(4));
            });
            reporter.Report(new MangaTranslationInstallProgress("正在下载 rec.onnx（1 MB / 16 MB）", .25,
                MangaTranslationInstallStage.Downloading, "rec.onnx", 1_000_000, 16_000_000));
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        }, TestModel);
        viewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(viewModel.OperationStatus) && viewModel.OperationStatus.Contains("rec.onnx"))
                progressShown.TrySetResult();
        };

        Task installing = viewModel.InstallCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsProgressIndeterminate);
        Assert.True(viewModel.CanCancel);
        await progressShown.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(.25, viewModel.Progress);
        Assert.Contains("1 MB / 16 MB", viewModel.OperationStatus);

        try
        {
            viewModel.CancelInstallCommand.Execute(null);
            await cancelStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.False(installing.IsCompleted);
            Assert.False(viewModel.CanInstall);
            Assert.False(viewModel.CanCancel);
            Assert.Contains("正在取消", viewModel.OperationStatus);
        }
        finally { finishCancellation.Set(); }
        await installing.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.True(viewModel.CanInstall);
        Assert.Equal("重试准备模型", viewModel.InstallLabel);
        Assert.Contains("已校验完整的模型会保留", viewModel.OperationStatus);
    }

    [AvaloniaFact]
    public async Task DownloadFailureShowsTheReasonAndAllowsRetry()
    {
        using var fixture = new SettingsFixture();
        using var viewModel = new MangaTranslationSettingsViewModel(fixture.Services, static (_, _, _) =>
            Task.FromException(new MangaTranslationException("下载 det.onnx 时连接超时，请重试。")), TestModel);

        await viewModel.InstallCommand.ExecuteAsync(null);

        Assert.True(viewModel.CanInstall);
        Assert.False(viewModel.CanCancel);
        Assert.Equal("重试准备模型", viewModel.InstallLabel);
        Assert.Contains("det.onnx", viewModel.OperationStatus);
    }

    private static MangaTranslationModelOption TestModel { get; } = new("test-model", "Test model", "test.gguf", 1);

    private sealed class SettingsFixture : IDisposable
    {
        private readonly string _root;
        public AppServices Services { get; }

        public SettingsFixture()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "EhViewerForPc.slnx")))
                current = current.Parent;
            _root = Path.Combine(current?.FullName ?? Path.GetTempPath(), ".qa", "manga-settings-" + Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(Path.Combine(_root, "data"));
            paths.EnsureCreated();
            var settings = new AppSettings();
            settings.Download.RootPath = Path.Combine(_root, "downloads");
            settings.Download.AutoStart = false;
            settings.Download.AddAsSource = false;
            settings.MangaTranslationModelDirectory = Path.Combine(_root, "models");
            Assert.True(new SettingsStore(paths).Save(settings, out _));
            Services = AppServices.Initialize(paths);
        }

        public void Dispose()
        {
            Services.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}

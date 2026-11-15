using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.Platform;
using EhViewerForPc.Core.Download;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.ImageSearch;
using EhViewerForPc.Services;
using EhViewerForPc.ViewModels;
using EhViewerForPc.Views;

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public sealed class TrayIntegrationTests
{
    [AvaloniaFact]
    public void RegistrationCreatesAndDisposesThePlatformTrayImplementation()
    {
        using var fixture = new ReaderRetryIntegrationTests.Fixture();
        using var shell = new ShellViewModel(fixture.Services);
        var window = new MainWindow(shell);
        using var locator = AvaloniaLocator.EnterScope();
        var platform = new TrayPlatformSpy(AvaloniaLocator.Current.GetRequiredService<IWindowingPlatform>());
        AvaloniaLocator.CurrentMutable.Bind<IWindowingPlatform>().ToConstant(platform);
        using var tray = new DesktopTrayController(window, fixture.Services, () => false);
        try
        {
            Assert.True(tray.Start());
            Assert.Equal(1, platform.Created);
            Assert.NotNull(platform.Tray.Icon);
            Assert.Equal("EhViewer for PC", platform.Tray.ToolTip);
            Assert.True(platform.Tray.Visible);
            tray.Dispose();
            Assert.Equal(1, platform.Tray.DisposeCount);
        }
        finally { tray.Dispose(); window.Close(); }
    }

    private sealed class TrayPlatformSpy(IWindowingPlatform inner) : IWindowingPlatform
    {
        internal int Created;
        internal TrayImplementationSpy Tray { get; } = new();
        public IWindowImpl CreateWindow() => inner.CreateWindow();
        public ITopLevelImpl CreateEmbeddableTopLevel() => inner.CreateEmbeddableTopLevel();
        public IWindowImpl CreateEmbeddableWindow() => inner.CreateEmbeddableWindow();
        public ITrayIconImpl CreateTrayIcon() { Created++; return Tray; }
        public void GetWindowsZOrder(ReadOnlySpan<IWindowImpl> windows, Span<long> zOrder) => inner.GetWindowsZOrder(windows, zOrder);
    }

    private sealed class TrayImplementationSpy : ITrayIconImpl
    {
        internal IWindowIconImpl? Icon;
        internal string? ToolTip;
        internal bool Visible;
        internal int DisposeCount;
        public INativeMenuExporter? MenuExporter => null;
        public Action? OnClicked { get; set; }
        public void SetIcon(IWindowIconImpl? icon) => Icon = icon;
        public void SetToolTipText(string? text) => ToolTip = text;
        public void SetIsVisible(bool visible) => Visible = visible;
        public void Dispose() => DisposeCount++;
    }

    [AvaloniaFact]
    public void TrayIsRegisteredOnTheApplicationAndRemovedOnDispose()
    {
        using var fixture = new ReaderRetryIntegrationTests.Fixture();
        using var shell = new ShellViewModel(fixture.Services);
        var window = new MainWindow(shell);
        using var tray = new DesktopTrayController(window, fixture.Services, () => false);
        try
        {
            Assert.True(tray.Start());
            Assert.Contains(tray.Icon!, TrayIcon.GetIcons(Application.Current!) ?? []);
            var icon = tray.Icon!;
            tray.Dispose();
            Assert.DoesNotContain(icon, TrayIcon.GetIcons(Application.Current!) ?? []);
        }
        finally { tray.Dispose(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosingHidesTheSameReaderAndBackgroundDownloadContinues()
    {
        using var fixture = new ReaderRetryIntegrationTests.Fixture();
        fixture.Handler.BlockImage = true;
        using var shell = new ShellViewModel(fixture.Services);
        using var reader = new ReaderPage(new ReaderViewModel(fixture.Services, fixture.Info, 2));
        var window = new MainWindow(shell);
        using var tray = new DesktopTrayController(window, fixture.Services, () => false);
        try
        {
            window.Show(); window.ShowDiagnosticPage(reader);
            Assert.True(tray.Start());
            var task = fixture.Services.Downloads.EnqueueRepair(new() { Gid = 42, Token = "0123456789", Pages = 4 },
                fixture.Folder, fixture.Services.Settings.Sources.Single().RootPath);
            await fixture.Handler.ImageStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Close();
            Assert.False(window.IsVisible);
            Assert.Same(reader, window.ActiveReader);
            Assert.Equal(DownloadState.Running, task.State);
            fixture.Handler.Release.TrySetResult();
            for (int i = 0; i < 250 && task.State is DownloadState.Pending or DownloadState.Running; i++) await Task.Delay(20);
            Assert.Equal(DownloadState.Completed, task.State);
            for (int i = 0; i < 3; i++)
            {
                tray.Restore();
                Assert.True(window.IsVisible);
                Assert.Same(reader, window.ActiveReader);
                Assert.Equal(2, reader.ViewModel.CurrentPageNumber);
                window.Close();
                Assert.False(window.IsVisible);
            }
            Assert.Equal(
                new[] { "显示窗口", "在线浏览", "收藏", "阅读历史", "下载", "本地库", "设置", "本地库图搜图", "退出" },
                tray.Icon!.Menu!.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).Select(i => i.Header));
        }
        finally { tray.Dispose(); window.Close(); }
    }

    [AvaloniaFact]
    public void TrayMenuBringsTheWindowBackAndSwitchesToEveryPage()
    {
        using var fixture = new ReaderRetryIntegrationTests.Fixture();
        using var shell = new ShellViewModel(fixture.Services);
        var window = new MainWindow(shell);
        using var tray = new DesktopTrayController(window, fixture.Services, () => false);
        try
        {
            window.Show(); Assert.True(tray.Start());
            window.Close();
            Assert.False(window.IsVisible);

            NativeMenuItem[] items = tray.Icon!.Menu!.Items.OfType<NativeMenuItem>()
                .Where(item => item is not NativeMenuItemSeparator).ToArray();
            // 菜单项由导航模型生成：每一项都要既回到前台，又停在对应页面。
            foreach (NavItem nav in shell.NavItems)
            {
                items.Single(item => item.Header == nav.Label).Command!.Execute(null);
                Assert.True(window.IsVisible);
                Assert.Same(nav, shell.SelectedNav);
                window.Close();
                Assert.False(window.IsVisible);
            }

            items.Single(item => item.Header == "本地库图搜图").Command!.Execute(null);
            Assert.True(window.IsVisible);
            Assert.NotNull(window.ActiveImageSearch);
            Assert.Same(window.ActiveImageSearch, window.NavigationFrame.Content);
        }
        finally { tray.Dispose(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosingDoesNotCancelTheApplicationIndexBuild()
    {
        using var fixture = new ReaderRetryIntegrationTests.Fixture();
        fixture.Services.Settings.ImageSearch.Enabled = true;
        fixture.Services.ApplySettings(fixture.Services.Settings);
        using var shell = new ShellViewModel(fixture.Services);
        var window = new MainWindow(shell);
        using var tray = new DesktopTrayController(window, fixture.Services, () => false);
        try
        {
            window.Show(); Assert.True(tray.Start());
            var info = fixture.Info;
            var build = fixture.Services.ImageSearch.BuildAsync([new ImageSearchGallery(info.SourceId, info.StableId, info.WorkId, info.FolderPath)],
                null, CancellationToken.None, options: new IndexBuildOptions { UseGpu = false });
            window.Close();
            Assert.False(window.IsVisible);
            var result = await build;
            Assert.Equal(2, result.Indexed);
            Assert.False(fixture.Services.ImageSearch.IsBuilding);
        }
        finally { tray.Dispose(); window.Close(); }
    }

    [AvaloniaFact]
    public void DisablingBackgroundModeClosesTheWindowInsteadOfHidingIt()
    {
        using var fixture = new ReaderRetryIntegrationTests.Fixture();
        fixture.Services.Settings.CloseToTray = false;
        using var shell = new ShellViewModel(fixture.Services);
        var window = new MainWindow(shell);
        using var tray = new DesktopTrayController(window, fixture.Services, () => false);
        bool closed = false; window.Closed += (_, _) => closed = true;
        try
        {
            window.Show(); Assert.True(tray.Start());
            window.Close();
            Assert.True(closed);
        }
        finally { tray.Dispose(); if (!closed) window.Close(); }
    }

    [AvaloniaFact]
    public void TrayExitBypassesHidingAndCancelledExitRemainsRecoverable()
    {
        using var fixture = new ReaderRetryIntegrationTests.Fixture();
        using var shell = new ShellViewModel(fixture.Services);
        var window = new MainWindow(shell);
        bool accept = false, closed = false;
        using var tray = new DesktopTrayController(window, fixture.Services, () => { if (!accept) return false; window.Close(); return true; });
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show(); Assert.True(tray.Start()); window.Close();
            Assert.False(window.IsVisible);
            var exit = tray.Icon!.Menu!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "退出");
            exit.Command!.Execute(null);
            Assert.False(closed); Assert.True(window.IsVisible);
            window.Close(); Assert.False(window.IsVisible);
            accept = true; exit.Command.Execute(null);
            Assert.True(closed);
        }
        finally { tray.Dispose(); if (!closed) window.Close(); }
    }

    [AvaloniaFact]
    public void ShutdownReasonsBypassTheTrayAndDisposeIsIdempotent()
    {
        using var fixture = new ReaderRetryIntegrationTests.Fixture();
        using var shell = new ShellViewModel(fixture.Services);
        var window = new MainWindow(shell);
        using var tray = new DesktopTrayController(window, fixture.Services, () => false);
        try
        {
            window.Show(); Assert.True(tray.Start());
            Assert.False(tray.ShouldHide(WindowCloseReason.OSShutdown));
            Assert.False(tray.ShouldHide(WindowCloseReason.ApplicationShutdown));
            Assert.True(window.IsVisible);
            tray.Dispose(); tray.Dispose();
            Assert.Null(window.BackgroundCloseHandler);
        }
        finally { tray.Dispose(); window.Close(); }
    }

    [AvaloniaFact]
    public void BackgroundPreferenceDefaultsOnAndSavedEditorPreservesRuntimeChanges()
    {
        using var fixture = new ReaderRetryIntegrationTests.Fixture();
        Assert.True(fixture.Services.Settings.CloseToTray);
        using var editor = new SettingsViewModel(fixture.Services);
        fixture.Services.Settings.CloseToTray = false;
        editor.Apply();
        Assert.False(fixture.Services.Settings.CloseToTray);
        editor.RefreshSources(); Assert.False(editor.CloseToTray);
        editor.CloseToTray = true; editor.Apply();
        Assert.True(fixture.Services.Settings.CloseToTray);
        var store = new SettingsStore(fixture.Services.Paths);
        Assert.True(store.Save(fixture.Services.Settings, out _));
        Assert.True(store.Load(out _).CloseToTray);
        editor.CloseToTray = false; editor.Apply();
        Assert.True(store.Save(fixture.Services.Settings, out _));
        Assert.False(store.Load(out _).CloseToTray);
    }

    [AvaloniaTheory]
    [InlineData(1200, false)]
    [InlineData(920, false)]
    [InlineData(740, false)]
    [InlineData(640, false)]
    [InlineData(540, false)]
    [InlineData(1200, true)]
    [InlineData(920, true)]
    [InlineData(740, true)]
    [InlineData(640, true)]
    [InlineData(540, true)]
    public void SettingsSwitchUsesTheCompiledPreferenceBinding(int width, bool light)
    {
        using var fixture = new ReaderRetryIntegrationTests.Fixture();
        using var editor = new SettingsViewModel(fixture.Services);
        int category = Array.IndexOf(editor.Categories, "界面与阅读");
        Assert.True(category >= 0, "托盘与外观设置必须有可查找的界面分类");
        editor.SelectedCategoryIndex = category;
        Application.Current!.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var page = new SettingsPage { DataContext = editor };
        var window = new Window { Width = width, Height = width == 540 ? 420 : 600, Content = page };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var toggle = page.GetVisualDescendants().OfType<ToggleSwitch>().Single(c => AutomationProperties.GetAutomationId(c) == "Settings.CloseToTray");
            Assert.True(toggle.IsChecked);
            window.UpdateLayout();
            var position = toggle.TranslatePoint(default, window)!.Value;
            Assert.True(position.X >= 0 && position.X + toggle.Bounds.Width <= width);
            toggle.IsChecked = false;
            Assert.False(editor.CloseToTray);
            editor.Apply(); Assert.False(fixture.Services.Settings.CloseToTray);
        }
        finally { window.Close(); }
    }
}

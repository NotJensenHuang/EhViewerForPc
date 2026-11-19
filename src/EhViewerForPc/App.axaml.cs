using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.Services;
using EhViewerForPc.ViewModels;
using EhViewerForPc.Views;

namespace EhViewerForPc;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 托盘模式取消主窗关闭并隐藏；真正关闭主窗或托盘退出才结束应用。
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            StartupOptions options = StartupOptions.Current ?? new StartupOptions();
            options.EnsureIsolatedDataDirectory();
            if (!string.IsNullOrWhiteSpace(options.AnimProbe))
            {
                // Diagnostic mode: measure one animated file, no window at all.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Dispatcher.UIThread.Post(async () =>
                {
                    int code = await AnimationProbe.RunAsync(options.AnimProbe!, options.OutputPath, options.DumpFrames);
                    desktop.Shutdown(code);
                });
                base.OnFrameworkInitializationCompleted();
                return;
            }

            if (options.LoginProbe)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Dispatcher.UIThread.Post(async () =>
                {
                    AppPaths probePaths = string.IsNullOrWhiteSpace(options.DataDirectory)
                        ? new AppPaths()
                        : new AppPaths(options.DataDirectory);
                    probePaths.EnsureCreated();
                    AppServices probeServices = AppServices.Initialize(probePaths);
                    int code = await LoginProbeService.RunAsync(probeServices, options.OutputPath);
                    probeServices.Dispose();
                    desktop.Shutdown(code);
                });
                base.OnFrameworkInitializationCompleted();
                return;
            }

            if (options.OnlineProbe)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Dispatcher.UIThread.Post(async () =>
                {
                    AppPaths probePaths = string.IsNullOrWhiteSpace(options.DataDirectory)
                        ? new AppPaths()
                        : new AppPaths(options.DataDirectory);
                    probePaths.EnsureCreated();
                    AppServices probeServices = AppServices.Initialize(probePaths);
                    int code = await OnlineProbeService.RunAsync(probeServices, options.OutputPath);
                    probeServices.Dispose();
                    desktop.Shutdown(code);
                });
                base.OnFrameworkInitializationCompleted();
                return;
            }

            if (options.SelfTest)
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    int exitCode = await SelfTestService.RunAsync(options);
                    desktop.Shutdown(exitCode);
                });
                base.OnFrameworkInitializationCompleted();
                return;
            }

            AppPaths paths = string.IsNullOrWhiteSpace(options.DataDirectory)
                ? new AppPaths()
                : new AppPaths(options.DataDirectory);

            AppServices services = AppServices.Initialize(paths);
            if (options.UiSmoke)
            {
                services.Settings.Download.RootPath = Path.Combine(Path.GetDirectoryName(paths.BaseDirectory)!, "smoke-downloads-" + Guid.NewGuid().ToString("N"));
                services.Settings.Download.AddAsSource = false;
                services.Settings.Network.ProxyMode = ProxyMode.None;
                services.Settings.Network.HostsOverride = "127.0.0.1 e-hentai.org\n127.0.0.1 exhentai.org";
                services.ReloadNetwork();
            }
            ApplyTheme(services.Settings.Theme);

            var shell = new ShellViewModel(services);
            var window = new MainWindow(shell);
            desktop.MainWindow = window;
            DesktopTrayController? tray = null;
            if (OperatingSystem.IsWindows() && !options.UiSmoke)
            {
                tray = new DesktopTrayController(window, services, () => desktop.TryShutdown());
                tray.Start();
            }

            // 与互斥锁同条件：仅在 Windows 主界面启用；冒烟模式不参与单实例。
            SingleInstanceEndpoint? endpoint = null;
            SingleInstanceProbe? activationProbe = null;
            // 初始化完成前到达的「打开作品」请求先排队，避免命中尚未初始化的资料库视图模型。
            bool libraryReady = false;
            string? pendingGallery = null;
            if (OperatingSystem.IsWindows() && !options.UiSmoke)
            {
                if (!string.IsNullOrWhiteSpace(options.SingleInstanceProbe))
                {
                    activationProbe = new SingleInstanceProbe(options.OutputPath ??
                        Path.Combine(Path.GetTempPath(), "ehviewer-single-instance-probe.json"));
                    endpoint = new SingleInstanceEndpoint(window, static () => false, activationProbe.RecordOpen, services.Log);
                }
                else
                {
                    endpoint = new SingleInstanceEndpoint(window, () => services.Settings.WindowMaximized,
                        id =>
                        {
                            if (string.IsNullOrWhiteSpace(id)) return;
                            if (!libraryReady)
                            {
                                pendingGallery = id;
                                return;
                            }

                            shell.Library.OpenById(id);
                        }, services.Log);
                }
            }

            bool initialized = false;
            window.Opened += async (_, _) =>
            {
                if (initialized) return;
                initialized = true;
                endpoint?.Start();
                if (activationProbe is not null)
                {
                    // 探针只验证激活链路，不初始化资料库，避免对真实环境产生副作用。
                    desktop.Shutdown(await activationProbe.RunAsync(window, endpoint!));
                    return;
                }

                if (!await shell.InitializeAsync(options.Root)) return;
                libraryReady = true;
                if (!string.IsNullOrWhiteSpace(options.Gallery))
                {
                    shell.Library.OpenById(options.Gallery);
                }

                // 命令行作品先打开，初始化期间排队的第二实例请求随后生效（后者是更晚的意图）。
                if (pendingGallery is { } pending)
                {
                    pendingGallery = null;
                    shell.Library.OpenById(pending);
                }

                if (options.UiSmoke)
                {
                    shell.Navigate("library");
                    int smokeCode = options.UiAudit
                        ? await UiAuditService.RunAsync(window, shell, options.OutputPath)
                        : options.NavigationRegression
                        ? await NavigationRegressionService.RunAsync(window, shell, options.OutputPath)
                        : await UiSmokeService.RunAsync(window, shell.Library, options.OutputPath);
                    desktop.Shutdown(smokeCode);
                }
            };

            desktop.Exit += (_, _) =>
            {
                // 先停监听再释放它所引用的窗口与服务，顺序与启动相反。
                try { endpoint?.Dispose(); }
                finally
                {
                    try { tray?.Dispose(); }
                    finally
                    {
                        try { shell.Dispose(); }
                        finally { services.Dispose(); }
                    }
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme(AppTheme theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            AppTheme.Dark => ThemeVariant.Dark,
            AppTheme.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };
    }
}

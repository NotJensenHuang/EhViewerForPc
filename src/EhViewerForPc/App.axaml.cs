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
            if (!string.IsNullOrWhiteSpace(options.Root) && Directory.Exists(options.Root))
            {
                // --root adds (or enables) a source instead of replacing the
                // configured list, so a debug run cannot wipe a user's sources.
                services.Settings.AddSource(Path.GetFullPath(options.Root));
                services.TrySaveSettings(out _);
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

            bool initialized = false;
            window.Opened += async (_, _) =>
            {
                if (initialized) return;
                initialized = true;
                await shell.InitializeAsync();
                if (!string.IsNullOrWhiteSpace(options.Gallery))
                {
                    shell.Library.OpenById(options.Gallery);
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
                try { tray?.Dispose(); }
                finally
                {
                    try { shell.Dispose(); }
                    finally { services.Dispose(); }
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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Storage;
using EhViewerReader.Services;
using EhViewerReader.ViewModels;
using EhViewerReader.Views;

namespace EhViewerReader;

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
            // Reader windows are non-modal; without this the process would stay
            // alive after the library window is closed.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            StartupOptions options = StartupOptions.Current ?? new StartupOptions();
            options.EnsureIsolatedDataDirectory();
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
            if (!string.IsNullOrWhiteSpace(options.Root) && Directory.Exists(options.Root))
            {
                // --root adds (or enables) a source instead of replacing the
                // configured list, so a debug run cannot wipe a user's sources.
                services.Settings.AddSource(Path.GetFullPath(options.Root));
                services.TrySaveSettings(out _);
            }

            ApplyTheme(services.Settings.Theme);

            var viewModel = new MainViewModel(services);
            var window = new MainWindow(viewModel);
            desktop.MainWindow = window;

            window.Opened += async (_, _) =>
            {
                await viewModel.InitializeAsync();
                if (!string.IsNullOrWhiteSpace(options.Gallery))
                {
                    viewModel.OpenById(options.Gallery);
                }

                if (options.UiSmoke)
                {
                    await UiSmokeService.RunAsync(window, viewModel, options.OutputPath);
                    desktop.Shutdown();
                }
            };

            desktop.ShutdownRequested += (_, _) =>
            {
                viewModel.Dispose();
                services.Dispose();
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

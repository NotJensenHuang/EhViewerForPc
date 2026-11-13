using Avalonia;
using EhViewerReader.Services;

namespace EhViewerReader;

internal static class Program
{
    private static Mutex? _instanceMutex;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // Console encoding is best effort (redirected output may reject it).
        }

        StartupOptions options = StartupOptions.Parse(args);
        StartupOptions.Current = options;

        if (!string.IsNullOrWhiteSpace(options.BookmarkProbeRoot))
            return BookmarkTransferProbe.Run(options.BookmarkProbeRoot, options.OutputPath);

        if (!string.IsNullOrWhiteSpace(options.ImageSearchProbeRoot))
            return LocalImageSearchAppProbe.RunAsync(options.ImageSearchProbeRoot, options.OutputPath).GetAwaiter().GetResult();

        if (!string.IsNullOrWhiteSpace(options.ProbeFile))
        {
            return FormatProbe.Run(options.ProbeFile);
        }

        if (options.SelfTest)
        {
            // A running dispatcher loop is required for WriteableBitmap/Skia
            // interop; the test itself creates no visible window.
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        if (!options.UiSmoke)
        {
            _instanceMutex = new Mutex(initiallyOwned: true, OperatingSystem.IsWindows() ? @"Local\EhViewerReader" : "EhViewerReader", out bool createdNew);
            if (!createdNew)
            {
                return 0;
            }
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .LogToTrace();
}

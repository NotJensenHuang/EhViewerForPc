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
            _instanceMutex = new Mutex(initiallyOwned: true, @"Local\EhViewerReader", out bool createdNew);
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
            .UseWin32()
            .UseSkia()
            .WithInterFont()
            .LogToTrace();
}

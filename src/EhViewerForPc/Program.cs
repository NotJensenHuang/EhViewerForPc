using Avalonia;
using EhViewerForPc.Services;

namespace EhViewerForPc;

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

        if (options.SingleInstanceProbeSignal)
        {
            // 探针的第二个进程：只投递请求，退出码区分「已投递」与「无人接收」。
            return SingleInstanceEndpoint.TrySignal(options.Gallery) ? 0 : 5;
        }

        if (options.SelfTest)
        {
            // A running dispatcher loop is required for WriteableBitmap/Skia
            // interop; the test itself creates no visible window.
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        if (!options.UiSmoke)
        {
            // Keeps the pre-rename lock name on purpose: an upgraded build may
            // still share the legacy data folder, so old and new binaries must
            // never write to the same state concurrently.
            _instanceMutex = new Mutex(initiallyOwned: true, OperatingSystem.IsWindows() ? @"Local\EhViewerReader" : "EhViewerReader", out bool createdNew);
            if (!createdNew)
            {
                // 探针要求自己是首实例：被占用时明确失败，避免与「已唤醒」结论混淆。
                if (options.SingleInstanceProbe is not null) return 6;

                // Hand the request to the running instance, then leave. The
                // foreground grant must happen in this process: Windows only
                // lets a freshly launched process grant foreground rights.
                SingleInstanceEndpoint.TrySignal(options.Gallery);
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

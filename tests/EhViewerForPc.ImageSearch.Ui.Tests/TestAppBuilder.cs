using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(EhViewerForPc.ImageSearch.Ui.Tests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public static class TestAppBuilder
{
    // The platform is exclusively in-memory; no Win32 window or focus is created.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().UseHarfBuzz()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

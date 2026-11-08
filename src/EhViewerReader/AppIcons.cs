using Avalonia.Controls;
using Avalonia.Platform;

namespace EhViewerReader;

/// <summary>
/// Window icon shared by every top-level window. The executable icon is set
/// through <c>ApplicationIcon</c>; this resource is what Avalonia draws in the
/// title bar and task switcher. The .ico stream (not a PNG) is required so the
/// Win32 backend can create an HICON at the right size from the frame set
/// instead of downscaling one large bitmap.
/// </summary>
internal static class AppIcons
{
    private static WindowIcon? _window;
    private static bool _attempted;

    public static WindowIcon? Window
    {
        get
        {
            if (!_attempted)
            {
                _attempted = true;
                _window = Load();
            }

            return _window;
        }
    }

    private static WindowIcon? Load()
    {
        try
        {
            using Stream stream = AssetLoader.Open(new Uri("avares://EhViewerReader/Assets/icon.ico"));
            return new WindowIcon(stream);
        }
        catch
        {
            // A missing icon must never prevent the app from starting.
            return null;
        }
    }
}

using System.Diagnostics;

namespace EhViewerReader.Services;

public static class PlatformLauncher
{
    public static void Open(string pathOrUrl)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
        else
        {
            var info = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
            info.ArgumentList.Add(pathOrUrl);
            Process.Start(info);
        }
    }
}

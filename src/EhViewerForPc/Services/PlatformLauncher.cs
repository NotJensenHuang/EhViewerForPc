using System.Diagnostics;

namespace EhViewerForPc.Services;

public static class PlatformLauncher
{
    public static void RevealFile(string path)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Path.GetFullPath(path)}\"") { UseShellExecute = false });
        else
            Open(Path.GetDirectoryName(path)!);
    }

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

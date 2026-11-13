using System.Runtime.InteropServices;

namespace EhViewerForPc.ImageSearch;

internal static class ImmutableFileLinks
{
    // 只用于后续以原子替换发布的不可变文件，禁止对链接目标原地写入。
    internal static bool TryCreate(string source, string destination)
    {
        if (OperatingSystem.IsWindows()) return CreateHardLink(destination, source, IntPtr.Zero);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return Link(source, destination) == 0;
        return false;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string destination, string source, IntPtr reserved);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
}

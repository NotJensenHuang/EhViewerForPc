using System.Runtime.InteropServices;

namespace EhViewerForPc.Services;

/// <summary>仅包围同步磁盘操作，降低当前线程的 I/O 优先级；绝不能跨 await 留在池线程上。</summary>
internal readonly partial struct BackgroundIoScope : IDisposable
{
    private const int BackgroundBegin = 0x10000;
    private const int BackgroundEnd = 0x20000;
    private readonly bool _active;
    private BackgroundIoScope(bool active) => _active = active;
    public static BackgroundIoScope Enter() => new(OperatingSystem.IsWindows() && SetThreadPriority(GetCurrentThread(), BackgroundBegin));
    public void Dispose()
    {
        if (_active) SetThreadPriority(GetCurrentThread(), BackgroundEnd);
    }
    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadPriority(nint thread, int priority);
}

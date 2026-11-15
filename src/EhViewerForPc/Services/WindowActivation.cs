using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace EhViewerForPc.Services;

/// <summary>
/// 把已有窗口带回前台的唯一入口：托盘点击与「第二实例请求激活」共用同一条路径，
/// 避免出现两份「显示窗口」实现。
/// </summary>
internal static partial class WindowActivation
{
    /// <param name="Foreground">是否取得前台焦点。</param>
    /// <param name="Fallback">是否走了降级路径（前台锁拒绝后的置顶切换）。</param>
    internal readonly record struct Result(bool Foreground, bool Fallback);

    /// <summary>
    /// 显示窗口并尝试取得前台焦点。后台进程直接调用 SetForegroundWindow 会被前台锁
    /// 拒绝（只闪任务栏），因此调用方应先用 <see cref="GrantForeground"/> 授权。
    /// </summary>
    internal static Result Restore(Window window, bool maximize)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = maximize ? WindowState.Maximized : WindowState.Normal;
        }

        nint handle = OperatingSystem.IsWindows() ? window.TryGetPlatformHandle()?.Handle ?? 0 : 0;
        if (handle == 0)
        {
            window.Activate();
            return new(window.IsActive, false);
        }

        if (SetForegroundWindow(handle)) return new(true, false);

        // 前台锁拒绝后，置顶切换仍是有效的 z 序变化：窗口会浮到其他窗口之上，
        // 只是拿不到键盘焦点。置顶标志在下一轮调度中清除，避免长期盖住其他应用。
        window.Topmost = true;
        window.Activate();
        Dispatcher.UIThread.Post(() =>
        {
            if (window.Topmost) window.Topmost = false;
        }, DispatcherPriority.Background);

        // 最后一档兜底：只提示不抢焦点，用户仍能看到是哪个窗口在请求注意。
        bool active = window.IsActive;
        if (!active) Flash(handle);
        return new(active, true);
    }

    /// <summary>
    /// 让 <paramref name="processId"/> 获得一次设置前台窗口的许可。由刚被用户点击启动的
    /// 进程调用最可靠；许可只对「下一次」SetForegroundWindow 生效，因此必须先授权再投递请求。
    /// </summary>
    internal static bool GrantForeground(uint processId)
    {
        if (!OperatingSystem.IsWindows() || processId == 0) return false;
        try
        {
            return AllowSetForegroundWindow(processId);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Flash(nint handle)
    {
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = handle,
            Flags = FlashAll,
            Count = 3,
        };
        FlashWindowEx(in info);
    }

    private const uint FlashAll = 0x00000003;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(in FlashInfo info);
}

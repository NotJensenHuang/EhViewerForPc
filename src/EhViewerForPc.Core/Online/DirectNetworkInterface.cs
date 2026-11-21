using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace EhViewerForPc.Core.Online;

internal static class DirectNetworkInterface
{
    private const SocketOptionName UnicastInterface = (SocketOptionName)31;
    private const byte HardwareInterfaceFlag = 1;
    private const uint NoInterfaceStatistics = 2;

    public static void Bind(Socket socket, IPAddress destination)
    {
        if (IPAddress.IsLoopback(destination)) return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        AddressFamily family = destination.AddressFamily;
        bool ipv6 = family == AddressFamily.InterNetworkV6;
        NetworkInterfaceComponent component = ipv6 ? NetworkInterfaceComponent.IPv6 : NetworkInterfaceComponent.IPv4;
        SocketAddress destinationAddress = new IPEndPoint(destination, 0).Serialize();
        var nativeDestination = new SockaddrInet();
        destinationAddress.Buffer.Span[..destinationAddress.Size].CopyTo(AsBytes(ref nativeDestination));

        int selectedIndex = 0;
        byte longestPrefix = 0;
        ulong lowestMetric = ulong.MaxValue;
        SockaddrInet selectedSource = default;
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or
                    NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.Wireless80211) ||
                !adapter.Supports(component))
            {
                continue;
            }

            int index;
            try
            {
                IPInterfaceProperties properties = adapter.GetIPProperties();
                index = (ipv6 ? properties.GetIPv6Properties()?.Index : properties.GetIPv4Properties()?.Index) ?? 0;
            }
            catch (NetworkInformationException)
            {
                // 枚举完成后，网卡可能已离线或不再支持此地址族。
                continue;
            }

            if (index == 0 || (ipv6 && destination.ScopeId != 0 && destination.ScopeId != index))
            {
                continue;
            }

            var adapterRow = new InterfaceRow { Index = index };
            if (GetIfEntry2Ex(NoInterfaceStatistics, ref adapterRow) != 0 ||
                (adapterRow.Flags & HardwareInterfaceFlag) == 0 ||
                GetBestRoute2(IntPtr.Zero, index, IntPtr.Zero, ref nativeDestination, 0, out RouteRow route, out SockaddrInet source) != 0 ||
                route.Index != index || source.Family != (ushort)family)
            {
                continue;
            }

            var ipRow = new IpInterfaceRow { Family = (ushort)family, Index = index };
            if (GetIpInterfaceEntry(ref ipRow) != 0) continue;

            // Windows 按最长前缀和路由、接口度量之和选路。只在物理接口中执行同样的比较。
            ulong metric = (ulong)route.Metric + ipRow.Metric;
            if (route.PrefixLength < longestPrefix ||
                (route.PrefixLength == longestPrefix && (metric > lowestMetric || (metric == lowestMetric && index >= selectedIndex))))
            {
                continue;
            }

            selectedIndex = index;
            longestPrefix = route.PrefixLength;
            lowestMetric = metric;
            selectedSource = source;
        }

        if (selectedIndex == 0)
        {
            throw new SocketException((int)SocketError.NetworkUnreachable);
        }

        // Winsock 的 IPv4 接口编号使用网络字节序，IPv6 使用主机字节序。
        socket.SetSocketOption(
            ipv6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP,
            UnicastInterface,
            ipv6 ? selectedIndex : IPAddress.HostToNetworkOrder(selectedIndex));

        var sourceAddress = new SocketAddress(family, ipv6 ? 28 : 16);
        AsBytes(ref selectedSource)[..sourceAddress.Size].CopyTo(sourceAddress.Buffer.Span);
        socket.Bind(new IPEndPoint(destination, 0).Create(sourceAddress));
    }

    private static Span<byte> AsBytes(ref SockaddrInet address) =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref address, 1));

    // 对应 Windows SDK 的原生布局。保留完整大小供 IP Helper 写入，只声明使用的字段。
    [StructLayout(LayoutKind.Explicit, Size = 1352)]
    private struct InterfaceRow
    {
        [FieldOffset(0)] private ulong _interfaceLuid;
        [FieldOffset(8)] public int Index;
        [FieldOffset(1152)] public byte Flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 104)]
    private struct RouteRow
    {
        [FieldOffset(0)] private ulong _interfaceLuid;
        [FieldOffset(8)] public int Index;
        [FieldOffset(40)] public byte PrefixLength;
        [FieldOffset(84)] public uint Metric;
    }

    [StructLayout(LayoutKind.Explicit, Size = 168)]
    private struct IpInterfaceRow
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(8)] private ulong _interfaceLuid;
        [FieldOffset(16)] public int Index;
        [FieldOffset(148)] public uint Metric;
    }

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    private struct SockaddrInet
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(4)] private uint _alignment;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIfEntry2Ex(uint level, ref InterfaceRow row);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestRoute2(
        IntPtr interfaceLuid,
        int interfaceIndex,
        IntPtr sourceAddress,
        ref SockaddrInet destinationAddress,
        uint addressSortOptions,
        out RouteRow bestRoute,
        out SockaddrInet bestSourceAddress);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIpInterfaceEntry(ref IpInterfaceRow row);
}

using System.Net;
using System.Net.NetworkInformation;

namespace EhViewerForPc.Core.Online;

internal static class VpnConnection
{
    private const NetworkInterfaceType ProprietaryVirtual = (NetworkInterfaceType)53; // IF_TYPE_PROP_VIRTUAL
    private static readonly IPNetwork LinkLocalIpv4 = IPNetwork.Parse("169.254.0.0/16");
    private static readonly object Gate = new();
    private static long _checkedUntil = long.MinValue;
    private static bool _useSystemNetwork;

    public static bool UseSystemNetwork()
    {
        if (!OperatingSystem.IsWindows()) return true;

        // 网页和图片共用短时快照。只在有请求时检测，也能覆盖没有通知的 TUN 切换。
        lock (Gate)
        {
            if (Environment.TickCount64 < _checkedUntil) return _useSystemNetwork;
            try
            {
                _useSystemNetwork = HasActiveTunnel();
            }
            catch (NetworkInformationException)
            {
                _useSystemNetwork = true;
            }

            _checkedUntil = Environment.TickCount64 + 1000;
            return _useSystemNetwork;
        }
    }

    private static bool HasActiveTunnel()
    {
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                !IsTunnel(adapter.NetworkInterfaceType, adapter.Description))
            {
                continue;
            }

            foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (IsUsableAddress(address.Address)) return true;
            }
        }

        return false;
    }

    internal static bool IsTunnel(NetworkInterfaceType type, string description)
    {
        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        // Windows 内置 VPN 可以表现为 PPP 或隧道。排除 PPPoE 与常见的 IPv6 过渡接口。
        if (type == NetworkInterfaceType.Ppp)
            return !description.Contains("PPPoE", comparison);
        if (type == NetworkInterfaceType.Tunnel)
            return !description.Contains("Teredo", comparison) && !description.Contains("ISATAP", comparison) &&
                !description.Contains("6to4", comparison) && !description.Contains("IP-HTTPS", comparison);

        // Wintun 常用「名称 + Tunnel」作为驱动描述，例如 Clash 的 Meta Tunnel。
        return (type == ProprietaryVirtual && description.EndsWith(" Tunnel", comparison)) ||
            description.Contains("Wintun", comparison) || description.Contains("WireGuard", comparison) ||
            description.Contains("OpenVPN", comparison) || description.Contains("TAP-Windows", comparison) ||
            description.Contains("Tailscale", comparison);
    }

    internal static bool IsUsableAddress(IPAddress address) =>
        !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) &&
        !address.IsIPv6LinkLocal && !LinkLocalIpv4.Contains(address);
}

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;

namespace EhViewerForPc.Core.Tests;

public sealed class AutomaticProxyTests
{
    [Theory]
    [InlineData(53, "Meta Tunnel", true)]
    [InlineData(53, "Wintun Userspace Tunnel", true)]
    [InlineData(6, "OpenVPN Data Channel Offload", true)]
    [InlineData(23, "Work VPN", true)]
    [InlineData(23, "WAN Miniport (PPPOE)", false)]
    [InlineData(131, "Microsoft Teredo Tunneling Adapter", false)]
    [InlineData(6, "Hyper-V Virtual Ethernet Adapter", false)]
    [InlineData(71, "Wi-Fi", false)]
    public void DetectsVpnInterfaces(int type, string description, bool expected) =>
        Assert.Equal(expected, VpnConnection.IsTunnel((NetworkInterfaceType)type, description));

    [Theory]
    [InlineData("198.18.0.1", true)]
    [InlineData("fdfe:dcba:9876::1", true)]
    [InlineData("169.254.2.3", false)]
    [InlineData("fe80::1", false)]
    [InlineData("127.0.0.1", false)]
    public void RequiresUsableTunnelAddress(string address, bool expected) =>
        Assert.Equal(expected, VpnConnection.IsUsableAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task NextRequestSwitchesRouteWhileAnExistingResponseRemainsReadable(bool forImages, bool proxyForImages)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string url = $"http://e-hentai.org:{port}/fixture";
        bool vpnConnected = false;
        var network = new NetworkSettings
        {
            ProxyForImages = proxyForImages,
            HostsOverride = "127.0.0.1 e-hentai.org",
        };
        using var client = EhHttp.Create(network, new CookieContainer(), forImages, log: null,
            upstreamHosts: null, useSystemNetwork: () => vpnConnected, systemProxy: new WebProxy(IPAddress.Loopback.ToString(), port));

        Task<HttpResponseMessage> first = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        using TcpClient firstConnection = await listener.AcceptTcpClientAsync(token);
        using NetworkStream firstStream = firstConnection.GetStream();
        Assert.Equal("GET /fixture HTTP/1.1", await ReadRequestLineAsync(firstStream, token));
        await firstStream.WriteAsync(
            "HTTP/1.1 200 OK\r\nContent-Length: 7\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
        using HttpResponseMessage firstResponse = await first;

        vpnConnected = true;
        string systemRequest = forImages && !proxyForImages ? "GET /fixture HTTP/1.1" : $"GET {url} HTTP/1.1";
        await CheckRequestAsync(systemRequest);
        await firstStream.WriteAsync("fixture"u8.ToArray(), token);
        Assert.Equal("fixture", await firstResponse.Content.ReadAsStringAsync(token));

        vpnConnected = false;
        await CheckRequestAsync("GET /fixture HTTP/1.1");

        async Task CheckRequestAsync(string expectedRequest)
        {
            Task<string> request = client.GetStringAsync(url, token);
            using TcpClient connection = await listener.AcceptTcpClientAsync(token);
            using NetworkStream stream = connection.GetStream();
            Assert.Equal(expectedRequest, await ReadRequestLineAsync(stream, token));
            await stream.WriteAsync(
                "HTTP/1.1 200 OK\r\nContent-Length: 7\r\nConnection: close\r\n\r\nfixture"u8.ToArray(), token);
            Assert.Equal("fixture", await request);
        }
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken token)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        string? requestLine = await reader.ReadLineAsync(token);
        while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
        return requestLine;
    }
}

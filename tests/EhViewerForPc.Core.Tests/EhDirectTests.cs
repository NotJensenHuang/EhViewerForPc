using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;

namespace EhViewerForPc.Core.Tests;

public sealed class EhDirectTests
{
    [Fact]
    public void SiteConnectionsPreferNewUpstreamAddressesAndKeepRetiredOnesAsFallback()
    {
        IPAddress retired = IPAddress.Parse("192.0.2.1");
        IPAddress retained = IPAddress.Parse("192.0.2.2");
        IPAddress added = IPAddress.Parse("192.0.2.3");
        IPAddress[] builtIn = [retired, retained];

        var changed = EhHttp.OrderDirectCandidates(builtIn, [retained, added]);
        Assert.Equal([added, retained], changed.Preferred);
        Assert.Equal([retired], changed.Fallback);

        var reordered = EhHttp.OrderDirectCandidates(builtIn, [retained, retired]);
        Assert.Same(builtIn, reordered.Preferred);
        Assert.Empty(reordered.Fallback);
    }

    [Theory]
    [InlineData("reader.hath.network", true)]
    [InlineData("reader.HATH.NETWORK.", true)]
    [InlineData("e-hentai.org", true)]
    [InlineData("reader.hath.network.example", false)]
    [InlineData("reader.lan", false)]
    public void EhDirectFiltersPairedFakeAddressesForPublicGalleryHosts(string host, bool dropsFakeIpv6)
    {
        IPAddress fakeIpv4 = IPAddress.Parse("198.18.33.5");
        IPAddress fakeIpv6 = IPAddress.Parse("fdfe:dcba:9876::17");
        IPAddress localIpv6 = IPAddress.Parse("fdfe:dcba:9876:1::17");
        IPAddress publicIpv6 = IPAddress.Parse("2606:4700::1111");

        IPAddress[] actual = EhHttp.FilterDirectSystemAddresses(
            host, [fakeIpv4, fakeIpv6, localIpv6, publicIpv6]);

        Assert.Equal(dropsFakeIpv6 ? [localIpv6, publicIpv6] : [fakeIpv6, localIpv6, publicIpv6], actual);
        Assert.Equal([fakeIpv6], EhHttp.FilterDirectSystemAddresses(host, [fakeIpv6]));
        if (dropsFakeIpv6)
        {
            Assert.Empty(EhHttp.FilterDirectSystemAddresses(host, [fakeIpv4, fakeIpv6]));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EhDirectUsesManualHostsAndPreservesRequestHost(bool forImages)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = EhHttp.Create(new NetworkSettings
        {
            ProxyMode = ProxyMode.EhDirect,
            ProxyForImages = true,
            HostsOverride = "127.0.0.1 e-hentai.org",
        }, new CookieContainer(), forImages);

        Task<string> request = client.GetStringAsync($"http://e-hentai.org:{port}/fixture", timeout.Token);
        using TcpClient connection = await listener.AcceptTcpClientAsync(timeout.Token);
        await using NetworkStream stream = connection.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        string? requestLine = await reader.ReadLineAsync(timeout.Token);
        var headers = new List<string>();
        while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } header)
        {
            headers.Add(header);
        }

        await stream.WriteAsync(
            "HTTP/1.1 200 OK\r\nContent-Length: 7\r\nConnection: close\r\n\r\nfixture"u8.ToArray(),
            timeout.Token);
        Assert.Equal("fixture", await request);
        Assert.Equal("GET /fixture HTTP/1.1", requestLine);
        Assert.Contains($"Host: e-hentai.org:{port}", headers, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EhDirectOmitsSniThenUsesNormalSniAndRejectsAnUntrustedCertificate(bool firstCandidateStalls)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var signingKey = RSA.Create(2048);
        var certificateRequest = new CertificateRequest(
            "CN=e-hentai.org", signingKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stalled = new TcpListener(IPAddress.Parse("127.0.0.2"), port);
        if (firstCandidateStalls) stalled.Start();

        const string localHost = "127.0.0.1 e-hentai.org";
        using var client = EhHttp.Create(new NetworkSettings
        {
            ProxyMode = ProxyMode.EhDirect,
            HostsOverride = firstCandidateStalls ? "127.0.0.2 e-hentai.org\n" + localHost : localHost,
        }, new CookieContainer(), forImages: false);

        Task<HttpResponseMessage> request = client.GetAsync($"https://e-hentai.org:{port}/", timeout.Token);
        using TcpClient? stalledConnection = firstCandidateStalls
            ? await stalled.AcceptTcpClientAsync(timeout.Token)
            : null;
        string? omitted = await ReceiveClientHelloAsync(listener, certificate, timeout.Token);
        string? standard = await ReceiveClientHelloAsync(listener, certificate, timeout.Token);

        await Assert.ThrowsAsync<HttpRequestException>(async () => await request);
        Assert.True(string.IsNullOrEmpty(omitted));
        Assert.Equal("e-hentai.org", standard);
    }

    private static async Task<string?> ReceiveClientHelloAsync(
        TcpListener listener, X509Certificate2 certificate, CancellationToken token)
    {
        using TcpClient connection = await listener.AcceptTcpClientAsync(token);
        await using var tls = new SslStream(connection.GetStream());
        string? serverName = null;
        try
        {
            await tls.AuthenticateAsServerAsync((_, hello, _, _) =>
            {
                serverName = hello.ServerName;
                return ValueTask.FromResult(new SslServerAuthenticationOptions { ServerCertificate = certificate });
            }, state: null, token);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
            // 客户端应拒绝未受信任的测试证书。
        }

        return serverName;
    }
}

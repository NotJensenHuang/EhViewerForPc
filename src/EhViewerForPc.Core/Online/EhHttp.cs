using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EhViewerForPc.Core.Models;

namespace EhViewerForPc.Core.Online;

/// <summary>
/// Cookie jar shared by every request. E-Hentai authenticates through cookies
/// (ipb_member_id / ipb_pass_hash) and hands out <c>igneous</c> after the first
/// page load; the same four values are valid on both e-hentai.org and
/// exhentai.org, so they are installed on both hosts.
/// </summary>
public sealed class EhSession
{
    public static readonly string[] AuthCookieNames =
        ["ipb_member_id", "ipb_pass_hash", "igneous", "sk", "ipb_session_id"];

    private static readonly string[] CookieHosts = ["https://e-hentai.org", "https://exhentai.org"];

    private readonly object _gate = new();

    public CookieContainer Cookies { get; } = new(4096, 2048, 8192);

    public bool HasCredentials
    {
        get
        {
            lock (_gate)
            {
                CookieCollection all = Cookies.GetAllCookies();
                return all["ipb_member_id"] is { Value.Length: > 0 } &&
                       all["ipb_pass_hash"] is { Value.Length: > 0 };
            }
        }
    }

    public string UserName
    {
        get
        {
            lock (_gate)
            {
                return Cookies.GetAllCookies()["ipb_member_id"]?.Value ?? string.Empty;
            }
        }
    }

    public void SetImageSize(int width)
    {
        lock (_gate)
        {
            foreach (string host in CookieHosts)
            {
                var uri = new Uri(host);
                string current = Cookies.GetCookies(uri)["uconfig"]?.Value ?? "";
                var options = current.Split('-', StringSplitOptions.RemoveEmptyEntries)
                    .Where(p => !p.StartsWith("xr_", StringComparison.Ordinal) && !p.StartsWith("oi_", StringComparison.Ordinal));
                TryAdd(uri, "uconfig", string.Join('-', options.Concat(new[] { "xr_" + width, "oi_n" })));
            }
        }
    }

    /// <summary>Installs stored cookies on both hosts.</summary>
    public void Import(IReadOnlyDictionary<string, string> cookies)
    {
        lock (_gate)
        {
            foreach (string host in CookieHosts)
            {
                var uri = new Uri(host);
                foreach (KeyValuePair<string, string> pair in cookies)
                {
                    if (pair.Key.Length == 0 || pair.Value.Length == 0) continue;
                    TryAdd(uri, pair.Key, pair.Value);
                }
            }
        }
    }

    /// <summary>Adds every auth cookie the server just set to the other host as well.</summary>
    public void MirrorAuthCookies()
    {
        lock (_gate)
        {
            CookieCollection all = Cookies.GetAllCookies();
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Cookie cookie in all)
            {
                if (cookie.Value.Length == 0) continue;
                if (!AuthCookieNames.Contains(cookie.Name, StringComparer.OrdinalIgnoreCase)) continue;
                values[cookie.Name] = cookie.Value;
            }

            foreach (string host in CookieHosts)
            {
                var uri = new Uri(host);
                foreach (KeyValuePair<string, string> pair in values) TryAdd(uri, pair.Key, pair.Value);
            }
        }
    }

    private void TryAdd(Uri uri, string name, string value)
    {
        try
        {
            Cookies.Add(uri, new Cookie(name, value, "/", uri.Host));
        }
        catch (CookieException)
        {
            // A malformed value must not break sign-in; the request will simply
            // be unauthenticated, which the caller already handles.
        }
    }

    public Dictionary<string, string> Snapshot()
    {
        lock (_gate)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Cookie cookie in Cookies.GetAllCookies())
            {
                if (cookie.Value.Length == 0) continue;
                if (!AuthCookieNames.Contains(cookie.Name, StringComparer.OrdinalIgnoreCase)) continue;
                result[cookie.Name] = cookie.Value;
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (Cookie cookie in Cookies.GetAllCookies())
            {
                try
                {
                    Cookies.Add(new Cookie(cookie.Name, string.Empty, cookie.Path, cookie.Domain)
                    {
                        Expires = DateTime.Now.AddYears(-2),
                    });
                }
                catch (CookieException)
                {
                    // Ignore cookies the container refuses to re-add.
                }
            }
        }
    }

    /// <summary>
    /// Parses every shape a user can realistically paste: a bare
    /// <c>a=1; b=2</c> pair list, a <c>Cookie:</c> request header, a browser
    /// "Copy as cURL" command, a JSON object, or the multi-line table the
    /// DevTools application panel copies.
    /// </summary>
    public static Dictionary<string, string> ParseCookieString(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return result;

        string input = text.Trim();

        // json: {"ipb_member_id":"123","ipb_pass_hash":"abc"} (optionally nested)
        if (input.StartsWith('{'))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(input);
                AddJsonCookies(document.RootElement, result);
                if (result.Count > 0) return result;
            }
            catch (JsonException)
            {
                // Not JSON after all; fall through to the text parsers.
            }
        }

        string payload = ExtractCookieHeader(input) ?? input;

        foreach (string rawLine in payload.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            // DevTools copies one cookie per line as name<TAB>value<TAB>domain.
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.Contains('\t') && !line.Contains('='))
            {
                string[] columns = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (columns.Length >= 2 && columns[1].Length > 0)
                {
                    result[columns[0]] = columns[1];
                    continue;
                }
            }

            foreach (string part in line.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                TryAddPair(part, result);
            }
        }

        return result;
    }

    private static void AddJsonCookies(JsonElement element, Dictionary<string, string> result)
    {
        if (element.ValueKind != JsonValueKind.Object) return;

        if (element.TryGetProperty("cookies", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object)
        {
            AddJsonCookies(nested, result);
            if (result.Count > 0) return;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) continue;
            string value = property.Value.GetString() ?? string.Empty;
            if (value.Length > 0) result[property.Name] = value;
        }
    }

    /// <summary>Pulls the value out of <c>-H 'cookie: …'</c> / <c>-b '…'</c> / <c>Cookie: …</c>.</summary>
    private static string? ExtractCookieHeader(string text)
    {
        int index = text.IndexOf("cookie:", StringComparison.OrdinalIgnoreCase);
        if (index >= 0) return TakeQuotedValue(text, index + "cookie:".Length);

        index = text.IndexOf("-b ", StringComparison.OrdinalIgnoreCase);
        if (index >= 0) return TakeQuotedValue(text, index + 3);

        index = text.IndexOf("--cookie ", StringComparison.OrdinalIgnoreCase);
        if (index >= 0) return TakeQuotedValue(text, index + "--cookie".Length);

        return null;
    }

    private static string TakeQuotedValue(string text, int start)
    {
        int end = text.Length;
        foreach (char quote in new[] { '"', '\'' })
        {
            int next = text.IndexOf(quote, start);
            if (next >= 0 && next < end) end = next;
        }

        return text[start..end].Trim();
    }

    private static bool TryAddPair(string part, Dictionary<string, string> result)
    {
        int eq = part.IndexOf('=');
        if (eq <= 0) return false;

        string name = part[..eq].Trim();
        string value = part[(eq + 1)..].Trim();

        // DevTools' table copy adds a domain column after a tab.
        int tab = value.IndexOf('\t');
        if (tab >= 0) value = value[..tab].Trim();

        value = value.Trim('"', '\'');
        if (name.Length == 0 || value.Length == 0) return false;
        if (name.Contains(' ') || name.Contains('\t')) return false;

        result[name] = value;
        return true;
    }
}

/// <summary>
/// Builds the HTTP stacks. Two clients exist per configuration: page traffic
/// (proxy + DoH) and image traffic (proxy optional, because image hosts are
/// frequently reached directly even when the forum is not).
/// </summary>
public static class EhHttp
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/126.0.0.0 Safari/537.36";

    public static HttpClient Create(NetworkSettings network, CookieContainer cookies, bool forImages, Action<string>? log = null)
    {
        var handler = new SocketsHttpHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(20, network.TimeoutSeconds)),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = forImages ? 8 : 4,
        };

        ApplyProxy(handler, network, forImages);

        HttpMessageHandler clientHandler = handler;
        bool direct = network.ProxyMode == ProxyMode.EhDirect;
        bool doh = network.DnsOverHttps;
        Dictionary<string, IPAddress[]> hosts = ParseHosts(network.HostsOverride);
        if (direct || doh || hosts.Count > 0)
        {
            var resolver = new HostResolver(
                doh ? network.DohEndpoint : null, hosts, direct, log);
            handler.ConnectCallback = resolver.ConnectAsync;
            clientHandler = new ResolverHandler(handler, resolver);
        }

        var client = new HttpClient(clientHandler)
        {
            Timeout = TimeSpan.FromSeconds(network.TimeoutSeconds),
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        // 图片请求不声明 avif：本地容器校验与解码只覆盖 jpeg/png/gif/webp，
        // 声明未支持的格式只会换来一个无法落盘的响应。
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept",
            forImages
                ? "image/webp,image/apng,image/png,image/jpeg,image/gif,*/*;q=0.8"
                : "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        return client;
    }

    private static void ApplyProxy(SocketsHttpHandler handler, NetworkSettings network, bool forImages)
    {
        if (forImages && !network.ProxyForImages)
        {
            handler.UseProxy = false;
            return;
        }

        switch (network.ProxyMode)
        {
            case ProxyMode.None:
            case ProxyMode.EhDirect:
                handler.UseProxy = false;
                break;
            case ProxyMode.System:
                handler.UseProxy = true;
                handler.Proxy = null;
                break;
            case ProxyMode.Http:
            case ProxyMode.Socks5:
                if (!network.HasProxy)
                {
                    handler.UseProxy = false;
                    break;
                }

                string scheme = network.ProxyMode == ProxyMode.Socks5 ? "socks5" : "http";
                var proxy = new WebProxy($"{scheme}://{network.ProxyHost}:{network.ProxyPort}");
                if (network.ProxyUserName.Length > 0)
                {
                    proxy.Credentials = new NetworkCredential(network.ProxyUserName, network.ProxyPassword);
                }

                handler.Proxy = proxy;
                handler.UseProxy = true;
                break;
        }
    }

    public static Dictionary<string, IPAddress[]> ParseHosts(string? text)
    {
        var map = new Dictionary<string, IPAddress[]>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return map;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            if (IPAddress.TryParse(parts[0], out IPAddress? address))
            {
                for (int i = 1; i < parts.Length; i++) AddHost(map, parts[i], address);
            }
            else if (IPAddress.TryParse(parts[^1], out IPAddress? tail))
            {
                for (int i = 0; i < parts.Length - 1; i++) AddHost(map, parts[i], tail);
            }
        }

        return map;
    }

    private static void AddHost(Dictionary<string, IPAddress[]> map, string host, IPAddress address)
    {
        string key = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (key.Length == 0 || key.Length > 253) return;
        map[key] = map.TryGetValue(key, out IPAddress[]? existing)
            ? [.. existing, address]
            : [address];
    }

    internal static IPAddress[] FilterDirectSystemAddresses(string host, IPAddress[] addresses)
    {
        if (!Array.Exists(addresses, IsFakeIpv4)) return addresses;

        host = host.TrimEnd('.');
        bool isPublicGalleryHost = EhDirectHosts.Addresses.ContainsKey(host) ||
            host.EndsWith(".hath.network", StringComparison.OrdinalIgnoreCase);

        // 对已知公开图库域名，仅在同一答复已有 Fake-IP IPv4 时排除 Mihomo 的示例 IPv6 段。
        return Array.FindAll(addresses, address =>
            !IsFakeIpv4(address) && (!isPublicGalleryHost || !EhDirectHosts.FakeIpv6Range.Contains(address)));
    }

    private static bool IsFakeIpv4(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        // 198.18.0.0/15 为基准测试网段，部分 TUN 代理将其用作 DNS 占位地址。
        Span<byte> octets = stackalloc byte[4];
        return address.TryWriteBytes(octets, out _) && octets[0] == 198 && (octets[1] & 0xfe) == 18;
    }

    private sealed class ResolverHandler(HttpMessageHandler inner, HostResolver resolver) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) resolver.Dispose();
        }
    }

    private static class EhDirectHosts
    {
        public static readonly IPNetwork FakeIpv6Range = IPNetwork.Parse("fdfe:dcba:9876::/64");

        // 上游 Ehviewer_CN_SXJ 的 EhHosts.java (243c39b)。仅为站点内容提供候选地址。
        public static readonly Dictionary<string, IPAddress[]> Addresses = ParseHosts("""
            e-hentai.org 172.66.132.196
            e-hentai.org 104.20.18.168
            e-hentai.org 104.20.19.168
            e-hentai.org 172.66.140.62
            e-hentai.org 172.67.2.238
            forums.e-hentai.org 172.66.132.196
            forums.e-hentai.org 172.66.140.62
            upld.e-hentai.org 89.149.221.236
            upld.e-hentai.org 95.211.208.236
            ehgt.org 185.132.132.79
            ehgt.org 62.112.8.152
            ehgt.org 185.132.132.76
            ehgt.org 2a00:7c80:0:123::3a85
            ehgt.org 2a00:7c80:0:12d::38a1
            ehgt.org 2a00:7c80:0:13b::37a4
            exhentai.org 178.175.128.251
            exhentai.org 178.175.129.251
            exhentai.org 178.175.128.252
            exhentai.org 178.175.128.253
            exhentai.org 178.175.128.254
            exhentai.org 178.175.129.252
            exhentai.org 178.175.129.253
            exhentai.org 178.175.129.254
            exhentai.org 178.175.132.19
            exhentai.org 178.175.132.20
            exhentai.org 178.175.132.21
            exhentai.org 178.175.132.22
            upld.exhentai.org 178.175.132.22
            upld.exhentai.org 178.175.129.254
            upld.exhentai.org 178.175.128.254
            s.exhentai.org 178.175.129.253
            s.exhentai.org 178.175.129.254
            s.exhentai.org 178.175.128.253
            s.exhentai.org 178.175.128.254
            s.exhentai.org 178.175.132.21
            s.exhentai.org 178.175.132.22
            """);
    }

    /// <summary>在建立 TCP 连接时应用 DoH 和 hosts 配置。</summary>
    private sealed class HostResolver : IDisposable
    {
        private const string DirectDohEndpoint = "https://dns.alidns.com/resolve";
        private static readonly IPAddress[] DirectDohBootstrap = [IPAddress.Parse("223.5.5.5"), IPAddress.Parse("223.6.6.6")];
        private static readonly TimeSpan DirectAddressTimeout = TimeSpan.FromMilliseconds(1500);
        private readonly string? _dohEndpoint;
        private readonly Dictionary<string, IPAddress[]> _hosts;
        private readonly bool _direct;
        private readonly Action<string>? _log;
        private readonly Dictionary<string, (IPAddress[] Addresses, DateTime Expires)> _cache =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<DnsEndPoint, (IPAddress Address, bool OmitSni, DateTime Expires)> _directCache = new();
        private readonly object _gate = new();
        private readonly HttpClient? _dohClient;

        public HostResolver(
            string? dohEndpoint, Dictionary<string, IPAddress[]> hosts, bool direct, Action<string>? log)
        {
            _dohEndpoint = dohEndpoint;
            _hosts = hosts;
            _direct = direct;
            _log = log;
            if (direct || dohEndpoint is not null)
            {
                var dohHandler = new SocketsHttpHandler { UseProxy = !direct };
                if (direct)
                {
                    // DoH 服务器用专用的引导解析，避免递归。
                    dohHandler.ConnectCallback = ConnectDohAsync;
                    dohHandler.UseCookies = false;
                    dohHandler.AllowAutoRedirect = false;
                }

                _dohClient = new HttpClient(dohHandler)
                {
                    Timeout = TimeSpan.FromSeconds(direct ? 3 : 10),
                };
            }
        }

        public void Dispose() => _dohClient?.Dispose();

        public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
        {
            DnsEndPoint endpoint = context.DnsEndPoint;
            HttpRequestMessage request = context.InitialRequestMessage;
            string host = endpoint.Host.TrimEnd('.');
            if (_direct && request.RequestUri?.Scheme == Uri.UriSchemeHttps &&
                request.Headers.Host is null &&
                request.Version.Major == 1 && request.VersionPolicy != HttpVersionPolicy.RequestVersionOrHigher &&
                EhDirectHosts.Addresses.TryGetValue(host, out IPAddress[]? builtIn))
            {
                return await ConnectDirectAsync(endpoint, host, builtIn, token).ConfigureAwait(false);
            }

            IPAddress[] addresses = await ResolveAsync(endpoint.Host, token).ConfigureAwait(false);
            return await ConnectAddressesAsync(addresses, endpoint.Port, token).ConfigureAwait(false);
        }

        private async ValueTask<Stream> ConnectDohAsync(SocketsHttpConnectionContext context, CancellationToken token)
        {
            DnsEndPoint endpoint = context.DnsEndPoint;
            IPAddress[] addresses;
            if (IPAddress.TryParse(endpoint.Host, out IPAddress? literal))
            {
                addresses = [literal];
            }
            else if (_hosts.TryGetValue(endpoint.Host, out IPAddress[]? mapped))
            {
                addresses = mapped;
            }
            else if (endpoint.Host.Equals("dns.alidns.com", StringComparison.OrdinalIgnoreCase))
            {
                addresses = DirectDohBootstrap;
            }
            else
            {
                addresses = await ResolveSystemAsync(endpoint.Host, token).ConfigureAwait(false);
            }

            return await ConnectAddressesAsync(addresses, endpoint.Port, token).ConfigureAwait(false);
        }

        private async ValueTask<Stream> ConnectAddressesAsync(
            IPAddress[] addresses, int port, CancellationToken token)
        {
            Exception? last = null;
            foreach (IPAddress address in addresses)
            {
                using var attempt = _direct && addresses.Length > 1
                    ? CancellationTokenSource.CreateLinkedTokenSource(token)
                    : null;
                attempt?.CancelAfter(DirectAddressTimeout);
                try
                {
                    return await ConnectAddressAsync(address, port, attempt?.Token ?? token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
                {
                    last = ex;
                }
                catch (SocketException ex)
                {
                    last = ex;
                }
            }

            throw last ?? new SocketException((int)SocketError.HostNotFound);
        }

        private async ValueTask<Stream> ConnectDirectAsync(
            DnsEndPoint endpoint, string host, IPAddress[] builtIn, CancellationToken token)
        {
            (IPAddress Address, bool OmitSni, DateTime Expires) recent;
            lock (_gate) _directCache.TryGetValue(endpoint, out recent);
            if (recent.Expires > DateTime.UtcNow)
            {
                using var recentTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                recentTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    return await ConnectTlsAsync(recent.Address, endpoint, recent.OmitSni, recentTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (!token.IsCancellationRequested &&
                    ex is SocketException or IOException or AuthenticationException or OperationCanceledException)
                {
                    lock (_gate)
                    {
                        if (_directCache.TryGetValue(endpoint, out var current) && current == recent)
                        {
                            _directCache.Remove(endpoint);
                        }
                    }
                }
            }

            if (_hosts.TryGetValue(host, out IPAddress[]? mapped))
            {
                return await ConnectTlsCandidatesAsync(mapped, endpoint, token).ConfigureAwait(false);
            }

            // 给可能过期的内置地址独立的预算，保留重新解析和连接的时间。
            using var builtInTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            builtInTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                return await ConnectTlsCandidatesAsync(builtIn, endpoint, builtInTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!token.IsCancellationRequested &&
                ex is SocketException or IOException or AuthenticationException or OperationCanceledException)
            {
                _log?.Invoke($"内置地址连接失败，重新解析 {host}：{ex.Message}");
            }

            IPAddress[] resolved = await ResolveAsync(host, token).ConfigureAwait(false);
            return await ConnectTlsCandidatesAsync(resolved, endpoint, token).ConfigureAwait(false);
        }

        private async ValueTask<Stream> ConnectTlsCandidatesAsync(
            IPAddress[] addresses, DnsEndPoint endpoint, CancellationToken token)
        {
            Exception? last = null;
            foreach (IPAddress address in addresses)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                attempt.CancelAfter(DirectAddressTimeout);
                try
                {
                    return await ConnectAddressTlsWithFallbackAsync(address, endpoint, attempt.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
                {
                    last = ex;
                }
                catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException)
                {
                    last = ex;
                }
            }

            throw last ?? new SocketException((int)SocketError.HostNotFound);
        }

        private async ValueTask<Stream> ConnectAddressTlsWithFallbackAsync(
            IPAddress address, DnsEndPoint endpoint, CancellationToken token)
        {
            try
            {
                return await ConnectAndRememberTlsAsync(address, endpoint, omitSni: true, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or AuthenticationException)
            {
                // TLS 失败后在新连接上使用标准 SNI，由系统校验完整证书。
                return await ConnectAndRememberTlsAsync(address, endpoint, omitSni: false, token).ConfigureAwait(false);
            }
        }

        private async ValueTask<Stream> ConnectAndRememberTlsAsync(
            IPAddress address, DnsEndPoint endpoint, bool omitSni, CancellationToken token)
        {
            Stream stream = await ConnectTlsAsync(address, endpoint, omitSni, token).ConfigureAwait(false);
            lock (_gate) _directCache[endpoint] = (address, omitSni, DateTime.UtcNow.AddMinutes(10));
            return stream;
        }

        private async ValueTask<Stream> ConnectTlsAsync(
            IPAddress address, DnsEndPoint endpoint, bool omitSni, CancellationToken token)
        {
            NetworkStream socket = await ConnectAddressAsync(address, endpoint.Port, token).ConfigureAwait(false);
            string host = endpoint.Host.TrimEnd('.');
            var tls = new SslStream(socket);
            var options = new SslClientAuthenticationOptions { TargetHost = omitSni ? string.Empty : host };
            if (omitSni)
            {
                // 省略 SNI 时由系统校验证书链，并用原始请求域名执行名称校验。
                options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    (errors & ~SslPolicyErrors.RemoteCertificateNameMismatch) == SslPolicyErrors.None &&
                    certificate is X509Certificate2 leaf && leaf.MatchesHostname(host);
            }

            try
            {
                await tls.AuthenticateAsClientAsync(options, token).ConfigureAwait(false);
                return tls;
            }
            catch
            {
                await tls.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private async ValueTask<NetworkStream> ConnectAddressAsync(
            IPAddress address, int port, CancellationToken token)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                if (_direct)
                {
                    DirectNetworkInterface.Bind(socket, address);
                }

                await socket.ConnectAsync(new IPEndPoint(address, port), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private async Task<IPAddress[]> ResolveAsync(string host, CancellationToken token)
        {
            if (IPAddress.TryParse(host, out IPAddress? literal)) return [literal];

            lock (_gate)
            {
                if (_cache.TryGetValue(host, out var cached) && cached.Expires > DateTime.UtcNow)
                {
                    return cached.Addresses;
                }
            }

            IPAddress[]? addresses = null;
            if (_hosts.TryGetValue(host, out IPAddress[]? mapped))
            {
                addresses = mapped;
            }
            else if (_dohEndpoint is not null)
            {
                addresses = await ResolveDohAsync(_dohEndpoint, host, token).ConfigureAwait(false);
            }

            if (addresses is null || addresses.Length == 0)
            {
                addresses = await ResolveSystemAsync(host, token).ConfigureAwait(false);
            }

            if (_direct && addresses.Length == 0 &&
                !DirectDohEndpoint.Equals(_dohEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                addresses = await ResolveDohAsync(DirectDohEndpoint, host, token).ConfigureAwait(false) ?? [];
            }

            lock (_gate)
            {
                if (!_direct || addresses.Length > 0)
                {
                    _cache[host] = (addresses, DateTime.UtcNow.AddMinutes(10));
                }
            }

            return addresses;
        }

        private async Task<IPAddress[]> ResolveSystemAsync(string host, CancellationToken token)
        {
            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
                return _direct ? FilterDirectSystemAddresses(host, addresses) : addresses;
            }
            catch (SocketException ex)
            {
                _log?.Invoke($"DNS fallback failed for {host}: {ex.Message}");
                return [];
            }
        }

        private async Task<IPAddress[]?> ResolveDohAsync(string dohEndpoint, string host, CancellationToken token)
        {
            try
            {
                if (!Uri.TryCreate(dohEndpoint, UriKind.Absolute, out Uri? endpoint) ||
                    (endpoint.Scheme != Uri.UriSchemeHttps && (_direct || endpoint.Scheme != Uri.UriSchemeHttp)))
                {
                    return null;
                }

                var lookup = new UriBuilder(endpoint);
                string nameAndType = "name=" + Uri.EscapeDataString(host) + "&type=A";
                lookup.Query = endpoint.Query.Length > 1 ? endpoint.Query[1..] + "&" + nameAndType : nameAndType;
                using var request = new HttpRequestMessage(HttpMethod.Get, lookup.Uri);
                request.Headers.TryAddWithoutValidation("Accept", "application/dns-json");

                using HttpResponseMessage response = await _dohClient!.SendAsync(request, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;

                await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("Answer", out JsonElement answers) ||
                    answers.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var list = new List<IPAddress>(4);
                foreach (JsonElement answer in answers.EnumerateArray())
                {
                    if (answer.ValueKind != JsonValueKind.Object ||
                        (answer.TryGetProperty("type", out JsonElement type) &&
                            (type.ValueKind != JsonValueKind.Number || !type.TryGetInt32(out int recordType) || recordType != 1)) ||
                        !answer.TryGetProperty("data", out JsonElement data) ||
                        data.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    if (IPAddress.TryParse(data.GetString(), out IPAddress? address) && (!_direct || !IsFakeIpv4(address)))
                    {
                        list.Add(address);
                    }
                }

                return list.Count == 0 ? null : list.ToArray();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                token.ThrowIfCancellationRequested();
                _log?.Invoke("DoH lookup failed: " + ex.Message);
                return null;
            }
        }
    }
}

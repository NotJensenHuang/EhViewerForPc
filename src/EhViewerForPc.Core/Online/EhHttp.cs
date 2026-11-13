using System.Net;
using System.Net.Sockets;
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

        bool doh = network.DnsOverHttps;
        Dictionary<string, IPAddress[]> hosts = ParseHosts(network.HostsOverride);
        if (doh || hosts.Count > 0)
        {
            var resolver = new HostResolver(doh ? network.DohEndpoint : null, hosts, log);
            handler.ConnectCallback = (context, token) => resolver.ConnectAsync(context.DnsEndPoint, token);
        }

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(network.TimeoutSeconds),
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept",
            forImages
                ? "image/avif,image/webp,image/apng,image/*,*/*;q=0.8"
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

    /// <summary>Socket connector that replaces the system resolver with DoH/hosts.</summary>
    private sealed class HostResolver
    {
        private readonly string? _dohEndpoint;
        private readonly Dictionary<string, IPAddress[]> _hosts;
        private readonly Action<string>? _log;
        private readonly Dictionary<string, (IPAddress[] Addresses, DateTime Expires)> _cache =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();
        private HttpClient? _dohClient;

        public HostResolver(string? dohEndpoint, Dictionary<string, IPAddress[]> hosts, Action<string>? log)
        {
            _dohEndpoint = dohEndpoint;
            _hosts = hosts;
            _log = log;
        }

        public async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, CancellationToken token)
        {
            IPAddress[] addresses = await ResolveAsync(endpoint.Host, token).ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                throw new SocketException((int)SocketError.HostNotFound);
            }

            Exception? last = null;
            foreach (IPAddress address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };

                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                {
                    socket.Dispose();
                    if (ex is OperationCanceledException) throw;
                    last = ex;
                }
            }

            throw last ?? new SocketException((int)SocketError.ConnectionRefused);
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
                addresses = await ResolveDohAsync(host, token).ConfigureAwait(false);
            }

            if (addresses is null || addresses.Length == 0)
            {
                try
                {
                    addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
                }
                catch (SocketException ex)
                {
                    _log?.Invoke($"DNS fallback failed for {host}: {ex.Message}");
                    addresses = [];
                }
            }

            lock (_gate)
            {
                _cache[host] = (addresses, DateTime.UtcNow.AddMinutes(10));
            }

            return addresses;
        }

        private async Task<IPAddress[]?> ResolveDohAsync(string host, CancellationToken token)
        {
            try
            {
                _dohClient ??= new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                string separator = _dohEndpoint!.Contains('?') ? "&" : "?";
                string url = _dohEndpoint + separator + "name=" + Uri.EscapeDataString(host) + "&type=A";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Accept", "application/dns-json");

                using HttpResponseMessage response = await _dohClient.SendAsync(request, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;

                await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
                if (!document.RootElement.TryGetProperty("Answer", out JsonElement answers) ||
                    answers.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var list = new List<IPAddress>(4);
                foreach (JsonElement answer in answers.EnumerateArray())
                {
                    if (answer.TryGetProperty("type", out JsonElement type) && type.GetInt32() != 1) continue;
                    if (!answer.TryGetProperty("data", out JsonElement data)) continue;
                    if (IPAddress.TryParse(data.GetString(), out IPAddress? address)) list.Add(address);
                }

                return list.Count == 0 ? null : list.ToArray();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _log?.Invoke("DoH lookup failed: " + ex.Message);
                return null;
            }
        }
    }
}

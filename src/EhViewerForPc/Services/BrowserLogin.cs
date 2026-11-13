using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace EhViewerForPc.Services;

/// <summary>How the login browser should start.</summary>
public sealed class BrowserLoginOptions
{
    /// <summary>First page to open (the forums login form).</summary>
    public required string Url { get; init; }

    /// <summary>Keeps the window invisible; used by the diagnostic probe only.</summary>
    public bool Headless { get; init; }

    /// <summary>Optional Chromium proxy string ("http://host:port", "socks5://host:port").</summary>
    public string? ProxyServer { get; init; }
}

/// <summary>
/// A real browser driven through the DevTools protocol.
///
/// forums.e-hentai.org rejects plain HTTP clients with a Cloudflare "Just a
/// moment" challenge, which is why password login over HttpClient always ends
/// in HTTP 403. The Android app works around this with a WebView; on the desktop
/// the equivalent is to open Edge/Chrome with a throwaway profile and read the
/// auth cookies over CDP once the user has logged in.
/// </summary>
public sealed class BrowserLoginSession : IDisposable
{
    /// <summary>Forums login form; the Cloudflare challenge lives on this host.</summary>
    public const string EhentaiLoginPage = "https://forums.e-hentai.org/index.php?act=Login&CODE=00";

    private static readonly string[] AuthCookieNames =
        ["ipb_member_id", "ipb_pass_hash", "igneous", "sk", "ipb_session_id"];

    private readonly Process _process;
    private readonly string? _ownedProfile;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ClientWebSocket _socket;
    private int _nextId;
    private bool _disposed;

    private BrowserLoginSession(
        Process process,
        ClientWebSocket socket,
        HttpClient http,
        int port,
        string browserName,
        string? ownedProfile)
    {
        _process = process;
        _socket = socket;
        _http = http;
        Port = port;
        BrowserName = browserName;
        _ownedProfile = ownedProfile;
    }

    public string BrowserName { get; }

    public int Port { get; }

    public bool HasExited => _process.HasExited;

    /// <summary>Starts the browser and connects to its DevTools endpoint.</summary>
    public static async Task<BrowserLoginSession> LaunchAsync(
        BrowserLoginOptions options,
        CancellationToken cancellationToken)
    {
        (string name, string path) = FindBrowser() ??
            throw new InvalidOperationException("没有找到 Microsoft Edge 或 Google Chrome，请先安装其中一个浏览器");

        string? profile = options.Headless ? null : CreateProfileDirectory();
        int port = GetFreePort();

        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
            CreateNoWindow = false,
            // Chromium prints "DevTools listening on …" to stderr; the app has
            // no console, and the pipes must be drained to avoid blocking.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("--remote-debugging-port=" + port);
        if (profile is not null) startInfo.ArgumentList.Add("--user-data-dir=" + profile);
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--disable-sync");
        startInfo.ArgumentList.Add("--disable-extensions");
        startInfo.ArgumentList.Add("--disable-background-networking");
        startInfo.ArgumentList.Add("--disable-component-update");
        startInfo.ArgumentList.Add("--window-size=1200,900");
        if (options.Headless)
        {
            startInfo.ArgumentList.Add("--headless=new");
        }
        else
        {
            startInfo.ArgumentList.Add("--new-window");
        }

        if (!string.IsNullOrWhiteSpace(options.ProxyServer))
        {
            startInfo.ArgumentList.Add("--proxy-server=" + options.ProxyServer);
        }

        startInfo.ArgumentList.Add(options.Url);

        Process process;
        try
        {
            process = Process.Start(startInfo) ??
                      throw new InvalidOperationException("无法启动浏览器进程");

            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException("无法启动 " + name + "：" + ex.Message, ex);
        }

        var http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        })
        {
            Timeout = TimeSpan.FromSeconds(3),
        };

        try
        {
            string webSocketUrl = await WaitForPageTargetAsync(http, port, process, cancellationToken)
                .ConfigureAwait(false);

            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
            await socket.ConnectAsync(new Uri(webSocketUrl), cancellationToken).ConfigureAwait(false);

            return new BrowserLoginSession(process, socket, http, port, name, profile);
        }
        catch
        {
            KillProcess(process);
            http.Dispose();
            DeleteProfile(profile);
            throw;
        }
    }

    /// <summary>Reads ipb_member_id / ipb_pass_hash (plus friends) from the browser.</summary>
    public async Task<Dictionary<string, string>> ReadAuthCookiesAsync(CancellationToken cancellationToken)
    {
        JsonElement root = await CallAsync("Storage.getCookies", null, cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("result", out JsonElement payload) ||
            !payload.TryGetProperty("cookies", out JsonElement cookies) ||
            cookies.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement cookie in cookies.EnumerateArray())
        {
            string name = ReadString(cookie, "name");
            string domain = ReadString(cookie, "domain");
            string value = ReadString(cookie, "value");
            if (name.Length == 0 || value.Length == 0) continue;
            if (!AuthCookieNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

            string bare = domain.TrimStart('.');
            bool eh = bare.Equals("e-hentai.org", StringComparison.OrdinalIgnoreCase) ||
                      bare.Equals("exhentai.org", StringComparison.OrdinalIgnoreCase);
            bool sub = bare.EndsWith(".e-hentai.org", StringComparison.OrdinalIgnoreCase) ||
                       bare.EndsWith(".exhentai.org", StringComparison.OrdinalIgnoreCase);
            if (!eh && !sub) continue;

            // ".e-hentai.org" is the cookie the forums hands out and the one the
            // site itself reads; a host-only cookie from a subdomain is a fallback.
            int rank = eh ? 2 : 1;
            if (ranks.TryGetValue(name, out int existing) && existing > rank) continue;

            result[name] = value;
            ranks[name] = rank;
        }

        return result;
    }

    /// <summary>
    /// Injects cookies into the browser session. Used by the diagnostic probe to
    /// prove the detection path without a real account.
    /// </summary>
    public async Task SetCookiesAsync(
        IReadOnlyDictionary<string, string> cookies,
        string domain,
        CancellationToken cancellationToken)
    {
        if (cookies.Count == 0) return;

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("cookies");
            writer.WriteStartArray();
            foreach (KeyValuePair<string, string> pair in cookies)
            {
                writer.WriteStartObject();
                writer.WriteString("name", pair.Key);
                writer.WriteString("value", pair.Value);
                writer.WriteString("domain", domain);
                writer.WriteString("path", "/");
                writer.WriteBoolean("secure", true);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        string parameters = Encoding.UTF8.GetString(buffer.WrittenSpan);
        await CallAsync("Storage.setCookies", parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Current page title, used to show the user what the browser is doing.</summary>
    public async Task<string?> TryGetTitleAsync(CancellationToken cancellationToken)
    {
        try
        {
            JsonElement root = await CallAsync(
                    "Runtime.evaluate",
                    "{\"expression\":\"document.title\",\"returnByValue\":true}",
                    cancellationToken)
                .ConfigureAwait(false);

            if (root.TryGetProperty("result", out JsonElement payload) &&
                payload.TryGetProperty("result", out JsonElement value) &&
                value.TryGetProperty("value", out JsonElement text))
            {
                string title = text.ValueKind == JsonValueKind.String ? text.GetString() ?? string.Empty : string.Empty;
                return title.Trim();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or WebSocketException or JsonException)
        {
            // The tab may be navigating; the next poll tries again.
        }

        return null;
    }

    public async Task BringToFrontAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CallAsync("Page.bringToFront", null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or WebSocketException or JsonException)
        {
            // Not fatal: the window may already be focused.
        }
    }

    private async Task<JsonElement> CallAsync(string method, string? parametersJson, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket.State != WebSocketState.Open)
            {
                await ReconnectAsync(cancellationToken).ConfigureAwait(false);
            }

            int id = ++_nextId;
            byte[] request = BuildRequest(id, method, parametersJson);
            await _socket.SendAsync(request, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream(256 * 1024);

            while (true)
            {
                WebSocketReceiveResult received = await _socket
                    .ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                    .ConfigureAwait(false);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException("DevTools 连接已关闭");
                }

                message.Write(buffer, 0, received.Count);
                if (message.Length > 16L * 1024 * 1024)
                {
                    message.SetLength(0);
                    throw new InvalidOperationException("DevTools 响应过大");
                }

                if (!received.EndOfMessage) continue;

                using JsonDocument document = JsonDocument.Parse(message.ToArray());
                JsonElement root = document.RootElement;
                message.SetLength(0);

                if (root.TryGetProperty("id", out JsonElement replyId) &&
                    replyId.ValueKind == JsonValueKind.Number &&
                    replyId.GetInt32() == id)
                {
                    return root.Clone();
                }

                // Browser events (Runtime.consoleAPICalled and friends) arrive on
                // the same socket; ignore them and keep reading.
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        _socket.Dispose();
        string webSocketUrl = await WaitForPageTargetAsync(_http, Port, _process, cancellationToken)
            .ConfigureAwait(false);

        _socket = new ClientWebSocket();
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        await _socket.ConnectAsync(new Uri(webSocketUrl), cancellationToken).ConfigureAwait(false);
    }

    private static byte[] BuildRequest(int id, string method, string? parametersJson)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            if (parametersJson is not null)
            {
                writer.WritePropertyName("params");
                writer.WriteRawValue(parametersJson, skipInputValidation: true);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static async Task<string> WaitForPageTargetAsync(
        HttpClient http,
        int port,
        Process process,
        CancellationToken cancellationToken)
    {
        string listUrl = $"http://127.0.0.1:{port}/json/list";
        string? lastError = null;

        for (int attempt = 0; attempt < 160; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException("浏览器启动后立即退出，可能是安全策略或配置拦截了调试端口");
            }

            try
            {
                string json = await http.GetStringAsync(listUrl, cancellationToken).ConfigureAwait(false);
                string? target = PickPageTarget(json);
                if (target is not null) return target;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastError = ex.Message;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("无法连接到浏览器调试端口：" + (lastError ?? "超时"));
    }

    private static string? PickPageTarget(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

            string? fallback = null;
            foreach (JsonElement target in document.RootElement.EnumerateArray())
            {
                if (ReadString(target, "type") != "page") continue;
                string url = ReadString(target, "webSocketDebuggerUrl");
                if (url.Length == 0) continue;

                string pageUrl = ReadString(target, "url");
                if (pageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return url;
                fallback ??= url;
            }

            return fallback;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Creates a throwaway Chromium profile so the user's own browser stays untouched.</summary>
    private static string? CreateProfileDirectory()
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "ehviewer-login-" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Chromium is happy without an explicit profile; it just reuses the
            // default one, which is still usable for a manual login.
            return null;
        }
    }

    private static void DeleteProfile(string? profile)
    {
        if (profile is null) return;
        try
        {
            if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The browser may still hold a lock; the OS will clean temp later.
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(3000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Removes profiles left behind by a crashed run. Only directories with our
    /// own prefix inside the temp folder are touched, and only when they are a
    /// day old.
    /// </summary>
    public static void CleanupStaleProfiles()
    {
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(Path.GetTempPath(), "ehviewer-login-*"))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) < TimeSpan.FromDays(1)) continue;
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still in use; try again next start.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp folder not enumerable; nothing to do.
        }
    }

    /// <summary>Locates Edge/Chrome; Edge ships with Windows, Chrome covers the rest.</summary>
    public static (string Name, string Path)? FindBrowser()
    {
        var candidates = new List<(string Name, string Path)>();
        if (OperatingSystem.IsWindows())
        {
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            candidates.Add(("Microsoft Edge", Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe")));
            candidates.Add(("Microsoft Edge", Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe")));
            candidates.Add(("Microsoft Edge", Path.Combine(localAppData, "Microsoft", "Edge", "Application", "msedge.exe")));
            candidates.Add(("Google Chrome", Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe")));
            candidates.Add(("Google Chrome", Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe")));
            candidates.Add(("Google Chrome", Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe")));
            candidates.Add(("Brave", Path.Combine(programFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")));
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add(("Google Chrome", "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"));
            candidates.Add(("Microsoft Edge", "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"));
            candidates.Add(("Brave", "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser"));
        }
        else
        {
            candidates.Add(("Google Chrome", "/usr/bin/google-chrome"));
            candidates.Add(("Chromium", "/usr/bin/chromium"));
            candidates.Add(("Microsoft Edge", "/usr/bin/microsoft-edge"));
        }

        foreach ((string name, string path) in candidates)
        {
            if (path.Length > 0 && File.Exists(path)) return (name, path);
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Closing is best effort.
        }

        _socket.Dispose();
        _gate.Dispose();
        KillProcess(_process);
        _http.Dispose();
        DeleteProfile(_ownedProfile);
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace EhViewerForPc.Core.Online;

internal static partial class UpstreamHostsParser
{
    internal const int MaxSourceLength = 128 * 1024;
    private const int MaxAddressesPerHost = 32;

    private static readonly string[] MapDeclaration =
        ["static", "{", "Map", "<", "String", ",", "List", "<", "InetAddress", ">", ">", "map", "=",
         "new", "HashMap", "<", ">", "(", ")", ";"];

    private static readonly IPNetwork[] ReservedV4 =
    [
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"), IPNetwork.Parse("192.0.2.0/24"), IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("198.18.0.0/15"), IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"), IPNetwork.Parse("224.0.0.0/3"),
    ];

    private static readonly IPNetwork GlobalV6 = IPNetwork.Parse("2000::/3");
    private static readonly IPNetwork[] ReservedV6 =
    [
        IPNetwork.Parse("2001::/23"), IPNetwork.Parse("2001:db8::/32"),
        IPNetwork.Parse("2002::/16"), IPNetwork.Parse("3fff::/20"),
    ];

    [GeneratedRegex("""(?<skip>\s+|//[^\r\n]*|/\*[\s\S]*?\*/)|"(?:\\[^\r\n]|[^"\\\r\n])*"|'(?:\\[^\r\n]|[^'\\\r\n])*'|[A-Za-z_$][A-Za-z0-9_$]*|(?<invalid>/\*|["'])|.""",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex JavaTokens();

    internal static bool TryParse(string source, out Dictionary<string, IPAddress[]> hosts)
    {
        hosts = new(StringComparer.OrdinalIgnoreCase);
        // Java 在词法分析前展开 Unicode 转义。仅接受无需展开的原始源码。
        if (source.Length is 0 or > MaxSourceLength || source.Contains(@"\u", StringComparison.Ordinal) ||
            source.Contains("\"\"\"", StringComparison.Ordinal)) return false;

        var tokens = new List<string>();
        foreach (Match match in JavaTokens().Matches(source))
        {
            if (match.Groups["invalid"].Success) return false;
            if (!match.Groups["skip"].Success) tokens.Add(match.Value);
        }

        string[] syntax = tokens.ToArray();
        var parsed = new Dictionary<string, IPAddress[]>(StringComparer.OrdinalIgnoreCase);
        bool found = false;
        for (int i = 0; i < syntax.Length; i++)
        {
            if (!syntax.AsSpan(i).StartsWith(MapDeclaration)) continue;
            if (found || !TryReadMap(syntax.AsSpan(i + MapDeclaration.Length), parsed)) return false;
            found = true;
        }

        if (!found || parsed.Count != EhHttp.DirectContentHostCount) return false;
        hosts = parsed;
        return true;
    }

    private static bool TryReadMap(ReadOnlySpan<string> syntax, Dictionary<string, IPAddress[]> hosts)
    {
        // 只读取 EhHosts 的内置表初始化器，整个初始化器都必须符合可识别的语法。
        if (!TryReadGroup(ref syntax, "getBuiltInHosts", hosts) ||
            !TryReadGroup(ref syntax, "getBuiltEXHosts", hosts)) return false;
        return syntax.StartsWith(["builtInHosts", "=", "map", ";", "}"]);
    }

    private static bool TryReadGroup(
        ref ReadOnlySpan<string> syntax,
        string setting,
        Dictionary<string, IPAddress[]> hosts)
    {
        if (!Consume(ref syntax, ["if", "(", "Settings", ".", setting, "(", ")", ")", "{"])) return false;
        while (Consume(ref syntax, ["put", "(", "map", ","]))
        {
            if (!TryReadLiteral(ref syntax, out string host)) return false;
            bool allowedHost = EhHttp.IsDirectContentHost(host);
            var addresses = new List<IPAddress>();
            int count = 0;
            while (Consume(ref syntax, [","]))
            {
                if (++count > MaxAddressesPerHost || !TryReadLiteral(ref syntax, out string value)) return false;
                if (!allowedHost) continue;
                if (!TryReadAddress(value, out IPAddress? address)) return false;
                if (!addresses.Contains(address)) addresses.Add(address);
            }

            if (count == 0 || !Consume(ref syntax, [")", ";"])) return false;
            if (allowedHost && !hosts.TryAdd(host, addresses.ToArray())) return false;
        }

        return Consume(ref syntax, ["}"]);
    }

    private static bool TryReadLiteral(ref ReadOnlySpan<string> syntax, out string value)
    {
        value = string.Empty;
        if (syntax.IsEmpty || !syntax[0].StartsWith('"') || syntax[0].Contains('\\')) return false;
        value = syntax[0][1..^1];
        syntax = syntax[1..];
        return value.Length > 0;
    }

    private static bool Consume(ref ReadOnlySpan<string> syntax, scoped ReadOnlySpan<string> expected)
    {
        if (!syntax.StartsWith(expected)) return false;
        syntax = syntax[expected.Length..];
        return true;
    }

    private static bool TryReadAddress(string value, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IPAddress? address)
    {
        if (!IPAddress.TryParse(value, out address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // IPAddress.TryParse 也接受旧式缩写、八进制和十六进制 IPv4。
            if (address.ToString() != value) return false;
        }
        else if (value.AsSpan().IndexOfAny("%[]") >= 0)
        {
            return false;
        }

        return IsPublicAddress(address);
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => !ReservedV4.Any(network => network.Contains(address)),
            AddressFamily.InterNetworkV6 => address.ScopeId == 0 && GlobalV6.Contains(address) &&
                !ReservedV6.Any(network => network.Contains(address)),
            _ => false,
        };
    }
}

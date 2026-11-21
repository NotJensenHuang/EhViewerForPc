using System.Net;
using EhViewerForPc.Core.Online;

namespace EhViewerForPc.Core.Tests;

public sealed class UpstreamHostsParserTests
{
    private const string Source = """
        public class EhHosts implements Dns {
            private static final Map<String, List<InetAddress>> builtInHosts;
            static {
                Map < String , List < InetAddress > > map = new HashMap <> ();
                if (Settings.getBuiltInHosts()) {
                    // put(map, "e-hentai.org", "127.0.0.1");
                    put(map, "e-hentai.org", "104.20.18.168", /* "127.0.0.1", */ "104.20.18.168", "172.66.140.62");
                    put(map, "forums.e-hentai.org", "172.66.132.196");
                    put(map, "upld.e-hentai.org", "89.149.221.236");
                    put(map, "ehgt.org", "2a00:7c80:0:123::3a85", "185.132.132.79");
                    put(map, "repo.e-hentai.org", "104.20.18.168");
                    put(map, "raw.githubusercontent.com", "151.101.0.133");
                }
                if (Settings.getBuiltEXHosts()) {
                    put(map, "exhentai.org", "178.175.128.251");
                    put(map, "upld.exhentai.org", "178.175.132.22");
                    put(map, "s.exhentai.org", "178.175.129.253");
                }
                builtInHosts = map;
            }
            private static final String Sample = "put(map, \"e-hentai.org\", \"127.0.0.1\");";
        }
        """;

    [Fact]
    public void ReadsCompleteSiteTableWithoutCommentsDuplicatesOrThirdPartyDomains()
    {
        Assert.True(UpstreamHostsParser.TryParse(Source, out var hosts));
        Assert.Equal(7, hosts.Count);
        Assert.Equal([IPAddress.Parse("104.20.18.168"), IPAddress.Parse("172.66.140.62")], hosts["e-hentai.org"]);
        Assert.Equal([IPAddress.Parse("2a00:7c80:0:123::3a85"), IPAddress.Parse("185.132.132.79")], hosts["ehgt.org"]);
        Assert.False(hosts.ContainsKey("repo.e-hentai.org"));
        Assert.False(hosts.ContainsKey("raw.githubusercontent.com"));
    }

    [Theory]
    [InlineData("put(map, \"s.exhentai.org\", \"178.175.129.253\");", "")]
    [InlineData("put(map, \"s.exhentai.org\", \"178.175.129.253\");", "// put(map, \"s.exhentai.org\", \"178.175.129.253\");")]
    [InlineData("put(map, \"s.exhentai.org\", \"178.175.129.253\");", "put(map, \"s.exhentai.org\");")]
    [InlineData("put(map, \"s.exhentai.org\", \"178.175.129.253\");", "put(map, \"s.exhentai.org\", \"178.175.129.253\"); put(map, \"S.EXHENTAI.ORG\", \"178.175.129.254\");")]
    [InlineData("\"178.175.129.253\"", "SomeVariable")]
    [InlineData("\"178.175.129.253\"", "\"178.175.\" + \"129.253\"")]
    [InlineData("\"178.175.129.253\"", "\"\\u0031\\u0037\\u0038.175.129.253\"")]
    [InlineData("builtInHosts = map;", "map.clear(); builtInHosts = map;")]
    [InlineData("builtInHosts = map;", "builtInHosts = anotherMap;")]
    public void RejectsPartialOrUnexpectedInitializers(string original, string replacement)
    {
        string changed = Source.Replace(original, replacement, StringComparison.Ordinal);

        Assert.NotEqual(Source, changed);
        Assert.False(UpstreamHostsParser.TryParse(changed, out var hosts));
        Assert.Empty(hosts);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("0x08080808")]
    [InlineData("010.8.8.8")]
    [InlineData("8.8")]
    [InlineData("dns.example.org")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("3fff::1")]
    [InlineData("2a00:7c80::1%0")]
    [InlineData("[2a00:7c80::1]")]
    public void RejectsAnythingOtherThanPublicIpLiterals(string address)
    {
        string changed = Source.Replace("178.175.129.253", address, StringComparison.Ordinal);

        Assert.False(UpstreamHostsParser.TryParse(changed, out var hosts));
        Assert.Empty(hosts);
    }

    [Fact]
    public void BoundsSourceAndAddressCount()
    {
        Assert.False(UpstreamHostsParser.TryParse(Source + new string(' ', UpstreamHostsParser.MaxSourceLength), out _));
        string tooMany = string.Join(", ", Enumerable.Repeat("\"178.175.129.253\"", 33));

        Assert.False(UpstreamHostsParser.TryParse(Source.Replace("\"178.175.129.253\"", tooMany), out _));
    }
}

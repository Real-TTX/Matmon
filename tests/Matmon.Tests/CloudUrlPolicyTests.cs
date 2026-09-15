using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>Plain-http is allowed only to a clearly-local host; a public http URL is refused so the provision
/// password / claim round-trip can't travel unencrypted. HTTPS is always allowed.</summary>
public sealed class CloudUrlPolicyTests
{
    [Theory]
    [InlineData("https://cloud.matmon.eu")]          // https public - fine
    [InlineData("https://cloud.example.com:8443")]   // https public with port - fine
    [InlineData("http://localhost:8055")]            // loopback name
    [InlineData("http://127.0.0.1:8055")]            // loopback ip
    [InlineData("http://[::1]:8055")]                // ipv6 loopback
    [InlineData("http://matmon-cloud:8055")]         // dotless docker alias
    [InlineData("http://nas.local")]                 // mDNS .local
    [InlineData("http://192.168.1.10:8055")]         // RFC1918
    [InlineData("http://10.0.0.5")]                  // RFC1918
    [InlineData("http://172.16.4.4")]                // RFC1918 (172.16/12)
    [InlineData("http://169.254.1.2")]               // link-local
    public void Allowed(string url)
    {
        Assert.True(CloudUrlPolicy.IsPlainHttpAllowed(new Uri(url)));
    }

    [Theory]
    [InlineData("http://cloud.matmon.eu")]           // public FQDN over http - refused
    [InlineData("http://cloud.example.com:8055")]    // public FQDN with port - refused
    [InlineData("http://8.8.8.8")]                   // public ip - refused
    [InlineData("http://172.32.0.1")]                // just outside 172.16/12 - refused
    public void Refused(string url)
    {
        Assert.False(CloudUrlPolicy.IsPlainHttpAllowed(new Uri(url)));
    }
}

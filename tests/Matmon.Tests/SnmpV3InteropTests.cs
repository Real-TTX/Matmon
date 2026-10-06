using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>
/// Interoperability against a real Net-SNMP agent - the only way to know the key extension and MAC truncation
/// match what devices do. Skipped unless MATMON_SNMPV3_TEST="host:port" points at an snmpd configured with the
/// users below (all with auth password "authpass123" and privacy password "privpass123"), e.g.
///   createUser usha512 SHA-512 authpass123 AES-256 privpass123   +   rouser usha512 priv
/// </summary>
public class SnmpV3InteropTests
{
    [Theory]
    [InlineData("umd5des", "md5", "des")]
    [InlineData("usha1aes", "sha1", "aes128")]
    [InlineData("usha224", "sha224", "aes128")]
    [InlineData("usha256", "sha256", "aes192")]
    [InlineData("usha384", "sha384", "aes256")]
    [InlineData("usha512", "sha512", "aes256")]
    [InlineData("usha1aes192", "sha1", "aes192")]
    [InlineData("usha1aes256", "sha1", "aes256")]
    [InlineData("usha1aes192c", "sha1", "aes192c")]
    [InlineData("usha1aes256c", "sha1", "aes256c")]
    [InlineData("usha512noprv", "sha512", "none")]
    public async Task AgentAnswersEveryAlgorithm(string user, string auth, string priv)
    {
        var endpoint = Environment.GetEnvironmentVariable("MATMON_SNMPV3_TEST");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return;
        }

        var (host, port) = (endpoint.Split(':')[0], endpoint.Split(':')[1]);
        var settings = new MonitoringSettings();
        settings.Parameters["snmp.version"] = "v3";
        settings.Parameters["snmp.port"] = port;
        settings.Parameters["snmp.v3.username"] = user;
        settings.Parameters["snmp.v3.authProtocol"] = auth;
        settings.Parameters["snmp.v3.authPassword"] = "authpass123";
        settings.Parameters["snmp.v3.privProtocol"] = priv;
        settings.Parameters["snmp.v3.privPassword"] = "privpass123";

        var items = await SnmpSensorExecutor.DiscoverAsync(host, settings, "1.3.6.1.2.1.1", TimeSpan.FromSeconds(5));

        Assert.Contains(items, item => item.Value.Contains("matmon-v3-test", StringComparison.Ordinal));
    }
}

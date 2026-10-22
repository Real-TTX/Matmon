using System.Diagnostics;
using Matmon.Core.SnmpTraps;
using Matmon.Core.Syslog;
using Matmon.Host.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// SNMPv3 traps sent by Net-SNMP's own <c>snmptrap</c> (in a Docker container) against the real receiver - the check
/// that the USM code reads what a real device writes, not only what our own encoder produces. Runs only when
/// MATMON_SNMPV3_TRAP_INTEROP=1 and Docker is available (it pulls alpine and installs net-snmp-tools).
/// </summary>
public sealed class SnmpV3TrapInteropTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-v3interop-" + Guid.NewGuid().ToString("N"));

    public SnmpV3TrapInteropTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public async Task Net_snmp_v3_traps_are_accepted_for_every_security_level()
    {
        if (Environment.GetEnvironmentVariable("MATMON_SNMPV3_TRAP_INTEROP") != "1")
        {
            return;
        }

        using var store = new SyslogStore(Path.Combine(_dir, "log.db"), NullLogger<SyslogStore>.Instance);
        var state = new SnmpTrapReceiverState();
        var ingest = new SnmpTrapIngest(store, state, NullLogger<SnmpTrapIngest>.Instance);
        SnmpTrapV3User[] users =
        [
            new() { Username = "authonly", AuthProtocol = "sha256", AuthPassword = "auth-secret-1" },
            new() { Username = "authpriv", AuthProtocol = "sha1", AuthPassword = "auth-secret-1", PrivProtocol = "aes128", PrivPassword = "priv-secret-1" },
            new() { Username = "authdes", AuthProtocol = "md5", AuthPassword = "auth-secret-1", PrivProtocol = "des", PrivPassword = "priv-secret-1" }
        ];
        ingest.Configure(IpAllowList.Parse(null, out _), [], pollOnTrap: false, users);
        using var listener = SnmpTrapUdpListener.Start(0, ingest, NullLogger.Instance);

        var target = $"host.docker.internal:{listener.Port}";
        const string engine = "0x80001F8880AABBCCDDEEFF0011";
        string[] commands =
        [
            $"snmptrap -v 3 -e {engine} -u authonly -l authNoPriv -a SHA-256 -A auth-secret-1 {target} '' 1.3.6.1.2.1.33.2.1 1.3.6.1.2.1.33.1.2.3.0 i 11",
            $"snmptrap -v 3 -e {engine} -u authpriv -l authPriv -a SHA -A auth-secret-1 -x AES -X priv-secret-1 {target} '' 1.3.6.1.2.1.33.2.1 1.3.6.1.2.1.33.1.2.3.0 i 12",
            $"snmptrap -v 3 -e {engine} -u authdes -l authPriv -a MD5 -A auth-secret-1 -x DES -X priv-secret-1 {target} '' 1.3.6.1.2.1.33.2.1 1.3.6.1.2.1.33.1.2.3.0 i 13",
            // A wrong password must not get in.
            $"snmptrap -v 3 -e {engine} -u authpriv -l authPriv -a SHA -A wrong-password -x AES -X priv-secret-1 {target} '' 1.3.6.1.2.1.33.2.1 1.3.6.1.2.1.33.1.2.3.0 i 14"
        ];
        // Every trap is sent three times (UDP): duplicates are fine, a lost datagram is not.
        var script = "apk add --no-cache net-snmp-tools >/dev/null && for i in 1 2 3; do " + string.Join(" && ", commands) + " && sleep 1; done";
        var docker = Process.Start(new ProcessStartInfo("docker", ["run", "--rm", "alpine:3", "sh", "-c", script])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        var stderr = await docker.StandardError.ReadToEndAsync();
        await docker.WaitForExitAsync();
        Assert.True(docker.ExitCode == 0, stderr);

        await Task.Delay(500);
        await store.FlushAsync();
        var messages = store.Search(new SyslogQuery { Contains = "SNMP trap" }, 50);
        foreach (var value in new[] { "= 11", "= 12", "= 13" })
        {
            Assert.Contains(messages, message => message.Message.Contains(value, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(messages, message => message.Message.Contains("= 14", StringComparison.Ordinal));
        Assert.Contains("failed authentication", state.Snapshot().Warning);
    }
}

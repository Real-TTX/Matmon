using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host;
using Matmon.Host.Services;
using Matmon.Probe;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// The small open points of the review of 2026-10-25: a probe batch sent twice was stored twice, a probe's wrong clock
/// went straight into the history, the forwarded headers were believed from anyone, and the cloud's own backup flags
/// (encrypted, version) were ignored.
/// </summary>
public sealed class ProbeExchangeHardeningTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("matmon-exchange-").FullName;
    private readonly SqliteTelemetryRepository _telemetry;
    private readonly InMemoryMonitoringWorkspaceStore _workspace;

    public ProbeExchangeHardeningTests()
    {
        _telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "telemetry.db"));
        _workspace = new InMemoryMonitoringWorkspaceStore(
            new ExchangeHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "workspace.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            _telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
    }

    private Guid RootId => _workspace.GetAllElements().OfType<ProbeElement>().First(probe => probe.ParentId is null).Id;

    [Fact]
    public void A_batch_the_probe_sends_again_is_recorded_once()
    {
        var host = _workspace.CreateHost(RootId, "nas", "10.0.0.5", null);
        var ping = _workspace.CreateSensor(host.Id, "NAS ping", "ping", string.Empty, null);
        var at = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var result = SensorExecutionResult.Healthy(TimeSpan.FromMilliseconds(4), "ok", 4);

        Assert.True(_workspace.RecordProbeObservation(ping.Id, result, at, TimeSpan.Zero, null, "probe-1", "Probe 1"));
        Assert.False(_workspace.RecordProbeObservation(ping.Id, result, at, TimeSpan.Zero, null, "probe-1", "Probe 1"));
        // A clock-corrected stamp moves by the network time between two sends - recognised within the tolerance.
        Assert.False(_workspace.RecordProbeObservation(ping.Id, result, at.AddMilliseconds(800), TimeSpan.FromSeconds(2), null, "probe-1", "Probe 1"));
        // The next real check is not a repeat.
        Assert.True(_workspace.RecordProbeObservation(ping.Id, result, at.AddSeconds(30), TimeSpan.Zero, null, "probe-1", "Probe 1"));

        Assert.Equal(2, _workspace.GetSensorHistory(ping.Id).Count);
    }

    [Fact]
    public void A_probe_clock_within_half_a_minute_is_left_alone()
    {
        var now = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, ProbeClock.Offset(now.AddSeconds(-20), now));
        Assert.Equal(TimeSpan.Zero, ProbeClock.Offset(null, now));
        Assert.Equal(now.AddSeconds(-50), ProbeClock.Correct(now.AddSeconds(-50), TimeSpan.Zero, now));
    }

    [Fact]
    public void A_probe_clock_that_is_off_moves_its_stamps_and_never_into_the_future()
    {
        var now = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

        // The probe runs an hour fast: a check it stamped 12:59:30 happened at 11:59:30.
        var fast = ProbeClock.Offset(now.AddHours(1), now);
        Assert.Equal(TimeSpan.FromHours(-1), fast);
        Assert.Equal(now.AddSeconds(-30), ProbeClock.Correct(now.AddHours(1).AddSeconds(-30), fast, now));

        // The probe runs a day slow: a buffered check from its "yesterday" is really from today.
        var slow = ProbeClock.Offset(now.AddDays(-1), now);
        Assert.Equal(now.AddMinutes(-10), ProbeClock.Correct(now.AddDays(-1).AddMinutes(-10), slow, now));

        // An older probe that sends no clock still cannot put a result in the future.
        Assert.Equal(now, ProbeClock.Correct(now.AddHours(3), TimeSpan.Zero, now));
    }

    [Fact]
    public void Only_local_and_private_senders_may_name_the_client_by_default()
    {
        var networks = TrustedProxies.Resolve(null, out var ignored);

        Assert.NotNull(networks);
        Assert.Empty(ignored);
        Assert.Contains(networks, network => network.Contains(System.Net.IPAddress.Parse("172.18.0.4")));
        Assert.Contains(networks, network => network.Contains(System.Net.IPAddress.Parse("127.0.0.1")));
        Assert.DoesNotContain(networks, network => network.Contains(System.Net.IPAddress.Parse("203.0.113.9")));
    }

    [Fact]
    public void A_configured_proxy_list_replaces_the_default_and_any_trusts_everyone()
    {
        var networks = TrustedProxies.Resolve("203.0.113.9; 198.51.100.0/24, not-an-address", out var ignored);

        Assert.NotNull(networks);
        Assert.Equal(["not-an-address"], ignored);
        Assert.Contains(networks, network => network.Contains(System.Net.IPAddress.Parse("203.0.113.9")));
        Assert.Contains(networks, network => network.Contains(System.Net.IPAddress.Parse("198.51.100.77")));
        Assert.DoesNotContain(networks, network => network.Contains(System.Net.IPAddress.Parse("10.0.0.1")));

        Assert.Null(TrustedProxies.Resolve("any", out _));
    }

    [Fact]
    public void The_cloud_says_whether_a_backup_is_encrypted_and_the_label_is_only_the_fallback()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(new CloudBackupClient.CloudBackupItem(Guid.NewGuid(), now, "Nightly 2026-10-25 02:00", "nightly-1", 10, Encrypted: true).IsEncrypted);
        Assert.True(new CloudBackupClient.CloudAccountBackupItem(Guid.NewGuid(), Guid.NewGuid(), "nas", now, "Config (encrypted)", "", 10).IsEncrypted);
        Assert.False(new CloudBackupClient.CloudBackupItem(Guid.NewGuid(), now, "Config", "", 10).IsEncrypted);
    }

    public void Dispose()
    {
        _workspace.Dispose();
        _telemetry.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

file sealed class ExchangeHostEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

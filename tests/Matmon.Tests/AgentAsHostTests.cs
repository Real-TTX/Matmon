using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// An agent IS a host - the machine it is installed on (<see cref="HostElement.Agent"/>) - not a probe. Its sensors live under
/// the host and run on the agent; where the host sits says which probe the agent talks through (a remote probe relays it, see
/// <see cref="AgentRelay"/>). Migration 12 turns every agent that used to be a probe into its host, keeping its id and token
/// so the installed agent carries on.
/// </summary>
public sealed class AgentAsHostTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("matmon-agenthost-").FullName;
    private readonly SqliteTelemetryRepository _telemetry;
    private readonly InMemoryMonitoringWorkspaceStore _store;

    public AgentAsHostTests()
    {
        _telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "telemetry.db"));
        _store = NewStore(Path.Combine(_dir, "workspace.json"), _telemetry);
    }

    private InMemoryMonitoringWorkspaceStore NewStore(string workspacePath, ITelemetryRepository telemetry) =>
        new(new AgentAsHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = workspacePath },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);

    private Guid RootId => _store.GetAllElements().OfType<ProbeElement>().First(probe => probe.ParentId is null).Id;

    private AgentEnrollmentRedemption Enrol(string machine, Guid? parentId = null, Guid? hostId = null, string? operatingSystem = "Microsoft Windows 10.0.26200")
    {
        var issue = _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), "admin", parentId, hostId);
        return _store.RedeemAgentEnrollment(issue.Code, machine, operatingSystem).Redemption!;
    }

    private HostElement HostOf(AgentEnrollmentRedemption redeemed) => (HostElement)_store.FindElement(redeemed.ElementId)!;

    private IReadOnlyList<SensorElement> SensorsUnder(Guid parentId) =>
        _store.GetAllElements().OfType<SensorElement>().Where(sensor => sensor.ParentId == parentId).ToArray();

    [Fact]
    public void A_new_agent_is_a_host_named_after_its_machine_and_no_probe_is_created()
    {
        var probesBefore = _store.GetAllElements().OfType<ProbeElement>().Count();

        var redeemed = Enrol("FILESRV-01");

        var host = HostOf(redeemed);
        Assert.Equal("FILESRV-01", host.Name);
        Assert.Equal("FILESRV-01", host.Address);
        Assert.Equal(RootId, host.ParentId);
        Assert.Equal(redeemed.ProbeId, host.Agent!.ProbeId);
        Assert.True(_store.TryValidateProbe(redeemed.ProbeId, redeemed.ProbeToken));
        Assert.False(_store.TryValidateProbe(redeemed.ProbeId, "wrong"));
        Assert.Equal(probesBefore, _store.GetAllElements().OfType<ProbeElement>().Count());

        // It measures its machine and reports in: System Health, Windows Update, its connection sensor.
        var types = SensorsUnder(host.Id).Select(sensor => sensor.SensorTypeKey).ToArray();
        Assert.Contains("local-health", types);
        Assert.Contains("windows-update", types);
        Assert.Contains("probe-heartbeat", types);
    }

    [Fact]
    public void An_agent_under_a_remote_probe_talks_through_it_and_runs_its_own_sensors()
    {
        var branch = _store.CreateProbe(RootId, "Branch", null);
        var issue = _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), "admin", branch.Id);
        Assert.Equal(branch.Id, _store.FindAgentRelayProbe(issue.Enrollment)!.Id);

        var redeemed = _store.RedeemAgentEnrollment(issue.Code, "PC-07", "Ubuntu 24.04").Redemption!;
        var host = HostOf(redeemed);
        Assert.Equal(branch.Id, host.ParentId);

        var ping = _store.CreateSensor(host.Id, "Ping self", "ping", "localhost", null);
        var branchPing = _store.CreateSensor(branch.Id, "Router", "ping", "10.0.0.1", null);

        // On-demand runs and assignments go to the agent, not to the probe it sits under.
        Assert.Equal(redeemed.ProbeId, _store.ResolveOwningRemoteProbeId(ping.Id));
        Assert.Equal(branch.ProbeId, _store.ResolveOwningRemoteProbeId(branchPing.Id));

        var provider = new ProbeSensorAssignmentProvider(_store);
        var agentWork = provider.BuildAssignments(redeemed.ProbeId).Sensors.Select(sensor => sensor.SensorId).ToArray();
        var branchWork = provider.BuildAssignments(branch.ProbeId).Sensors.Select(sensor => sensor.SensorId).ToArray();
        Assert.Contains(ping.Id, agentWork);
        Assert.DoesNotContain(ping.Id, branchWork);
        Assert.Contains(branchPing.Id, branchWork);
        Assert.DoesNotContain(branchPing.Id, agentWork);
        // The connection sensor reads the primary's picture of the agent: it never goes to the agent itself.
        Assert.DoesNotContain(SensorsUnder(host.Id).Single(sensor => sensor.SensorTypeKey == "probe-heartbeat").Id, agentWork);

        Assert.True(provider.TryBuildRecordingContext(redeemed.ProbeId, ping.Id, out var runner, out _, out _));
        Assert.Equal(host.Name, runner.Name);
        Assert.False(provider.TryBuildRecordingContext(branch.ProbeId, ping.Id, out _, out _, out _));
    }

    [Fact]
    public void Sensors_of_an_agent_on_the_primary_are_not_polled_by_the_primary()
    {
        var host = HostOf(Enrol("WS-01"));
        var ping = _store.CreateSensor(host.Id, "Ping", "ping", "localhost", null);
        var elements = _store.GetAllElements().ToDictionary(element => element.Id);

        Assert.False(SensorPollingService.IsLocalSensor(elements[ping.Id], elements));
        Assert.True(SensorPollingService.IsLocalSensor(elements[SensorsUnder(host.Id).Single(sensor => sensor.SensorTypeKey == "probe-heartbeat").Id], elements));
    }

    [Fact]
    public void An_existing_host_becomes_the_agent_where_it_is()
    {
        var folder = _store.CreateFolder(RootId, "Servers", null);
        var existing = _store.CreateHost(folder.Id, "NAS", "10.0.0.20", null);
        var disk = _store.CreateSensor(existing.Id, "Disk", "windows-disk", "", null);

        var redeemed = Enrol("nas-01", hostId: existing.Id);

        Assert.Equal(existing.Id, redeemed.ElementId);
        var host = HostOf(redeemed);
        Assert.Equal(folder.Id, host.ParentId);
        Assert.Equal("10.0.0.20", host.Address);
        Assert.Equal(redeemed.ProbeId, _store.ResolveOwningRemoteProbeId(disk.Id));
    }

    [Fact]
    public void An_agent_moves_to_another_host_with_its_machine_sensors_and_keeps_its_identity()
    {
        var redeemed = Enrol("old-name");
        var old = HostOf(redeemed);
        var admins = _store.CreateSensor(old.Id, "Admin check", "ping", "10.0.0.1", null);
        var target = _store.CreateHost(RootId, "Real machine", "10.0.0.30", null);

        Assert.Null(_store.RebindAgentHost(old.Id, target.Id));

        var moved = (HostElement)_store.FindElement(target.Id)!;
        Assert.Equal(redeemed.ProbeId, moved.Agent!.ProbeId);
        Assert.Null(((HostElement)_store.FindElement(old.Id)!).Agent);
        Assert.True(_store.TryValidateProbe(redeemed.ProbeId, redeemed.ProbeToken));
        var movedTypes = SensorsUnder(target.Id).Select(sensor => sensor.SensorTypeKey).ToArray();
        Assert.Contains("local-health", movedTypes);
        Assert.Contains("probe-heartbeat", movedTypes);
        Assert.Equal(old.Id, _store.FindElement(admins.Id)!.ParentId);

        Assert.Contains("already runs on", _store.RebindAgentHost(target.Id, target.Id));
        Assert.Contains("already has an agent", _store.RebindAgentHost(HostOf(Enrol("other")).Id, target.Id));
        Assert.NotNull(_store.RebindAgentHost(Guid.NewGuid(), old.Id));
    }

    [Fact]
    public void The_identity_survives_a_restart()
    {
        var redeemed = Enrol("WS-02");
        _store.Dispose();

        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "telemetry2.db"));
        using var reopened = NewStore(Path.Combine(_dir, "workspace.json"), telemetry);
        Assert.True(reopened.TryValidateProbe(redeemed.ProbeId, redeemed.ProbeToken));
        Assert.IsType<HostElement>(reopened.FindRunnerByProbeId(redeemed.ProbeId));
    }

    // ---- migration 12: an agent probe becomes its host ---------------------------------------------------------------

    private const string LegacyWorkspace = """
        {
          "schemaVersion": 11,
          "rootProbe": {
            "$kind": "probe", "probeId": "primary", "id": "11111111-1111-1111-1111-111111111111", "name": "Primary Probe",
            "children": [
              {
                "$kind": "probe", "probeId": "old-agent", "enrollmentToken": "secret-1", "id": "22222222-2222-2222-2222-222222222222", "name": "Old agent",
                "parentId": "11111111-1111-1111-1111-111111111111", "agentEnrolledUtc": "2026-09-01T10:00:00+00:00",
                "boundHostId": "99999999-9999-9999-9999-999999999999",
                "children": [
                  {
                    "$kind": "host", "id": "99999999-9999-9999-9999-999999999999", "parentId": "22222222-2222-2222-2222-222222222222", "name": "WS-OLD", "address": "localhost",
                    "children": [
                      { "$kind": "sensor", "id": "33333333-3333-3333-3333-333333333333", "parentId": "99999999-9999-9999-9999-999999999999", "name": "System Health", "sensorTypeKey": "local-health", "target": "" }
                    ]
                  },
                  {
                    "$kind": "host", "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "parentId": "22222222-2222-2222-2222-222222222222", "name": "Printer", "address": "10.0.0.50",
                    "children": []
                  },
                  { "$kind": "sensor", "id": "88888888-8888-8888-8888-888888888888", "parentId": "22222222-2222-2222-2222-222222222222", "name": "Probe Heartbeat", "sensorTypeKey": "probe-heartbeat", "target": "old-agent" },
                  { "$kind": "sensor", "id": "55555555-5555-5555-5555-555555555555", "parentId": "22222222-2222-2222-2222-222222222222", "name": "Custom check", "sensorTypeKey": "ping", "target": "10.0.0.1" }
                ]
              },
              {
                "$kind": "probe", "probeId": "docker-probe", "enrollmentToken": "secret-2", "id": "66666666-6666-6666-6666-666666666666", "name": "Docker probe",
                "parentId": "11111111-1111-1111-1111-111111111111",
                "children": [
                  { "$kind": "sensor", "id": "77777777-7777-7777-7777-777777777777", "parentId": "66666666-6666-6666-6666-666666666666", "name": "System Health", "sensorTypeKey": "local-health", "target": "" }
                ]
              }
            ]
          },
          "notificationRules": [
            { "id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "name": "Old agent down", "targetElementId": "22222222-2222-2222-2222-222222222222" }
          ]
        }
        """;

    [Fact]
    public void Migration_12_turns_an_agent_probe_into_its_host_and_the_agent_carries_on()
    {
        var path = Path.Combine(_dir, "legacy.json");
        File.WriteAllText(path, LegacyWorkspace);

        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "legacy.db"));
        using var store = NewStore(path, telemetry);
        var elements = store.GetAllElements();
        var root = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var hostId = Guid.Parse("99999999-9999-9999-9999-999999999999");

        // The probe is gone; its host is the agent, where the probe was, with the same id and token.
        Assert.Null(store.FindElement(Guid.Parse("22222222-2222-2222-2222-222222222222")));
        var host = Assert.IsType<HostElement>(store.FindElement(hostId));
        Assert.Equal(root, host.ParentId);
        Assert.Equal("old-agent", host.Agent!.ProbeId);
        Assert.True(store.TryValidateProbe("old-agent", "secret-1"));

        // The probe's own sensors went into the host, another device moved up beside it.
        Assert.Equal(hostId, elements.Single(element => element.Id == Guid.Parse("33333333-3333-3333-3333-333333333333")).ParentId);
        Assert.Equal(hostId, elements.Single(element => element.Id == Guid.Parse("88888888-8888-8888-8888-888888888888")).ParentId);
        Assert.Equal(hostId, elements.Single(element => element.Id == Guid.Parse("55555555-5555-5555-5555-555555555555")).ParentId);
        Assert.Equal(root, elements.Single(element => element.Id == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")).ParentId);

        // What pointed at the probe points at the host; a real probe is left alone.
        Assert.Equal(hostId, store.FindNotificationRule(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))!.TargetElementId);
        Assert.True(store.TryValidateProbe("docker-probe", "secret-2"));
        Assert.Equal(Guid.Parse("66666666-6666-6666-6666-666666666666"), elements.Single(element => element.Id == Guid.Parse("77777777-7777-7777-7777-777777777777")).ParentId);
    }

    [Fact]
    public void An_agent_from_before_host_binding_gets_a_host_through_migrations_6_and_12()
    {
        var path = Path.Combine(_dir, "legacy-old.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 5,
              "rootProbe": {
                "$kind": "probe", "probeId": "primary", "id": "11111111-1111-1111-1111-111111111111", "name": "Primary Probe",
                "children": [
                  {
                    "$kind": "probe", "probeId": "very-old", "enrollmentToken": "secret-3", "id": "22222222-2222-2222-2222-222222222222", "name": "Very old",
                    "parentId": "11111111-1111-1111-1111-111111111111", "agentEnrolledUtc": "2026-09-01T10:00:00+00:00",
                    "children": [
                      { "$kind": "sensor", "id": "33333333-3333-3333-3333-333333333333", "parentId": "22222222-2222-2222-2222-222222222222", "name": "System Health", "sensorTypeKey": "local-health", "target": "" }
                    ]
                  }
                ]
              }
            }
            """);

        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "legacy-old.db"));
        using var store = NewStore(path, telemetry);

        var host = Assert.IsType<HostElement>(store.FindRunnerByProbeId("very-old"));
        Assert.Equal("Very old", host.Name);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), host.ParentId);
        Assert.Equal(host.Id, store.FindElement(Guid.Parse("33333333-3333-3333-3333-333333333333"))!.ParentId);
        Assert.True(store.TryValidateProbe("very-old", "secret-3"));
        Assert.DoesNotContain(store.GetAllElements().OfType<ProbeElement>(), probe => probe.ProbeId == "very-old");
    }

    // ---- the relay: a remote probe passes its agents on ---------------------------------------------------------------

    [Fact]
    public void The_primary_believes_a_relay_only_with_a_valid_probe_token()
    {
        var branch = _store.CreateProbe(RootId, "Branch", null);
        var agent = Enrol("PC-01", branch.Id);

        string? Read(string probeId, string token, out string? origin)
        {
            var http = new DefaultHttpContext();
            http.Request.Headers[AgentRelay.ProbeHeader] = probeId;
            http.Request.Headers[AgentRelay.TokenHeader] = token;
            http.Request.Headers[AgentRelay.OriginHeader] = "http://10.0.0.5:8099";
            return AgentRelay.ReadRelay(http.Request, _store, out origin);
        }

        Assert.Equal(branch.ProbeId, Read(branch.ProbeId, branch.EnrollmentToken!, out var origin));
        Assert.Equal("http://10.0.0.5:8099", origin);
        Assert.Null(Read(branch.ProbeId, "wrong", out _));
        // An agent is no relay, even with its own valid token.
        Assert.Null(Read(agent.ProbeId, agent.ProbeToken, out _));
        Assert.Null(AgentRelay.ReadRelay(new DefaultHttpContext().Request, _store, out _));
    }

    [Theory]
    [InlineData("/api/probes/heartbeat", true)]
    [InlineData("/api/probes/pc-01/assignments", true)]
    [InlineData("/api/agents/enroll", true)]
    [InlineData("/api/agent/packages/win-x64", true)]
    [InlineData("/api/agent/install.sh", true)]
    [InlineData("/api/probe/status", false)]
    [InlineData("/probe", false)]
    [InlineData("/healthz", false)]
    public void A_probe_relays_exactly_what_an_agent_calls(string path, bool relayed) =>
        Assert.Equal(relayed, AgentRelay.IsAgentPath(new PathString(path)));

    public void Dispose()
    {
        _store.Dispose();
        _telemetry.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A SQLite handle can linger on Windows; temp cleanup must not fail the run.
        }
    }
}

file sealed class AgentAsHostEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

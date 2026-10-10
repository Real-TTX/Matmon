using System.Text.Json;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// An agent belongs to a HOST - the machine it sits on - not just to a probe with loose sensors underneath it. The
/// probe is the agent's connection to the instance; the machine is a host inside it, and the probe says which one
/// (<see cref="ProbeElement.BoundHostId"/>). The admin can name an existing host when issuing the code, so a machine
/// that is already in the tree does not turn up a second time.
/// </summary>
public sealed class AgentHostBindingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("matmon-agenthost-").FullName;
    private readonly SqliteTelemetryRepository _telemetry;
    private readonly InMemoryMonitoringWorkspaceStore _store;

    public AgentHostBindingTests()
    {
        _telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "telemetry.db"));
        _store = NewStore(Path.Combine(_dir, "workspace.json"), _telemetry);
    }

    private InMemoryMonitoringWorkspaceStore NewStore(string workspacePath, ITelemetryRepository telemetry) =>
        new(new AgentHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = workspacePath },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);

    private Guid RootId => _store.GetAllElements().OfType<ProbeElement>().First(probe => probe.ParentId is null).Id;

    private AgentEnrollmentRedemption Enrol(string? name, string machine, Guid? hostElementId = null, string? operatingSystem = "Microsoft Windows 10.0.26200")
    {
        var issue = _store.CreateAgentEnrollment(name, TimeSpan.FromHours(1), "admin", hostElementId: hostElementId);
        return _store.RedeemAgentEnrollment(issue.Code, machine, allowNewProbe: true, operatingSystem).Redemption!;
    }

    private ProbeElement ProbeOf(AgentEnrollmentRedemption redeemed) => (ProbeElement)_store.FindElement(redeemed.ElementId)!;

    private IReadOnlyList<T> Children<T>(Guid parentId) where T : MonitoringElement =>
        _store.GetAllElements().OfType<T>().Where(element => element.ParentId == parentId).ToArray();

    private HostElement ExistingHost(string name, string address)
    {
        var folder = _store.CreateFolder(RootId, "Servers", null);
        return _store.CreateHost(folder.Id, name, address, null);
    }

    // ---- a new agent brings its machine -----------------------------------------------------------------------------

    [Fact]
    public void ANewAgentIsBoundToAHostNamedAfterItsMachine()
    {
        var redeemed = Enrol(name: null, machine: "WS-01");
        var probe = ProbeOf(redeemed);

        var host = probe.FindBoundHost();
        Assert.NotNull(host);
        Assert.Equal("WS-01", host.Name);
        Assert.Equal("WS-01", host.Address);
        Assert.Equal(probe.Id, host.ParentId);
        Assert.Equal(probe.BoundHostId, host.Id);
    }

    [Fact]
    public void TheMachinesSensorsLiveUnderTheHostNotLooseUnderTheProbe()
    {
        var redeemed = Enrol(null, "WS-01");
        var probe = ProbeOf(redeemed);
        var host = probe.FindBoundHost()!;

        var underHost = Children<SensorElement>(host.Id);
        Assert.Contains(underHost, sensor => sensor.SensorTypeKey == "local-health");
        Assert.Contains(underHost, sensor => sensor.SensorTypeKey == "windows-update");

        // What stays at probe level is the probe's own business: its heartbeat.
        var underProbe = Children<SensorElement>(probe.Id).Select(sensor => sensor.SensorTypeKey).ToArray();
        Assert.DoesNotContain("local-health", underProbe);
        Assert.DoesNotContain("windows-update", underProbe);
        Assert.Contains("probe-heartbeat", underProbe);
    }

    [Fact]
    public void TheUpdateSensorTargetsThisMachineExplicitly()
    {
        // With no target a sensor inherits its host's address - and a host that was chosen by its IP address is not
        // recognised as "this machine", so the sensor would go out over WinRM to itself. "localhost" runs here.
        var redeemed = Enrol(null, "WS-01");
        var host = ProbeOf(redeemed).FindBoundHost()!;

        var update = Children<SensorElement>(host.Id).Single(sensor => sensor.SensorTypeKey == "windows-update");
        var health = Children<SensorElement>(host.Id).Single(sensor => sensor.SensorTypeKey == "local-health");
        Assert.Equal("localhost", update.Target);
        Assert.Equal(string.Empty, health.Target);
    }

    [Fact]
    public void WithoutAReportedMachineNameTheHostIsCalledAfterTheProbeAndAddressedAsLocalhost()
    {
        var redeemed = Enrol("Backup box", machine: "");
        var host = ProbeOf(redeemed).FindBoundHost()!;

        Assert.Equal("Backup box", host.Name);
        Assert.Equal("localhost", host.Address);
    }

    // ---- binding to a host that is already in the tree --------------------------------------------------------------

    [Fact]
    public void ACodeCanNameAnExistingHostAndThatHostMovesUnderTheAgentWithItsSensors()
    {
        var host = ExistingHost("SRV-01", "192.168.1.20");
        var ping = _store.CreateSensor(host.Id, "Ping", "ping", "192.168.1.20", null);
        var issue = _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), "admin", hostElementId: host.Id);

        Assert.Equal(host.Id, issue.Enrollment.HostElementId);
        Assert.Equal("SRV-01", issue.Enrollment.Name); // the agent's probe is named after the machine it is for

        var redeemed = _store.RedeemAgentEnrollment(issue.Code, "SRV-01-LIVE", allowNewProbe: true, "Microsoft Windows 10.0.26200").Redemption!;
        var probe = ProbeOf(redeemed);

        // The same host - same id, same address, same sensors - now inside the agent's probe.
        Assert.Equal(host.Id, probe.BoundHostId);
        var moved = Assert.IsType<HostElement>(_store.FindElement(host.Id));
        Assert.Equal(probe.Id, moved.ParentId);
        Assert.Equal("192.168.1.20", moved.Address);
        Assert.Equal(host.Id, _store.FindElement(ping.Id)!.ParentId);

        // No second host for the same machine.
        Assert.Single(_store.GetAllElements().OfType<HostElement>());

        // And the agent's own sensors went onto it.
        Assert.Contains(Children<SensorElement>(host.Id), sensor => sensor.SensorTypeKey == "local-health");
    }

    [Fact]
    public void AHostThatAlreadyHasAnAgentCannotBeChosenAgain()
    {
        var host = ExistingHost("SRV-01", "192.168.1.20");
        var redeemed = Enrol(null, "SRV-01", host.Id);

        var error = Assert.Throws<InvalidOperationException>(() =>
            _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), "admin", hostElementId: host.Id));
        Assert.Contains("already has an agent", error.Message);

        // The agent that has it can still be re-enrolled onto it - that is the same agent coming back.
        var again = _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), "admin", redeemed.ElementId, host.Id);
        Assert.Equal(host.Id, again.Enrollment.HostElementId);
    }

    [Fact]
    public void OnlyAnExistingHostCanBeNamedWhenTheCodeIsIssued()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), null, hostElementId: Guid.NewGuid()));

        var folder = _store.CreateFolder(RootId, "Servers", null);
        Assert.Throws<InvalidOperationException>(() =>
            _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), null, hostElementId: folder.Id));
        Assert.Throws<InvalidOperationException>(() =>
            _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), null, hostElementId: RootId));

        Assert.Empty(_store.GetPendingAgentEnrollments());
    }

    [Fact]
    public void AHostThatVanishesBeforeTheCodeIsUsedDoesNotStopTheEnrolment()
    {
        var host = ExistingHost("SRV-01", "192.168.1.20");
        var issue = _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), "admin", hostElementId: host.Id);
        Assert.True(_store.DeleteElement(host.Id));

        var redeemed = _store.RedeemAgentEnrollment(issue.Code, "SRV-01-LIVE", allowNewProbe: true).Redemption;

        Assert.NotNull(redeemed);
        var bound = ProbeOf(redeemed).FindBoundHost();
        Assert.NotNull(bound);
        Assert.NotEqual(host.Id, bound.Id);
        Assert.Equal("SRV-01-LIVE", bound.Name);
    }

    [Fact]
    public void AHostAnotherAgentTookInTheMeantimeFallsBackToAHostOfItsOwn()
    {
        var host = ExistingHost("SRV-01", "192.168.1.20");
        var first = _store.CreateAgentEnrollment("first", TimeSpan.FromHours(1), null, hostElementId: host.Id);
        var second = _store.CreateAgentEnrollment("second", TimeSpan.FromHours(1), null, hostElementId: host.Id);

        var one = _store.RedeemAgentEnrollment(first.Code, "A", allowNewProbe: true).Redemption!;
        var two = _store.RedeemAgentEnrollment(second.Code, "B", allowNewProbe: true).Redemption!;

        Assert.Equal(host.Id, ProbeOf(one).BoundHostId);
        var other = ProbeOf(two).FindBoundHost();
        Assert.NotNull(other);
        Assert.NotEqual(host.Id, other.Id);
        Assert.Equal("B", other.Name);
    }

    // ---- re-enrolment -----------------------------------------------------------------------------------------------

    [Fact]
    public void AReenrolmentKeepsTheHostTheAgentIsBoundTo()
    {
        var redeemed = Enrol(null, "WS-01");
        var hostId = ProbeOf(redeemed).BoundHostId;
        var again = _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), null, redeemed.ElementId);

        var back = _store.RedeemAgentEnrollment(again.Code, "WS-01-RENAMED", allowNewProbe: false).Redemption!;

        Assert.True(back.Reenrolled);
        Assert.Equal(hostId, ProbeOf(back).BoundHostId);
        Assert.Single(Children<HostElement>(redeemed.ElementId));
        Assert.Single(Children<SensorElement>(hostId!.Value), sensor => sensor.SensorTypeKey == "local-health");
    }

    [Fact]
    public void ADockerProbeMovingOntoAnAgentGetsAHostForItsMachineAndKeepsTheRest()
    {
        var docker = _store.CreateProbe(RootId, "Docker probe 01", null);
        var switchHost = _store.CreateHost(docker.Id, "Switch-01", "10.0.0.2", null);
        var issue = _store.CreateAgentEnrollment(null, TimeSpan.FromHours(1), "admin", docker.Id);

        var redeemed = _store.RedeemAgentEnrollment(issue.Code, "NUC-01", allowNewProbe: false, "Ubuntu 24.04.1 LTS").Redemption!;
        var probe = ProbeOf(redeemed);

        var machine = probe.FindBoundHost();
        Assert.NotNull(machine);
        Assert.NotEqual(switchHost.Id, machine.Id);
        Assert.Equal("NUC-01", machine.Name);
        Assert.Contains(Children<SensorElement>(machine.Id), sensor => sensor.SensorTypeKey == "linux-update");

        // What the probe monitored before is still there, and is not its machine.
        Assert.Equal(docker.Id, _store.FindElement(switchHost.Id)!.ParentId);
    }

    // ---- the binding only counts while it is true -----------------------------------------------------------------------

    [Fact]
    public void AHostMovedOutOfTheAgentIsNoLongerItsBoundHost()
    {
        var redeemed = Enrol(null, "WS-01");
        var probe = ProbeOf(redeemed);
        var hostId = probe.BoundHostId!.Value;

        Assert.True(_store.MoveElement(hostId, RootId));

        Assert.Null(ProbeOf(redeemed).FindBoundHost());
    }

    [Fact]
    public void ADeletedHostLeavesTheAgentUnbound()
    {
        var redeemed = Enrol(null, "WS-01");

        Assert.True(_store.DeleteElement(ProbeOf(redeemed).BoundHostId!.Value));

        Assert.Null(ProbeOf(redeemed).FindBoundHost());
    }

    [Fact]
    public void TheBindingSurvivesCloneAndARestart()
    {
        var redeemed = Enrol(null, "WS-01");
        var probe = ProbeOf(redeemed);
        var clone = Assert.IsType<ProbeElement>(probe.Clone());
        Assert.Equal(probe.BoundHostId, clone.BoundHostId);
        Assert.NotNull(clone.FindBoundHost());

        _store.Dispose();
        using var reopened = NewStore(Path.Combine(_dir, "workspace.json"), _telemetry);
        var again = Assert.IsType<ProbeElement>(reopened.FindElement(redeemed.ElementId));
        Assert.Equal(probe.BoundHostId, again.BoundHostId);
        Assert.NotNull(again.FindBoundHost());
    }

    // ---- migration 6: agents from before this existed ---------------------------------------------------------------

    private const string LegacyWorkspace = """
        {
          "schemaVersion": 5,
          "rootProbe": {
            "$kind": "probe", "probeId": "primary", "id": "11111111-1111-1111-1111-111111111111", "name": "Primary Probe",
            "children": [
              {
                "$kind": "probe", "probeId": "old-agent", "id": "22222222-2222-2222-2222-222222222222", "name": "Old agent",
                "parentId": "11111111-1111-1111-1111-111111111111", "agentEnrolledUtc": "2026-09-01T10:00:00+00:00",
                "children": [
                  { "$kind": "sensor", "id": "33333333-3333-3333-3333-333333333333", "parentId": "22222222-2222-2222-2222-222222222222", "name": "System Health", "sensorTypeKey": "local-health", "target": "" },
                  { "$kind": "sensor", "id": "44444444-4444-4444-4444-444444444444", "parentId": "22222222-2222-2222-2222-222222222222", "name": "Windows Update", "sensorTypeKey": "windows-update", "target": "" },
                  { "$kind": "sensor", "id": "55555555-5555-5555-5555-555555555555", "parentId": "22222222-2222-2222-2222-222222222222", "name": "Custom check", "sensorTypeKey": "ping", "target": "10.0.0.1" },
                  { "$kind": "sensor", "id": "88888888-8888-8888-8888-888888888888", "parentId": "22222222-2222-2222-2222-222222222222", "name": "Probe Heartbeat", "sensorTypeKey": "probe-heartbeat", "target": "old-agent" }
                ]
              },
              {
                "$kind": "probe", "probeId": "docker-probe", "id": "66666666-6666-6666-6666-666666666666", "name": "Docker probe",
                "parentId": "11111111-1111-1111-1111-111111111111",
                "children": [
                  { "$kind": "sensor", "id": "77777777-7777-7777-7777-777777777777", "parentId": "66666666-6666-6666-6666-666666666666", "name": "System Health", "sensorTypeKey": "local-health", "target": "" }
                ]
              }
            ]
          }
        }
        """;

    [Fact]
    public void AnAgentFromBeforeBindingGetsAHostAndItsMachineSensorsMoveIntoIt()
    {
        var path = Path.Combine(_dir, "legacy.json");
        File.WriteAllText(path, LegacyWorkspace);

        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "legacy.db"));
        using var store = NewStore(path, telemetry);
        var elements = store.GetAllElements();
        var agent = elements.OfType<ProbeElement>().Single(probe => probe.ProbeId == "old-agent");

        var host = agent.FindBoundHost();
        Assert.NotNull(host);
        Assert.Equal("Old agent", host.Name);
        Assert.Equal("localhost", host.Address);

        // The two machine sensors moved - same ids - and the admin's own check and the heartbeat stayed on the probe.
        Assert.Equal(host.Id, elements.Single(element => element.Id == Guid.Parse("33333333-3333-3333-3333-333333333333")).ParentId);
        Assert.Equal(host.Id, elements.Single(element => element.Id == Guid.Parse("44444444-4444-4444-4444-444444444444")).ParentId);
        Assert.Equal(agent.Id, elements.Single(element => element.Id == Guid.Parse("55555555-5555-5555-5555-555555555555")).ParentId);
        Assert.Equal(agent.Id, elements.Single(element => element.Id == Guid.Parse("88888888-8888-8888-8888-888888888888")).ParentId);
    }

    [Fact]
    public void AProbeThatIsNotAnAgentIsLeftAlone()
    {
        var path = Path.Combine(_dir, "legacy2.json");
        File.WriteAllText(path, LegacyWorkspace);

        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "legacy2.db"));
        using var store = NewStore(path, telemetry);
        var elements = store.GetAllElements();
        var docker = elements.OfType<ProbeElement>().Single(probe => probe.ProbeId == "docker-probe");

        Assert.Null(docker.BoundHostId);
        Assert.DoesNotContain(elements.OfType<HostElement>(), host => host.ParentId == docker.Id);
        Assert.Equal(docker.Id, elements.Single(element => element.Id == Guid.Parse("77777777-7777-7777-7777-777777777777")).ParentId);
    }

    [Fact]
    public void TheMigrationRunsOnceSoARestartDoesNotAddAnotherHost()
    {
        var path = Path.Combine(_dir, "legacy3.json");
        File.WriteAllText(path, LegacyWorkspace);
        var dbPath = Path.Combine(_dir, "legacy3.db");

        using (var telemetry = new SqliteTelemetryRepository(dbPath))
        using (var first = NewStore(path, telemetry))
        {
            Assert.Single(first.GetAllElements().OfType<HostElement>());
        }

        using (var telemetry = new SqliteTelemetryRepository(dbPath))
        using (var second = NewStore(path, telemetry))
        {
            Assert.Single(second.GetAllElements().OfType<HostElement>());
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(InMemoryMonitoringWorkspaceStore.CurrentSchemaVersion, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

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

file sealed class AgentHostEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

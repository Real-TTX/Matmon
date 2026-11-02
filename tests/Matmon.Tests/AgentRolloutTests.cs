using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// Rollout keys: one secret for many machines (an MSI pushed by Intune / a GPO, a script). A machine that enrols with one
/// gets its identity at once but is only accepted once an admin approves it; a machine that enrols again is recognised by
/// its machine id and keeps its host.
/// </summary>
public sealed class AgentRolloutTests : IDisposable
{
    private const string MachineA = "0123456789ABCDEF0123456789ABCDEF";
    private const string MachineB = "FEDCBA9876543210FEDCBA9876543210";

    private readonly string _dir = Directory.CreateTempSubdirectory("matmon-rollout-").FullName;
    private readonly SqliteTelemetryRepository _telemetry;
    private readonly InMemoryMonitoringWorkspaceStore _store;

    public AgentRolloutTests()
    {
        _telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "telemetry.db"));
        _store = new InMemoryMonitoringWorkspaceStore(new RolloutEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "workspace.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            _telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
    }

    private Guid RootId => _store.GetAllElements().OfType<ProbeElement>().First(probe => probe.ParentId is null).Id;

    private PendingAgent Enrol(string secret, string machine, string? machineId = MachineA) =>
        _store.EnrollWithRolloutKey(secret, machine, "Microsoft Windows 10.0.26200", "nightly-1", machineId, "10.0.0.5")!;

    [Fact]
    public void A_key_is_prefixed_base62_and_only_its_hash_is_kept()
    {
        var secret = AgentRolloutKeySecret.Generate();
        Assert.StartsWith("mmr_", secret);
        Assert.True(AgentRolloutKeySecret.IsKey(secret));
        Assert.False(AgentRolloutKeySecret.IsKey("K7QM-4TXC-9RHA-B2NW-P6FE"));
        Assert.False(AgentRolloutKeySecret.IsKey(secret + "'"));
        Assert.Equal(64, AgentRolloutKeySecret.Hash(secret).Length);
        Assert.Equal(string.Empty, AgentRolloutKeySecret.Hash("mmr_short"));

        var issue = _store.CreateAgentRolloutKey("Office", null, null, null, "admin");
        Assert.DoesNotContain(issue.Secret, File.Exists(Path.Combine(_dir, "workspace.json")) ? File.ReadAllText(Path.Combine(_dir, "workspace.json")) : string.Empty);
        Assert.Equal(AgentRolloutKeySecret.Hash(issue.Secret), issue.Key.KeyHash);
    }

    [Fact]
    public void A_machine_waits_until_approved_and_then_becomes_a_host_with_the_identity_it_already_holds()
    {
        var folder = _store.CreateFolder(RootId, "Office PCs", null);
        var key = _store.CreateAgentRolloutKey("Office", folder.Id, null, null, "admin");

        var pending = Enrol(key.Secret, "PC-0815");

        // Not accepted yet: no host, the identity does not open anything - but the heartbeat can say why.
        Assert.False(_store.TryValidateProbe(pending.ProbeId, pending.Token));
        Assert.True(_store.IsPendingAgent(pending.ProbeId, pending.Token));
        Assert.False(_store.IsPendingAgent(pending.ProbeId, "wrong"));
        Assert.DoesNotContain(_store.GetAllElements().OfType<HostElement>(), host => host.Name == "PC-0815");
        Assert.Single(_store.GetPendingAgents());

        var approval = _store.ApprovePendingAgent(pending.Id);

        Assert.Null(approval.Problem);
        var host = (HostElement)_store.FindElement(approval.HostId!.Value)!;
        Assert.Equal("PC-0815", host.Name);
        Assert.Equal(folder.Id, host.ParentId);
        Assert.Equal(pending.ProbeId, host.Agent!.ProbeId);
        Assert.Equal(MachineA, host.Agent.MachineId);
        Assert.True(_store.TryValidateProbe(pending.ProbeId, pending.Token));
        Assert.False(_store.IsPendingAgent(pending.ProbeId, pending.Token));
        Assert.Empty(_store.GetPendingAgents());
        var types = _store.GetAllElements().OfType<SensorElement>().Where(sensor => sensor.ParentId == host.Id).Select(sensor => sensor.SensorTypeKey).ToArray();
        Assert.Contains("local-health", types);
        Assert.Contains("windows-update", types);
        Assert.Contains("probe-heartbeat", types);
    }

    [Fact]
    public void The_same_machine_again_replaces_its_request_and_after_approval_re_enrols_its_host()
    {
        var key = _store.CreateAgentRolloutKey("Office", null, null, 1, "admin");

        var first = Enrol(key.Secret, "PC-0815");
        var again = Enrol(key.Secret, "PC-0815");

        // One request, one counted machine - although the key allows only one.
        Assert.Single(_store.GetPendingAgents());
        Assert.NotEqual(first.Token, again.Token);
        Assert.Equal(1, _store.GetAgentRolloutKeys().Single().DeviceCount);
        var hostId = _store.ApprovePendingAgent(again.Id).HostId!.Value;

        // Reinstalled later (the MSI pushed once more): offered as a re-install of the host, approved onto it.
        var reinstall = Enrol(key.Secret, "PC-0815");
        Assert.Equal(hostId, reinstall.ReplacesHostId);
        Assert.Equal(hostId, _store.ApprovePendingAgent(reinstall.Id).HostId);
        Assert.Single(_store.GetAllElements().OfType<HostElement>(), host => host.Name == "PC-0815");
        Assert.True(_store.TryValidateProbe(reinstall.ProbeId, reinstall.Token));
        Assert.False(_store.TryValidateProbe(again.ProbeId, again.Token));
    }

    [Fact]
    public void A_key_takes_no_more_machines_than_allowed_and_nothing_once_revoked_or_expired()
    {
        var key = _store.CreateAgentRolloutKey("Office", null, null, 1, "admin");
        Assert.NotNull(Enrol(key.Secret, "PC-1", MachineA));
        Assert.Null(_store.EnrollWithRolloutKey(key.Secret, "PC-2", null, null, MachineB, null));

        var other = _store.CreateAgentRolloutKey("Lab", null, null, null, "admin");
        Assert.True(_store.IsAgentRolloutKeyValid(other.Secret));
        Assert.True(_store.RevokeAgentRolloutKey(other.Key.Id));
        Assert.False(_store.IsAgentRolloutKeyValid(other.Secret));
        Assert.Null(_store.EnrollWithRolloutKey(other.Secret, "PC-3", null, null, MachineB, null));
        Assert.True(_store.DeleteAgentRolloutKey(other.Key.Id));

        Assert.Null(_store.EnrollWithRolloutKey("mmr_" + new string('A', 40), "PC-4", null, null, null, null));
        Assert.Throws<InvalidOperationException>(() => _store.CreateAgentRolloutKey("Past", null, DateTimeOffset.UtcNow.AddDays(-1), null, "admin"));
        Assert.Throws<InvalidOperationException>(() => _store.CreateAgentRolloutKey(" ", null, null, null, "admin"));
    }

    [Fact]
    public void A_rejected_machine_stays_locked_out()
    {
        var key = _store.CreateAgentRolloutKey("Office", null, null, null, "admin");
        var pending = Enrol(key.Secret, "PC-0815");

        Assert.True(_store.RejectPendingAgent(pending.Id));

        Assert.False(_store.IsPendingAgent(pending.ProbeId, pending.Token));
        Assert.False(_store.TryValidateProbe(pending.ProbeId, pending.Token));
        Assert.NotNull(_store.ApprovePendingAgent(pending.Id).Problem);
    }

    [Fact]
    public void Keys_and_waiting_machines_never_leave_the_instance_in_a_backup()
    {
        var key = _store.CreateAgentRolloutKey("Office", null, null, null, "admin");
        var pending = Enrol(key.Secret, "PC-0815");

        var package = System.Text.Encoding.UTF8.GetString(_store.CreateBackupBytes(WorkspaceBackupSection.All));

        Assert.DoesNotContain(key.Key.KeyHash, package);
        Assert.DoesNotContain(pending.Token, package);
    }

    [Fact]
    public void An_install_script_for_a_key_keeps_the_identity_of_machines_that_have_the_agent()
    {
        var secret = AgentRolloutKeySecret.Generate();
        var hash = new string('a', 64);

        var windows = AgentInstallScripts.PowerShell("https://matmon.example", secret, hash);
        var unix = AgentInstallScripts.Shell("https://matmon.example", secret, new Dictionary<string, string?> { ["linux-x64"] = hash });

        Assert.Contains($"$MatmonCode = '{secret}'", windows);
        Assert.Contains("--keep-identity", windows);
        Assert.Contains("ROLLOUT KEY", windows);
        Assert.Contains($"MATMON_CODE='{secret}'", unix);
        Assert.Contains("--keep-identity", unix);

        // A single-use code keeps the old script: no keep-identity.
        Assert.DoesNotContain("--keep-identity", AgentInstallScripts.Shell("https://matmon.example", "K7QM-4TXC-9RHA-B2NW-P6FE", new Dictionary<string, string?>()));
    }

    [Fact]
    public void A_machine_id_is_hashed_and_only_a_clean_one_is_accepted()
    {
        var id = AgentMachineId.FromRaw("6F1B2C3D-0000-4000-8000-ABCDEFABCDEF");
        Assert.NotNull(id);
        Assert.Equal(32, id!.Length);
        Assert.Equal(id, AgentMachineId.FromRaw(" 6f1b2c3d-0000-4000-8000-abcdefabcdef "));
        Assert.Equal(id, AgentMachineId.Clean(id.ToLowerInvariant()));
        Assert.Null(AgentMachineId.Clean("not-a-machine-id"));
        Assert.Null(AgentMachineId.FromRaw(""));
    }

    public void Dispose()
    {
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

file sealed class RolloutEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

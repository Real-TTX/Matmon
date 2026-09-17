using System.Text.Json;
using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>A standalone (non-clustered) PVE host reports no <c>quorate</c> field on <c>/cluster/status</c>, so the
/// cluster-scope Proxmox Health sensor must NOT treat it as "not quorate" (which used to flip the sensor to a
/// false Critical "cluster is not quorate" on a perfectly healthy single host). Quorum only means something on a
/// real cluster.</summary>
public class ProxmoxClusterQuorumTests
{
    private static bool Read(string json, out bool quorate)
    {
        using var document = JsonDocument.Parse(json);
        return ProxmoxPveSensorExecutor.TryReadQuorate(document.RootElement, out quorate);
    }

    [Fact]
    public void Standalone_host_has_no_quorum_info()
    {
        // What a single, non-clustered node returns: just the node entry, no quorate field.
        var found = Read("""[ { "type": "node", "name": "pve", "local": 1, "online": 1, "nodeid": 0 } ]""", out _);

        Assert.False(found, "a standalone host reports no quorum info, so the sensor must not judge quorum at all");
    }

    [Fact]
    public void Empty_cluster_status_has_no_quorum_info()
    {
        Assert.False(Read("[]", out _));
    }

    [Fact]
    public void Real_cluster_reports_quorate()
    {
        var found = Read("""[ { "type": "cluster", "name": "prod", "quorate": 1, "nodes": 3 }, { "type": "node", "name": "pve1" } ]""", out var quorate);

        Assert.True(found);
        Assert.True(quorate);
    }

    [Fact]
    public void Real_cluster_that_lost_quorum_reports_not_quorate()
    {
        var found = Read("""[ { "type": "cluster", "name": "prod", "quorate": 0, "nodes": 3 } ]""", out var quorate);

        Assert.True(found, "a real cluster does carry quorum info");
        Assert.False(quorate, "quorate=0 must be surfaced so a genuinely split cluster still alarms");
    }
}

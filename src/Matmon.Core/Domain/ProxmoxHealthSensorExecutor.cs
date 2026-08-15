namespace Matmon.Core.Domain;

/// <summary>
/// Cluster-wide Proxmox health: quorum, nodes online and VM / container / storage rollups.
/// Thin wrapper over <see cref="ProxmoxPveSensorExecutor"/> with the scope fixed to the cluster
/// view, so it reuses the same REST/auth plumbing and channel building.
/// </summary>
public sealed class ProxmoxHealthSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new SensorDefinition
    {
        Key = "proxmox-health",
        DisplayName = "Proxmox Health",
        Description = "Cluster-wide Proxmox health via the REST API: quorum, nodes online, VM / container / storage rollups, plus a dynamic per-VM/CT channel set (up + CPU% + RAM%) across the cluster.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters = ProxmoxPveSensorExecutor.Definition.Parameters
            .Where(parameter => !string.Equals(parameter.Key, "pve.scope", StringComparison.OrdinalIgnoreCase))
            .ToArray()
    };

    public string SensorTypeKey => Definition.Key;

    private static readonly ProxmoxPveSensorExecutor Inner = new();

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = context;
        _ = cancellationToken;
        return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
    }

    public ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var settings = context.Settings.Clone();
        settings.Parameters["pve.scope"] = "cluster";
        return Inner.ExecuteAsync(
            new SensorExecutionContext(ProxmoxPveSensorExecutor.Definition.Key, context.Target, settings),
            cancellationToken);
    }
}

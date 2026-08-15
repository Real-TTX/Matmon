namespace Matmon.Core.Domain;

/// <summary>
/// Per-node Proxmox health: CPU / memory / disk usage plus VM and container online / offline
/// counts. Thin wrapper over <see cref="ProxmoxPveSensorExecutor"/> with the scope fixed to the
/// node view. A <c>pve.node</c> may be given; otherwise the first visible node is used.
/// </summary>
public sealed class ProxmoxNodeHealthSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new SensorDefinition
    {
        Key = "proxmox-node-health",
        DisplayName = "Proxmox Node Health",
        Description = "Per-node Proxmox health via the REST API: CPU, memory and disk usage, VM / container online / offline counts, plus a dynamic per-VM/CT channel set (up + CPU% + RAM%) for the guests on the node. Uses the first visible node when none is given.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters = BuildParameters()
    };

    public string SensorTypeKey => Definition.Key;

    private static readonly ProxmoxPveSensorExecutor Inner = new();

    private static IReadOnlyList<SensorParameterDefinition> BuildParameters()
    {
        var nodeParameter = new SensorParameterDefinition
        {
            Key = "pve.node",
            Label = "Node",
            Kind = SensorParameterKind.Text,
            Description = "Node name to monitor. Leave empty to use the first visible node.",
            Placeholder = "pve"
        };

        return new[] { nodeParameter }
            .Concat(ProxmoxPveSensorExecutor.Definition.Parameters
                .Where(parameter => !string.Equals(parameter.Key, "pve.scope", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

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
        settings.Parameters["pve.scope"] = "node";
        return Inner.ExecuteAsync(
            new SensorExecutionContext(ProxmoxPveSensorExecutor.Definition.Key, context.Target, settings),
            cancellationToken);
    }
}

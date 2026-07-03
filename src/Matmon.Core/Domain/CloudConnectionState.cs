namespace Matmon.Core.Domain;

/// <summary>
/// Runtime status of this instance's link to Matmon.Cloud (for display/diagnostics). The connection
/// credentials themselves come from configuration (<c>Matmon__CloudUrl</c> / <c>CloudInstanceId</c> /
/// <c>CloudInstanceToken</c>); this records the last heartbeat outcome. Persisted in the workspace.
/// </summary>
public sealed class CloudConnectionState
{
    public Guid? InstanceId { get; set; }

    public string? CloudUrl { get; set; }

    public DateTimeOffset? LastHeartbeatUtc { get; set; }

    /// <summary>Last outcome: "ok", "unauthorized", "not configured", or "failed: …".</summary>
    public string? LastStatus { get; set; }

    public CloudConnectionState Clone() => new()
    {
        InstanceId = InstanceId,
        CloudUrl = CloudUrl,
        LastHeartbeatUtc = LastHeartbeatUtc,
        LastStatus = LastStatus
    };
}

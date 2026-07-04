using System.Text.Json.Serialization;

namespace Matmon.Core.Domain;

/// <summary>
/// UI-managed configuration of this instance's link to Matmon.Cloud. The user connects/disconnects
/// from System → Cloud; the values persist in the workspace (the token encrypted at rest), so no
/// environment variables or restart are needed. Environment variables (<c>Matmon__CloudUrl</c> …) are
/// only a first-run bootstrap: once <see cref="Configured"/> is set, the UI settings win.
/// </summary>
public sealed class CloudConnectionSettings
{
    /// <summary>Base URL of the Matmon.Cloud control plane.</summary>
    public string? Url { get; set; }

    /// <summary>The instance id issued by Matmon.Cloud.</summary>
    public string? InstanceId { get; set; }

    /// <summary>The instance token, DataProtection-encrypted (never the plaintext).</summary>
    public string? ProtectedToken { get; set; }

    /// <summary>Whether the link is active. Disconnect sets this false.</summary>
    public bool Enabled { get; set; }

    /// <summary>True once the user has connected/disconnected via the UI — from then on the UI wins over env.</summary>
    public bool Configured { get; set; }

    /// <summary>Relay raised/recovered alerts to the Matmon.Cloud notification gateway (which delivers them).</summary>
    public bool RelayAlerts { get; set; }

    /// <summary>Comma/semicolon-separated recipients the cloud gateway delivers relayed alerts to.</summary>
    public string? RelayRecipients { get; set; }

    /// <summary>Whether a token is stored (for display; not persisted).</summary>
    [JsonIgnore]
    public bool HasToken => !string.IsNullOrEmpty(ProtectedToken);

    public CloudConnectionSettings Clone() => new()
    {
        Url = Url,
        InstanceId = InstanceId,
        ProtectedToken = ProtectedToken,
        Enabled = Enabled,
        Configured = Configured,
        RelayAlerts = RelayAlerts,
        RelayRecipients = RelayRecipients
    };
}

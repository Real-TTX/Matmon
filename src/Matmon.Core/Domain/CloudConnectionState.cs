namespace Matmon.Core.Domain;

/// <summary>
/// Persisted credentials for this instance's link to Matmon.Cloud. The instance registers once and
/// keeps the returned token to authenticate heartbeats/notifications. Stored in the workspace document.
/// </summary>
public sealed class CloudConnectionState
{
    public Guid? InstanceId { get; set; }

    public string? Token { get; set; }

    public string? PublicToken { get; set; }

    /// <summary>The cloud base URL this instance registered against (to detect a changed target).</summary>
    public string? RegisteredUrl { get; set; }

    public bool IsRegistered => InstanceId is not null && !string.IsNullOrWhiteSpace(Token);

    public CloudConnectionState Clone() => new()
    {
        InstanceId = InstanceId,
        Token = Token,
        PublicToken = PublicToken,
        RegisteredUrl = RegisteredUrl
    };
}

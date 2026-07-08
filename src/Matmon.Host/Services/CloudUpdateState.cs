namespace Matmon.Host.Services;

/// <summary>
/// In-memory holder for the "a newer Matmon build is available" signal the cloud returns on each heartbeat.
/// The cloud knows the latest released version (from the executor) and compares it to the version this
/// instance reports, so the instance only has to display the result. Reset on restart; refreshed within a
/// minute by the next heartbeat. A no-op while the instance isn't linked to Matmon.Cloud.
/// </summary>
public sealed class CloudUpdateState
{
    private volatile bool _updateAvailable;
    private volatile string? _latestVersion;

    /// <summary>True when the cloud reports a newer build on the same channel is available.</summary>
    public bool UpdateAvailable => _updateAvailable;

    /// <summary>The latest version the cloud knows about (for display), or null when unknown.</summary>
    public string? LatestVersion => _latestVersion;

    public void Set(bool updateAvailable, string? latestVersion)
    {
        _updateAvailable = updateAvailable;
        _latestVersion = string.IsNullOrWhiteSpace(latestVersion) ? null : latestVersion.Trim();
    }
}

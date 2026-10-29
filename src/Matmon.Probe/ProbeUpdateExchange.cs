using System.Text.RegularExpressions;

namespace Matmon.Probe;

/// <summary>
/// What a Docker probe says about updating itself, on every heartbeat (<see cref="ProbeHeartbeatRequest.SelfUpdate"/>).
/// Null from an agent (it updates itself, see its UpdateStatus) and from a probe older than this.
/// </summary>
public sealed record ProbeSelfUpdateReport(
    /// <summary>Whether the probe can replace its own container (the Docker socket is mounted).</summary>
    bool CanUpdate,
    /// <summary>Why not, when it cannot ("the Docker socket is not mounted (/var/run/docker.sock)").</summary>
    string? UnavailableReason = null,
    /// <summary>An update is being downloaded or handed over right now.</summary>
    bool Installing = false,
    /// <summary>The version it is installing.</summary>
    string? InstallingTo = null,
    /// <summary>How the last update ended: "Updated", "RolledBack" or "Failed".</summary>
    string? LastOutcomeKind = null,
    string? LastOutcomeFrom = null,
    string? LastOutcomeTo = null,
    DateTimeOffset? LastOutcomeUtc = null,
    string? LastOutcomeDetail = null,
    /// <summary>The answer to the last update command: its id, whether it was taken, and why not.</summary>
    string? CommandId = null,
    bool? CommandAccepted = null,
    string? CommandMessage = null);

/// <summary>"Update to this version", handed to a probe with a heartbeat answer (an admin pressed Update on the Probes page).</summary>
public sealed record ProbeUpdateCommand(string Id, string Version, string? RequestedBy = null);

/// <summary>
/// The primary's answer to a heartbeat. An older probe ignores the body; an older primary returns another shape, in which
/// both fields are simply missing.
/// </summary>
public sealed record ProbeHeartbeatResponse(string? PrimaryVersion = null, ProbeUpdateCommand? InstallUpdate = null);

/// <summary>The probe side of updating a Docker probe from its primary. Registered by the Host in Secondary mode only.</summary>
public interface IProbeSelfUpdate
{
    ProbeSelfUpdateReport Report();

    /// <summary>Starts the command in the background; a command already handled is ignored (the primary repeats it
    /// until it sees the answer).</summary>
    void Handle(ProbeUpdateCommand command);
}

/// <summary>
/// The version rule: a Docker probe follows its PRIMARY's build, exactly - up or down, versions are build labels, not
/// something to order (the agent's rule). CI publishes every build under its own version tag beside latest / nightly,
/// so the probe pulls <c>&lt;its own repository&gt;:&lt;primary version&gt;</c>; a primary built locally has no such
/// tag and the probe falls back to the newest image of the tag it runs.
/// </summary>
public static partial class ProbeUpdatePolicy
{
    /// <summary>Whether the Probes page offers an update: the two versions are known and differ.</summary>
    public static bool ShouldOffer(string? primaryVersion, string? probeVersion) =>
        !string.IsNullOrWhiteSpace(primaryVersion)
        && !string.IsNullOrWhiteSpace(probeVersion)
        && !string.Equals(primaryVersion.Trim(), probeVersion.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>A version CI published as an image tag (not a local build).</summary>
    public static bool IsPublished(string? version) =>
        !string.IsNullOrWhiteSpace(version)
        && !version.StartsWith("local-", StringComparison.OrdinalIgnoreCase)
        && IsValidTag(version);

    /// <summary>The version arrives over the network and becomes an image tag: only what Docker allows in a tag.</summary>
    public static bool IsValidTag(string? value) => value is not null && TagPattern().IsMatch(value);

    /// <summary>Why a probe refuses a command, or null when it goes ahead.</summary>
    public static string? WhyNot(ProbeUpdateCommand command, string currentVersion, bool canUpdate, string? unavailableReason, bool installing)
    {
        if (!canUpdate)
        {
            return $"this probe cannot update itself: {unavailableReason ?? "no Docker socket"}";
        }

        if (installing)
        {
            return "an update is already being installed";
        }

        if (!IsValidTag(command.Version))
        {
            return "the requested version is not a valid image tag";
        }

        return string.Equals(command.Version, currentVersion, StringComparison.OrdinalIgnoreCase)
            ? $"this probe already runs {currentVersion}"
            : null;
    }

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex TagPattern();
}

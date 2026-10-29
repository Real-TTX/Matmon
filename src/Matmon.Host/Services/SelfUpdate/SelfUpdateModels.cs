namespace Matmon.Host.Services.SelfUpdate;

/// <summary>
/// How this installation can update itself. Decided by what it runs in, never configured: a container whose Docker
/// socket is mounted can replace its own container, a Windows service can swap its own program folder, anything else
/// is updated by whoever installed it.
/// </summary>
public enum SelfUpdateMode
{
    /// <summary>No way to update from inside - a plain source run, a container without the socket, a Linux install.</summary>
    Unavailable,

    /// <summary>A container with the Docker socket mounted: pull the new image, recreate the container, roll back on failure.</summary>
    Docker,

    /// <summary>A Windows service: download the signed package from the release feed, swap the program folder, roll back on failure.</summary>
    WindowsService
}

/// <summary>The admin's choices on System > Updates. Instance-bound - never carried in a backup.</summary>
public sealed record SelfUpdateSettings
{
    /// <summary>Install a found update on its own, inside the window below. Off by default: an update restarts the monitor.</summary>
    public bool AutoUpdate { get; init; }

    /// <summary>The hour (in the workspace's display time zone) the install window opens - 3 = 03:00.</summary>
    public int WindowStartHour { get; init; } = 3;

    /// <summary>The hour it closes (exclusive). Equal to the start = the whole day.</summary>
    public int WindowEndHour { get; init; } = 5;

    /// <summary>Matmon Cloud may start an install - the instance's owner or admins, its managing partner, a platform admin.
    /// On by default (the cloud can operate the instance through remote access anyway); an admin who wants every update
    /// started on the machine itself switches it off.</summary>
    public bool AllowCloudInstall { get; init; } = true;

    public SelfUpdateSettings Normalized() => this with
    {
        WindowStartHour = Math.Clamp(WindowStartHour, 0, 23),
        WindowEndHour = Math.Clamp(WindowEndHour, 0, 23)
    };
}

/// <summary>What a check found: an update to install (with what it is), or why there is none.</summary>
public sealed record SelfUpdateOffer(bool Available, string? Version, string Detail, string? Identity = null)
{
    /// <summary>What identifies the offered build for "this one failed before": an image id, or the package version.</summary>
    public string? Identity { get; init; } = Identity;

    /// <summary>Docker: the image to install when it is not the tag the container runs (a remote probe follows its
    /// primary's exact version tag). Null = the container's own tag.</summary>
    public string? ImageReference { get; init; }

    public static SelfUpdateOffer None(string detail) => new(false, null, detail);
}

public enum SelfUpdateOutcomeKind
{
    Updated,
    RolledBack,
    Failed
}

/// <summary>How the last update attempt ended - read back from where the updater left it (container labels, a file).</summary>
public sealed record SelfUpdateOutcome(
    SelfUpdateOutcomeKind Kind,
    string? FromVersion,
    string? ToVersion,
    DateTimeOffset AtUtc,
    string? Detail,
    string? Identity = null);

/// <summary>One installation method. Each knows how to tell whether it applies, find an update and hand over to its updater.</summary>
public interface ISelfUpdateTarget
{
    SelfUpdateMode Mode { get; }

    /// <summary>Whether this method applies here. <paramref name="reason"/> says why not (shown on System > Updates).</summary>
    bool IsAvailable(out string reason);

    Task<SelfUpdateOffer> CheckAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Hands over to the updater, which stops this process. Returns once the hand-over is done (or throws); everything
    /// after that is the updater's job, including the rollback.
    /// </summary>
    Task ApplyAsync(SelfUpdateOffer offer, CancellationToken cancellationToken);

    /// <summary>How the last update ended, or null when there was none to report.</summary>
    Task<SelfUpdateOutcome?> ReadLastOutcomeAsync(CancellationToken cancellationToken);
}

namespace Matmon.Host.Services.SelfUpdate;

/// <summary>
/// When an automatic update may go ahead - pure, so the rules are tested rather than discovered at three in the morning.
/// </summary>
public static class SelfUpdatePolicy
{
    /// <summary>After a rollback the same build is not tried again for this long: one that broke once breaks again, and
    /// an hourly update/rollback loop would take the monitor down every hour.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(24);

    /// <summary>Whether <paramref name="local"/> (a wall-clock time in the workspace zone) lies in the install window.
    /// A window may run past midnight (22-2); start = end means the whole day.</summary>
    public static bool IsInWindow(TimeOnly local, int startHour, int endHour)
    {
        if (startHour == endHour)
        {
            return true;
        }

        var hour = local.Hour;
        return startHour < endHour
            ? hour >= startHour && hour < endHour
            : hour >= startHour || hour < endHour;
    }

    /// <summary>Null when the update may be installed on its own now, else why not.</summary>
    public static string? WhyNotAutomatic(
        SelfUpdateSettings settings,
        SelfUpdateOffer offer,
        SelfUpdateOutcome? last,
        DateTimeOffset nowUtc,
        TimeZoneInfo zone)
    {
        if (!settings.AutoUpdate)
        {
            return "automatic updates are off";
        }

        if (!offer.Available)
        {
            return offer.Detail;
        }

        if (RecentlyFailed(offer, last, nowUtc) is { } failed)
        {
            return failed;
        }

        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime);
        return IsInWindow(local, settings.WindowStartHour, settings.WindowEndHour)
            ? null
            : $"waiting for the install window ({settings.WindowStartHour:00}:00-{settings.WindowEndHour:00}:00)";
    }

    /// <summary>Why this exact build is not tried again yet - it was rolled back or failed within the last day.</summary>
    public static string? RecentlyFailed(SelfUpdateOffer offer, SelfUpdateOutcome? last, DateTimeOffset nowUtc)
    {
        if (last is not { Kind: SelfUpdateOutcomeKind.RolledBack or SelfUpdateOutcomeKind.Failed })
        {
            return null;
        }

        var sameBuild = (offer.Identity is not null && string.Equals(offer.Identity, last.Identity, StringComparison.OrdinalIgnoreCase))
            || (offer.Version is not null && string.Equals(offer.Version, last.ToVersion, StringComparison.OrdinalIgnoreCase));
        if (!sameBuild || nowUtc - last.AtUtc >= RetryAfterFailure)
        {
            return null;
        }

        return $"{offer.Version ?? "this build"} was rolled back at {last.AtUtc:u}; it is tried again after {last.AtUtc + RetryAfterFailure:u}";
    }

    /// <summary>An unversioned local build labels itself with its build time - there is no release to follow.</summary>
    public static bool IsLocalBuild(string version) =>
        version.StartsWith("local-", StringComparison.OrdinalIgnoreCase);
}

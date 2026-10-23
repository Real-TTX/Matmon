namespace Matmon.Core.Domain;

/// <summary>
/// Dependencies, the PRTG way: a sensor depends on another sensor - a site's router ping, a host's ping - and while that
/// one is DOWN (Critical) a failing dependent is shown as Unknown ("&lt;dependency&gt; is down") and raises no alert. One
/// outage, one alert, instead of one for everything behind the router.
///
/// The setting is <see cref="MonitoringSettings.DependencySensorId"/>, set on any element and inherited down the tree. The
/// rules worth knowing, all here so the store and the editor cannot disagree:
/// <list type="bullet">
/// <item>The nearest element that says something wins; <see cref="Guid.Empty"/> there means "no dependency from here down".</item>
/// <item>A sensor never depends on itself: the router ping usually sits inside the very folder it guards, so it skips
/// its own id and keeps looking further up (to the site's uplink, say).</item>
/// <item>A chain that comes back to the sensor (A depends on B, B on A) is ignored for that sensor - otherwise a real
/// outage of both would hold both and alert nobody. The editor refuses to save one, this is the safety net for what
/// gets there anyway (a move, a restore).</item>
/// </list>
/// Pure - the store supplies the lineage and the lookups.
/// </summary>
public static class MonitoringDependencies
{
    /// <summary>The longest chain that is followed when looking for a loop. A real topology is a handful deep.</summary>
    public const int MaxDepth = 16;

    /// <summary>"No dependency from here down" - the value an element stores to switch an inherited dependency off.</summary>
    public static readonly Guid None = Guid.Empty;

    /// <summary>The dependency the settings name for the sensor at the END of <paramref name="lineage"/> (root first, the
    /// sensor last), or null. Does not check that the sensor exists or follow the chain - see <see cref="ResolveWithoutLoop"/>.</summary>
    public static Guid? Configured(IReadOnlyList<MonitoringElement> lineage, Guid sensorId)
    {
        for (var index = lineage.Count - 1; index >= 0; index--)
        {
            var value = lineage[index].Settings?.DependencySensorId;
            if (value is null)
            {
                continue;
            }

            if (value == None)
            {
                return null;
            }

            if (value == sensorId)
            {
                continue; // the dependency sits inside the folder it guards - it depends on whatever is above that
            }

            return value;
        }

        return null;
    }

    /// <summary>The dependency of <paramref name="sensorId"/> - null when it has none, or when following the chain
    /// (<paramref name="dependencyOf"/> = <see cref="Configured"/> for any sensor, null for one that does not exist) leads
    /// back to the sensor itself. A loop further down the chain that does not include this sensor does not matter here:
    /// the sensors in it ignore their own dependency, so this one may still depend on them.</summary>
    public static Guid? ResolveWithoutLoop(Guid sensorId, Func<Guid, Guid?> dependencyOf)
    {
        var first = dependencyOf(sensorId);
        if (first is null)
        {
            return null;
        }

        var seen = new HashSet<Guid> { sensorId };
        Guid? current = first;
        for (var depth = 0; current is { } id && depth < MaxDepth; depth++)
        {
            if (id == sensorId)
            {
                return null;
            }

            if (!seen.Add(id))
            {
                break; // a loop that does not include this sensor
            }

            current = dependencyOf(id);
        }

        return first;
    }

    /// <summary>Would making <paramref name="elementId"/>'s sensors depend on <paramref name="dependencyId"/> create a loop?
    /// True when the dependency's own chain reaches one of the sensors that would get it - that is, a sensor at or below
    /// the element (<paramref name="isAtOrBelow"/>). Used by the editor to refuse the setting with a reason.</summary>
    public static bool WouldLoop(Guid dependencyId, Func<Guid, bool> isAtOrBelow, Func<Guid, Guid?> dependencyOf)
    {
        var seen = new HashSet<Guid>();
        Guid? current = dependencyOf(dependencyId);
        for (var depth = 0; current is { } id && depth < MaxDepth; depth++)
        {
            if (id != dependencyId && isAtOrBelow(id))
            {
                return true;
            }

            if (!seen.Add(id))
            {
                return false;
            }

            current = dependencyOf(id);
        }

        return false;
    }

    /// <summary>How long a failing dependent waits for its dependency to be checked again before it alerts anyway. The
    /// dependency is polled at once when that happens (a router ping every 30 s and an HTTP check every 5 minutes cannot
    /// be relied on to notice in the right order), so this only matters when the dependency cannot be reached at all.</summary>
    public static readonly TimeSpan CheckWait = TimeSpan.FromMinutes(2);

    /// <summary>Should a dependent's new alert wait for its dependency to be checked? Yes while the dependency's latest
    /// result is older than the dependent's problem (it might be the router that is down, it just has not been asked yet),
    /// for at most <see cref="CheckWait"/>. The caller has already established that the dependency is not down - a down
    /// dependency holds the sensor outright.</summary>
    public static bool WaitForCheck(DateTimeOffset problemSince, DateTimeOffset? dependencyCheckedUtc, DateTimeOffset now) =>
        dependencyCheckedUtc is { } checkedUtc && checkedUtc < problemSince && now - problemSince < CheckWait;

    /// <summary>The message a held sensor shows instead of its own.</summary>
    public static string HeldMessage(string dependencyName, string? ownMessage) =>
        string.IsNullOrWhiteSpace(ownMessage)
            ? $"Dependency '{dependencyName}' is down - this sensor is not judged until it is up again."
            : $"Dependency '{dependencyName}' is down - this sensor is not judged until it is up again. (It said: {ownMessage.Trim()})";
}

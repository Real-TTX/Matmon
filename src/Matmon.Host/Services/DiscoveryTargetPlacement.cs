using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>
/// Where Discovery puts the hosts it creates. The target is the probe that ran the scan or a folder inside that probe's
/// OWN subtree - never a folder of a nested probe: the probe that scanned the network is the one that can reach the
/// devices, so it has to be the one that runs their sensors. No target (an older job, or nothing chosen) = the probe
/// itself, which is where Discovery always put them.
/// </summary>
public static class DiscoveryTargetPlacement
{
    /// <summary>The probe and every folder in its own subtree (depth-first, tree order), nested probes left out.</summary>
    public static IReadOnlyList<MonitoringContainerElement> Candidates(ProbeElement probe)
    {
        var candidates = new List<MonitoringContainerElement> { probe };
        Collect(probe, candidates);
        return candidates;
    }

    private static void Collect(MonitoringContainerElement container, List<MonitoringContainerElement> candidates)
    {
        foreach (var child in container.Children)
        {
            if (child is FolderElement folder)
            {
                candidates.Add(folder);
                Collect(folder, candidates);
            }
        }
    }

    /// <summary>
    /// Resolves the chosen target against the scan's probe. <paramref name="found"/> is the element the id names in
    /// the whole workspace (null when none), so a refusal can say WHY: gone, not a container for hosts, or under
    /// another probe.
    /// </summary>
    public static DiscoveryTargetResolution Resolve(ProbeElement probe, Guid? targetId, MonitoringElement? found)
    {
        if (targetId is not Guid id || id == Guid.Empty || id == probe.Id)
        {
            return DiscoveryTargetResolution.Ok(probe);
        }

        if (found is null)
        {
            return DiscoveryTargetResolution.Refused("The chosen target folder no longer exists. Choose another one.");
        }

        if (found is not FolderElement and not ProbeElement)
        {
            return DiscoveryTargetResolution.Refused(
                $"Hosts can only be placed in a probe or a folder - '{found.Name}' is a {found.Kind.ToString().ToLowerInvariant()}.");
        }

        var match = Candidates(probe).FirstOrDefault(candidate => candidate.Id == id);
        return match is null
            ? DiscoveryTargetResolution.Refused(
                $"'{found.Name}' is not inside probe '{probe.Name}'. Discovered hosts have to sit under the probe that scanned them, because that probe runs their sensors.")
            : DiscoveryTargetResolution.Ok(match);
    }
}

public sealed record DiscoveryTargetResolution(MonitoringContainerElement? Target, string? Problem)
{
    public bool IsOk => Target is not null;

    public static DiscoveryTargetResolution Ok(MonitoringContainerElement target) => new(target, null);

    public static DiscoveryTargetResolution Refused(string problem) => new(null, problem);
}

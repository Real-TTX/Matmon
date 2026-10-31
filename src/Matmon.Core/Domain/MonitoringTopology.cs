namespace Matmon.Core.Domain;

/// <summary>Shared tree-walk helpers for the monitoring element topology. Single source for the lineage and
/// descendant enumerations that were previously copy-pasted across page models and services.</summary>
public static class MonitoringTopology
{
    /// <summary>The element's ancestor chain root-first (root … parent, element). Stops at a missing parent,
    /// so a detached subtree still yields a usable partial lineage.</summary>
    public static IReadOnlyList<MonitoringElement> BuildLineage(
        MonitoringElement element,
        IReadOnlyDictionary<Guid, MonitoringElement> elementsById)
    {
        var lineage = new List<MonitoringElement> { element };
        var current = element;

        while (current.ParentId is Guid parentId && elementsById.TryGetValue(parentId, out var parent))
        {
            lineage.Add(parent);
            current = parent;
        }

        lineage.Reverse();
        return lineage;
    }

    /// <summary>Every element below <paramref name="parent"/> (depth-first, excluding the parent itself).</summary>
    public static IEnumerable<MonitoringElement> EnumerateDescendants(MonitoringContainerElement parent)
    {
        foreach (var child in parent.Children)
        {
            yield return child;

            if (child is MonitoringContainerElement container)
            {
                foreach (var descendant in EnumerateDescendants(container))
                {
                    yield return descendant;
                }
            }
        }
    }

    /// <summary>
    /// Where a sensor's checks run: the nearest agent host or probe in its lineage (root-first, as
    /// <see cref="BuildLineage"/> returns it). A host with an agent runs what is under it; any other host or folder belongs to
    /// the probe above it. Null when the lineage has neither (a detached element).
    /// </summary>
    public static MonitoringContainerElement? FindRunner(IReadOnlyList<MonitoringElement> lineage)
    {
        for (var index = lineage.Count - 1; index >= 0; index--)
        {
            if (lineage[index] is ProbeElement or HostElement { Agent: not null })
            {
                return (MonitoringContainerElement)lineage[index];
            }
        }

        return null;
    }

    /// <summary>The nearest PROBE in a lineage - the one an agent below it talks through (it relays), or the one that runs a sensor.</summary>
    public static ProbeElement? FindProbe(IReadOnlyList<MonitoringElement> lineage) =>
        lineage.OfType<ProbeElement>().LastOrDefault();

    /// <summary>The id a runner reports under: a probe's <see cref="ProbeElement.ProbeId"/>, an agent's <see cref="AgentIdentity.ProbeId"/>.</summary>
    public static string RunnerId(MonitoringContainerElement runner) => runner switch
    {
        ProbeElement probe => probe.ProbeId,
        HostElement { Agent: { } agent } => agent.ProbeId,
        _ => string.Empty
    };

    /// <summary>Whether a runner is somewhere else than the primary itself: a remote probe or an agent.</summary>
    public static bool IsRemoteRunner(MonitoringContainerElement? runner) =>
        runner is HostElement { Agent: not null } or ProbeElement { ParentId: not null };

    private static bool IsRunner(MonitoringElement element) => element is ProbeElement or HostElement { Agent: not null };

    /// <summary>What a runner RUNS: every element below it except what sits under a nested probe or agent host, which
    /// belongs to that one. A probe placed under another one used to hand its sensors - and their credentials - to the
    /// outer probe as well, so both executed them; the same goes for an agent's host under a probe.</summary>
    public static IEnumerable<MonitoringElement> EnumerateOwnedDescendants(MonitoringContainerElement runner)
    {
        foreach (var child in runner.Children)
        {
            if (IsRunner(child))
            {
                continue;
            }

            yield return child;

            if (child is MonitoringContainerElement container)
            {
                foreach (var descendant in EnumerateOwnedDescendants(container))
                {
                    yield return descendant;
                }
            }
        }
    }

    /// <summary>The element itself plus every descendant (depth-first).</summary>
    public static IEnumerable<MonitoringElement> EnumerateSelfAndDescendants(MonitoringElement element)
    {
        yield return element;

        if (element is MonitoringContainerElement container)
        {
            foreach (var child in container.Children)
            {
                foreach (var nested in EnumerateSelfAndDescendants(child))
                {
                    yield return nested;
                }
            }
        }
    }
}

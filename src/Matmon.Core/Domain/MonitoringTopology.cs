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

    /// <summary>What a probe RUNS: every element below it except what sits under a nested probe, which belongs to that
    /// probe. A probe placed under another one used to hand its sensors - and their credentials - to the outer probe
    /// as well, so both executed them.</summary>
    public static IEnumerable<MonitoringElement> EnumerateOwnedDescendants(ProbeElement probe)
    {
        foreach (var child in probe.Children)
        {
            if (child is ProbeElement)
            {
                continue;
            }

            yield return child;

            if (child is MonitoringContainerElement container)
            {
                foreach (var descendant in EnumerateOwnedBelow(container))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static IEnumerable<MonitoringElement> EnumerateOwnedBelow(MonitoringContainerElement parent)
    {
        foreach (var child in parent.Children)
        {
            if (child is ProbeElement)
            {
                continue;
            }

            yield return child;

            if (child is MonitoringContainerElement container)
            {
                foreach (var descendant in EnumerateOwnedBelow(container))
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

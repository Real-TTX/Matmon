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

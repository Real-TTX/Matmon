namespace Matmon.Core.Domain;

/// <summary>
/// Whether a notification rule covers an element - pure, so the targeting rules are tested rather than found
/// out by a missing alert e-mail. No target = everything; an element target = that element (and, with
/// IncludeDescendants, its subtree); a TAG target = every element whose effective tags (own + every
/// ancestor's, the same cascade the map tiles and the tree filter use) include it.
/// </summary>
public static class NotificationRuleTargeting
{
    public static bool Matches(NotificationRule rule, Guid elementId, IReadOnlyDictionary<Guid, MonitoringElement> elementsById)
    {
        // A tag target: the element's EFFECTIVE tags - its own plus every ancestor's, the same cascade the map
        // tiles and the tree filter use - must include it.
        if (rule.TargetTag is { Length: > 0 } tag)
        {
            var lineage = new List<MonitoringElement>();
            var cursor = elementsById.GetValueOrDefault(elementId);
            for (var depth = 0; cursor is not null && depth < 256; depth++)
            {
                lineage.Insert(0, cursor);
                cursor = cursor.ParentId is Guid parentId ? elementsById.GetValueOrDefault(parentId) : null;
            }

            return MonitoringTagResolver.HasTag(MonitoringTagResolver.ResolveEffective(lineage), tag);
        }

        if (rule.TargetElementId is not Guid targetId)
        {
            return true; // no target = all elements
        }

        if (targetId == elementId)
        {
            return true;
        }

        if (!rule.IncludeDescendants)
        {
            return false;
        }

        var current = elementId;
        var guard = 0;
        while (elementsById.TryGetValue(current, out var element) && element.ParentId is Guid parent && guard++ < 256)
        {
            if (parent == targetId)
            {
                return true;
            }

            current = parent;
        }

        return false;
    }
}

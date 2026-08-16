namespace Matmon.Core.Domain;

public sealed class MonitoringInheritanceResolver
{
    public MonitoringSettings ResolveTemplate(
        MonitoringTemplate template,
        IReadOnlyDictionary<Guid, MonitoringTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(templates);

        var resolved = new MonitoringSettings();
        foreach (var inheritedTemplate in ResolveTemplateChain(template.Id, templates))
        {
            resolved.ApplyFrom(inheritedTemplate.Settings);
        }

        return resolved;
    }

    public MonitoringSettings Resolve(
        IReadOnlyList<MonitoringElement> lineage,
        IReadOnlyDictionary<Guid, MonitoringTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        ArgumentNullException.ThrowIfNull(templates);

        var resolved = new MonitoringSettings();

        foreach (var element in lineage)
        {
            foreach (var template in ResolveTemplateChain(element.AppliedTemplateIds, templates))
            {
                resolved.ApplyFrom(template.Settings);
            }

            resolved.ApplyFrom(element.Settings);
        }

        return resolved;
    }

    private static IEnumerable<MonitoringTemplate> ResolveTemplateChain(
        IEnumerable<Guid> templateIds,
        IReadOnlyDictionary<Guid, MonitoringTemplate> templates)
    {
        foreach (var templateId in templateIds)
        {
            foreach (var template in ResolveTemplateChain(templateId, templates))
            {
                yield return template;
            }
        }
    }

    /// <summary>The template's inheritance chain root-first (parent … template). Public because the template
    /// editor/impact views need the same walk (previously reimplemented in the page model).</summary>
    public static IEnumerable<MonitoringTemplate> ResolveTemplateChain(
        Guid templateId,
        IReadOnlyDictionary<Guid, MonitoringTemplate> templates)
    {
        var chain = new Stack<MonitoringTemplate>();
        var visited = new HashSet<Guid>();
        var currentId = templateId;

        while (templates.TryGetValue(currentId, out var current) && visited.Add(currentId))
        {
            chain.Push(current);

            if (current.ParentTemplateId is not Guid parentId)
            {
                break;
            }

            currentId = parentId;
        }

        while (chain.Count > 0)
        {
            yield return chain.Pop();
        }
    }

}

namespace Matmon.Core.Domain;

public sealed class MonitoringTemplate
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string? Key { get; set; }

    public string Name { get; set; } = string.Empty;

    public MonitoringTemplateScope TargetKind { get; set; } = MonitoringTemplateScope.Any;

    public string? SensorTypeKey { get; set; }

    /// <summary>
    /// A sensor template's target (an address, a URL): a sensor made from it gets this target unless one is typed,
    /// and re-applying / restoring the template sets it again. Empty = the parent template's, else the sensor's own.
    /// </summary>
    public string? Target { get; set; }

    public Guid? ParentTemplateId { get; set; }

    /// <summary>The template store set this template was added from (its slug on Matmon Cloud), null for one made or imported here.
    /// With <see cref="StoreVersion"/> it lets the instance say when the store has a newer version and update the set in place.
    /// Instance-local: never written into a template file.</summary>
    public string? StoreSlug { get; set; }

    /// <summary>The store version this template was added or last updated from.</summary>
    public string? StoreVersion { get; set; }

    /// <summary>The target this template gives a sensor: its own, else the nearest parent's (null = none).</summary>
    public static string? ResolveTarget(MonitoringTemplate template, IReadOnlyDictionary<Guid, MonitoringTemplate> templates)
    {
        var seen = new HashSet<Guid>();
        for (MonitoringTemplate? current = template; current is not null && seen.Add(current.Id);
             current = current.ParentTemplateId is Guid parentId && templates.TryGetValue(parentId, out var parent) ? parent : null)
        {
            if (!string.IsNullOrWhiteSpace(current.Target))
            {
                return current.Target.Trim();
            }
        }

        return null;
    }

    public MonitoringSettings Settings { get; set; } = new();

    /// <summary>Tags merged into an element's own tags when this template is applied (copy + origin).</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Deep, detached copy - see <see cref="MonitoringElement.Clone"/>.</summary>
    public MonitoringTemplate Clone()
    {
        return new MonitoringTemplate
        {
            Id = Id,
            Key = Key,
            Name = Name,
            TargetKind = TargetKind,
            SensorTypeKey = SensorTypeKey,
            Target = Target,
            ParentTemplateId = ParentTemplateId,
            StoreSlug = StoreSlug,
            StoreVersion = StoreVersion,
            Settings = Settings.Clone(),
            Tags = [.. Tags]
        };
    }
}

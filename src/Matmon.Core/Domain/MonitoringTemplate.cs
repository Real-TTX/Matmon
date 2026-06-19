namespace Matmon.Core.Domain;

public sealed class MonitoringTemplate
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string? Key { get; set; }

    public string Name { get; set; } = string.Empty;

    public MonitoringTemplateScope TargetKind { get; set; } = MonitoringTemplateScope.Any;

    public string? SensorTypeKey { get; set; }

    public Guid? ParentTemplateId { get; set; }

    public MonitoringSettings Settings { get; set; } = new();

    /// <summary>Tags merged into an element's own tags when this template is applied (copy + origin).</summary>
    public List<string> Tags { get; set; } = [];
}

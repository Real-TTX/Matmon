using System.Text.Json.Serialization;

namespace Matmon.Core.Domain;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(ProbeElement), typeDiscriminator: "probe")]
[JsonDerivedType(typeof(FolderElement), typeDiscriminator: "folder")]
[JsonDerivedType(typeof(HostElement), typeDiscriminator: "host")]
[JsonDerivedType(typeof(SensorElement), typeDiscriminator: "sensor")]
public abstract class MonitoringElement
{
    protected MonitoringElement(string name)
    {
        Name = name;
    }

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; }

    public string? Description { get; set; }

    public Guid? ParentId { get; set; }

    public MonitoringSettings Settings { get; set; } = new();

    public List<Guid> AppliedTemplateIds { get; set; } = [];

    public abstract MonitoringElementKind Kind { get; }

    public virtual bool CanHaveChildren => false;
}

public abstract class MonitoringContainerElement : MonitoringElement
{
    protected MonitoringContainerElement(string name) : base(name)
    {
    }

    public override bool CanHaveChildren => true;

    public List<MonitoringElement> Children { get; set; } = [];
}

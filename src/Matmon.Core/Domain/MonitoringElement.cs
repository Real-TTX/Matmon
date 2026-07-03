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

    /// <summary>
    /// Free-form labels for grouping/filtering. Tags cascade: a sensor's effective
    /// tags are its own plus every ancestor's (see <c>MonitoringTagResolver</c>).
    /// Template tags are merged in when a template is applied (copy + origin).
    /// </summary>
    public List<string> Tags { get; set; } = [];

    public Guid? ParentId { get; set; }

    public MonitoringSettings Settings { get; set; } = new();

    /// <summary>
    /// Templates are applied as a one-shot copy: their resolved values are baked into
    /// <see cref="Settings"/> when applied. <see cref="TemplateOriginId"/> records which template the
    /// element was created from / last restored from, so it can be re-applied ("restore") later.
    /// </summary>
    public Guid? TemplateOriginId { get; set; }

    /// <summary>
    /// Legacy live-inheritance links. No longer populated — templates are copied now. Kept so old
    /// workspaces still deserialize; <c>MigrateAppliedTemplatesToCopies</c> bakes any remaining links
    /// into <see cref="Settings"/> on load and clears this list.
    /// </summary>
    public List<Guid> AppliedTemplateIds { get; set; } = [];

    public abstract MonitoringElementKind Kind { get; }

    public virtual bool CanHaveChildren => false;

    /// <summary>
    /// Deep, detached copy — mutating the clone (or its <see cref="Settings"/> / children) never
    /// touches the original. Used by the store's read accessors so callers can read/enumerate a private
    /// copy without racing writers that mutate the live tree.
    /// </summary>
    public abstract MonitoringElement Clone();

    protected void CopyBaseTo(MonitoringElement clone)
    {
        clone.Name = Name;
        clone.Description = Description;
        clone.Tags = [.. Tags];
        clone.ParentId = ParentId;
        clone.Settings = Settings.Clone();
        clone.TemplateOriginId = TemplateOriginId;
        clone.AppliedTemplateIds = [.. AppliedTemplateIds];
    }
}

public abstract class MonitoringContainerElement : MonitoringElement
{
    protected MonitoringContainerElement(string name) : base(name)
    {
    }

    public override bool CanHaveChildren => true;

    public List<MonitoringElement> Children { get; set; } = [];

    protected void CopyChildrenTo(MonitoringContainerElement clone)
    {
        clone.Children = Children.Select(child => child.Clone()).ToList();
    }
}

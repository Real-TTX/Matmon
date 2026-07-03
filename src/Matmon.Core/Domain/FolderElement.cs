namespace Matmon.Core.Domain;

public sealed class FolderElement : MonitoringContainerElement
{
    public FolderElement(string name) : base(name)
    {
    }

    public override MonitoringElementKind Kind => MonitoringElementKind.Folder;

    public override MonitoringElement Clone()
    {
        var clone = new FolderElement(Name) { Id = Id };
        CopyBaseTo(clone);
        CopyChildrenTo(clone);
        return clone;
    }
}

namespace Matmon.Core.Domain;

public sealed class FolderElement : MonitoringContainerElement
{
    public FolderElement(string name) : base(name)
    {
    }

    public override MonitoringElementKind Kind => MonitoringElementKind.Folder;
}

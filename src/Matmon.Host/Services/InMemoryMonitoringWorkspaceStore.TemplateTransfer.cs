using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed partial class InMemoryMonitoringWorkspaceStore
{
    public IReadOnlyList<MonitoringTemplate> ImportTemplates(IReadOnlyList<MonitoringTemplate> templates)
    {
        lock (_gate)
        {
            var added = new List<MonitoringTemplate>();
            var ids = _document.Templates.Select(template => template.Id).ToHashSet();
            foreach (var template in templates)
            {
                if (ids.Contains(template.Id))
                {
                    continue;
                }

                var copy = template.Clone();
                if (copy.ParentTemplateId is Guid parent && !ids.Contains(parent))
                {
                    copy.ParentTemplateId = null;
                }

                _document.Templates.Add(copy);
                ids.Add(copy.Id);
                added.Add(copy.Clone());
            }

            if (added.Count > 0)
            {
                QueueSave(SavePriority.Configuration);
            }

            return added;
        }
    }

    public IReadOnlySet<string> GetSecretParameterKeys()
    {
        lock (_gate)
        {
            return DeclaredSecretParameterKeys(_document);
        }
    }
}

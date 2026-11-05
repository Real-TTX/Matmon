using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>What <see cref="IMonitoringWorkspaceStore.UpdateTemplatesFromStore"/> did: the templates brought to the new version,
/// the ones the new version added, and the ones of the set the new version no longer has (left alone).</summary>
public sealed record TemplateStoreUpdateResult(IReadOnlyList<string> Updated, IReadOnlyList<string> Added, IReadOnlyList<string> NoLongerInSet);

public sealed partial class InMemoryMonitoringWorkspaceStore
{
    public IReadOnlyList<MonitoringTemplate> ImportTemplates(IReadOnlyList<MonitoringTemplate> templates, string? storeSlug = null, string? storeVersion = null)
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

                copy.StoreSlug = CleanStoreText(storeSlug, 80);
                copy.StoreVersion = copy.StoreSlug is null ? null : CleanStoreText(storeVersion, 40);
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

    public TemplateStoreUpdateResult UpdateTemplatesFromStore(string storeSlug, string? storeVersion, IReadOnlyList<MonitoringTemplate> templates)
    {
        var slug = CleanStoreText(storeSlug, 80) ?? throw new ArgumentException("A store set is required.", nameof(storeSlug));
        var version = CleanStoreText(storeVersion, 40);
        lock (_gate)
        {
            var secretKeys = DeclaredSecretParameterKeys(_document);
            var existing = _document.Templates.Where(template => string.Equals(template.StoreSlug, slug, StringComparison.OrdinalIgnoreCase)).ToList();
            var byName = existing.GroupBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var ids = _document.Templates.Select(template => template.Id).ToHashSet();

            // The file's ids -> the id each template ends up with here (a matched one keeps its own).
            var finalIds = templates.ToDictionary(template => template.Id,
                template => byName.TryGetValue(template.Name, out var match) ? match.Id : template.Id);

            var updated = new List<string>();
            var added = new List<string>();
            var touched = new HashSet<Guid>();
            foreach (var incoming in templates)
            {
                Guid? parent = incoming.ParentTemplateId is Guid parentId && finalIds.TryGetValue(parentId, out var mapped) ? mapped : null;
                if (byName.TryGetValue(incoming.Name, out var target) && touched.Add(target.Id))
                {
                    var settings = incoming.Settings.Clone();
                    KeepLocalSecrets(target.Settings, settings, secretKeys);
                    target.TargetKind = incoming.TargetKind;
                    target.SensorTypeKey = incoming.SensorTypeKey;
                    target.Target = incoming.Target;
                    target.ParentTemplateId = parent == target.Id ? null : parent;
                    target.Settings = settings;
                    target.Tags = [.. incoming.Tags];
                    target.StoreVersion = version;
                    updated.Add(target.Name);
                }
                else if (!ids.Contains(incoming.Id))
                {
                    var copy = incoming.Clone();
                    copy.ParentTemplateId = parent is Guid p && (ids.Contains(p) || finalIds.ContainsValue(p)) ? p : null;
                    copy.StoreSlug = slug;
                    copy.StoreVersion = version;
                    _document.Templates.Add(copy);
                    ids.Add(copy.Id);
                    added.Add(copy.Name);
                }
            }

            // A parent pointing at a template that never arrived (a broken file) is dropped rather than left dangling.
            foreach (var template in _document.Templates.Where(t => t.ParentTemplateId is Guid p && !ids.Contains(p)))
            {
                template.ParentTemplateId = null;
            }

            var gone = existing.Where(template => !touched.Contains(template.Id)).Select(template => template.Name).ToList();
            if (updated.Count + added.Count > 0)
            {
                QueueSave(SavePriority.Configuration);
            }

            return new TemplateStoreUpdateResult(updated, added, gone);
        }
    }

    /// <summary>
    /// What was filled in HERE survives an update: a store file carries credential bundles without values and no secret parameters,
    /// so the values of a bundle of the same name and kind and every secret parameter the new version does not set are kept.
    /// </summary>
    private static void KeepLocalSecrets(MonitoringSettings current, MonitoringSettings incoming, IReadOnlySet<string> secretKeys)
    {
        foreach (var bundle in incoming.Credentials)
        {
            var old = current.Credentials.FirstOrDefault(candidate =>
                candidate.Kind == bundle.Kind && string.Equals(candidate.Name, bundle.Name, StringComparison.OrdinalIgnoreCase));
            if (old is not null && bundle.Values.Count == 0 && string.IsNullOrEmpty(bundle.ProtectedValues))
            {
                bundle.Values = new Dictionary<string, string>(old.Values, StringComparer.OrdinalIgnoreCase);
                bundle.ProtectedValues = old.ProtectedValues;
                bundle.HydrationFailed = old.HydrationFailed;
            }
        }

        foreach (var (key, value) in current.Parameters)
        {
            if (IsSecretParameter(key, secretKeys) && !incoming.Parameters.ContainsKey(key))
            {
                incoming.Parameters[key] = value;
            }
        }
    }

    private static string? CleanStoreText(string? text, int max)
    {
        var value = (text ?? string.Empty).Trim();
        return value.Length == 0 ? null : value.Length > max ? value[..max] : value;
    }

    public IReadOnlySet<string> GetSecretParameterKeys()
    {
        lock (_gate)
        {
            return DeclaredSecretParameterKeys(_document);
        }
    }
}

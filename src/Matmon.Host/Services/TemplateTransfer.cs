using System.Text.Json;
using System.Text.Json.Serialization;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>
/// Templates as a file: export one template (with the templates it is based on) or all of them, import such a file on
/// any instance. The file carries what a template IS - scope, sensor type, parameters, thresholds, schedule, channel
/// settings, tags - and never a secret: credential bundles travel as their name and kind only (to be filled in after the
/// import), secret parameters (passwords, tokens, communities ...) are left out, and so is everything that only means
/// something on this instance (the dependency sensor). Built-in template keys are dropped, so an imported copy never
/// stands in for a template the instance seeds itself.
/// </summary>
public static class TemplateTransfer
{
    public const string Format = "matmon-templates";
    public const int Version = 1;

    /// <summary>A file larger than this is no template file.</summary>
    public const int MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>At most this many templates per file.</summary>
    public const int MaxTemplates = 500;

    public sealed class Package
    {
        public string? Format { get; set; }

        public int Version { get; set; } = TemplateTransfer.Version;

        public string? ExportedBy { get; set; }

        public DateTimeOffset ExportedUtc { get; set; }

        public List<MonitoringTemplate> Templates { get; set; } = [];
    }

    public sealed record ImportResult(IReadOnlyList<MonitoringTemplate> Templates, IReadOnlyList<string> Notes);

    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>The templates to export: the chosen ones and every template they are based on, parents first.</summary>
    public static IReadOnlyList<MonitoringTemplate> WithParents(IReadOnlyList<MonitoringTemplate> all, IEnumerable<Guid> chosen)
    {
        var byId = all.ToDictionary(template => template.Id);
        var ordered = new List<MonitoringTemplate>();
        var seen = new HashSet<Guid>();

        void Add(Guid id, int depth)
        {
            if (depth > 32 || !byId.TryGetValue(id, out var template) || seen.Contains(id))
            {
                return;
            }

            if (template.ParentTemplateId is Guid parent)
            {
                Add(parent, depth + 1);
            }

            if (seen.Add(id))
            {
                ordered.Add(template);
            }
        }

        foreach (var id in chosen)
        {
            Add(id, 0);
        }

        return ordered;
    }

    public static byte[] Export(IReadOnlyList<MonitoringTemplate> templates, IReadOnlySet<string> declaredSecretParameters, string exportedBy, DateTimeOffset now)
    {
        var package = new Package
        {
            Format = Format,
            ExportedBy = exportedBy,
            ExportedUtc = now,
            Templates = templates.Select(template => Strip(template.Clone(), declaredSecretParameters)).ToList()
        };
        return JsonSerializer.SerializeToUtf8Bytes(package, Options);
    }

    private static MonitoringTemplate Strip(MonitoringTemplate template, IReadOnlySet<string> declaredSecretParameters)
    {
        template.Key = null;
        var settings = template.Settings;
        settings.DependencySensorId = null;
        foreach (var key in settings.Parameters.Keys.ToArray())
        {
            if (InMemoryMonitoringWorkspaceStore.IsSecretParameter(key, declaredSecretParameters) ||
                settings.Parameters[key].StartsWith(InMemoryMonitoringWorkspaceStore.ProtectedParameterPrefix, StringComparison.Ordinal))
            {
                settings.Parameters.Remove(key);
            }
        }

        foreach (var credential in settings.Credentials)
        {
            credential.Values.Clear();
            credential.ProtectedValues = string.Empty;
        }

        return template;
    }

    /// <summary>
    /// Reads a template file. The templates come back with NEW ids (parents inside the file re-linked to them, a parent
    /// that is not in the file dropped), names unique against <paramref name="existingNames"/>, no built-in key, no
    /// secrets - whatever the file says. Throws <see cref="InvalidDataException"/> with a reason in words.
    /// </summary>
    public static ImportResult Read(byte[] bytes, IReadOnlyCollection<string> existingNames, IReadOnlySet<string> knownSensorTypes, IReadOnlySet<string> declaredSecretParameters)
    {
        if (bytes.Length == 0)
        {
            throw new InvalidDataException("The file is empty.");
        }

        if (bytes.Length > MaxFileBytes)
        {
            throw new InvalidDataException("The file is too large for a template file (at most 2 MB).");
        }

        Package? package;
        try
        {
            package = JsonSerializer.Deserialize<Package>(bytes, Options);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"This is not a Matmon template file: {exception.Message}");
        }

        if (package is null || !string.Equals(package.Format, Format, StringComparison.Ordinal))
        {
            throw new InvalidDataException("This is not a Matmon template file (it does not say \"matmon-templates\").");
        }

        if (package.Version > Version)
        {
            throw new InvalidDataException($"The file was written by a newer Matmon (format {package.Version}) - update this instance first.");
        }

        if (package.Templates.Count == 0)
        {
            throw new InvalidDataException("The file holds no templates.");
        }

        if (package.Templates.Count > MaxTemplates)
        {
            throw new InvalidDataException($"The file holds {package.Templates.Count} templates - at most {MaxTemplates} are taken at once.");
        }

        var notes = new List<string>();
        var newIds = package.Templates.Where(template => template is not null).GroupBy(template => template.Id)
            .ToDictionary(group => group.Key, _ => Guid.NewGuid());
        var names = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var imported = new List<MonitoringTemplate>();

        foreach (var source in package.Templates.Where(template => template is not null).DistinctBy(template => template.Id))
        {
            var name = string.IsNullOrWhiteSpace(source.Name) ? "Imported template" : source.Name.Trim();
            if (name.Length > 120)
            {
                name = name[..120];
            }

            var unique = name;
            for (var number = 2; !names.Add(unique); number++)
            {
                unique = number == 2 ? $"{name} (imported)" : $"{name} (imported {number - 1})";
            }

            if (unique != name)
            {
                notes.Add($"'{name}' exists already - imported as '{unique}'.");
            }

            Guid? parent = null;
            if (source.ParentTemplateId is Guid parentId)
            {
                if (newIds.TryGetValue(parentId, out var mapped))
                {
                    parent = mapped;
                }
                else
                {
                    notes.Add($"'{unique}' was based on a template that is not in the file - it is a template of its own now.");
                }
            }

            var sensorType = string.IsNullOrWhiteSpace(source.SensorTypeKey) ? null : source.SensorTypeKey.Trim();
            if (sensorType is not null && !knownSensorTypes.Contains(sensorType))
            {
                notes.Add($"'{unique}' is for the sensor type '{sensorType}', which this instance does not have.");
            }

            var template = Strip(new MonitoringTemplate
            {
                Id = newIds[source.Id],
                Name = unique,
                TargetKind = source.TargetKind,
                SensorTypeKey = sensorType,
                ParentTemplateId = parent,
                Settings = source.Settings ?? new MonitoringSettings(),
                Tags = MonitoringTagResolver.Normalize(source.Tags)
            }, declaredSecretParameters);

            // A credential slot whose values were left behind: say so once per template.
            if (template.Settings.Credentials.Count > 0)
            {
                notes.Add($"'{unique}' names {template.Settings.Credentials.Count} credential{(template.Settings.Credentials.Count == 1 ? string.Empty : "s")} without their secrets - fill them in on the template.");
            }

            imported.Add(template);
        }

        // Parents before children, so a parent always exists when its child is added.
        var ordered = new List<MonitoringTemplate>();
        var placed = new HashSet<Guid>();
        var remaining = imported.ToList();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(template => template.ParentTemplateId is not Guid parent || placed.Contains(parent)).ToArray();
            if (ready.Length == 0)
            {
                // A loop in the file: cut it rather than refuse the whole file.
                foreach (var template in remaining)
                {
                    template.ParentTemplateId = null;
                }

                notes.Add("The file's templates were based on each other in a circle - that link was dropped.");
                continue;
            }

            foreach (var template in ready)
            {
                ordered.Add(template);
                placed.Add(template.Id);
                remaining.Remove(template);
            }
        }

        return new ImportResult(ordered, notes);
    }

    public static string FileName(IReadOnlyList<MonitoringTemplate> templates, DateTimeOffset now)
    {
        var stem = templates.Count == 1
            ? new string(templates[0].Name.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-').ToArray()).Trim('-')
            : "templates";
        while (stem.Contains("--", StringComparison.Ordinal))
        {
            stem = stem.Replace("--", "-", StringComparison.Ordinal);
        }

        return $"matmon-template-{(stem.Length == 0 ? "export" : stem)}-{now:yyyyMMdd}.json";
    }
}

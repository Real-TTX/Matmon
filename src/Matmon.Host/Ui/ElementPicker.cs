using Matmon.Core.Domain;

namespace Matmon.Host.Ui;

/// <summary>One selectable element in the <c>_ElementPicker</c> dialog.</summary>
public sealed record ElementPickerOption(
    Guid Id,
    string Name,
    string Path,
    MonitoringElementKind Kind,
    string KindLabel,
    string KindIconKey,
    IReadOnlyList<string> Tags);

/// <summary>
/// View model for the reusable searchable element picker (a field that looks like
/// a normal input but opens a filterable dialog instead of a long dropdown).
/// </summary>
public sealed class ElementPickerModel
{
    /// <summary>The form field name written into the hidden input (the selected element id).</summary>
    public required string FieldName { get; init; }

    /// <summary>Unique DOM id so multiple pickers can coexist on a page.</summary>
    public required string DomId { get; init; }

    public Guid? SelectedId { get; init; }

    public string Placeholder { get; init; } = "Select…";

    /// <summary>Allow clearing the selection back to none.</summary>
    public bool AllowClear { get; init; } = true;

    public IReadOnlyList<ElementPickerOption> Options { get; init; } = [];

    public ElementPickerOption? Selected =>
        SelectedId is { } id ? Options.FirstOrDefault(option => option.Id == id) : null;
}

public static class ElementPickerOptions
{
    private static readonly Dictionary<MonitoringElementKind, string> IconKeys = new()
    {
        [MonitoringElementKind.Probe] = "probe",
        [MonitoringElementKind.Folder] = "folder",
        [MonitoringElementKind.Host] = "host",
        [MonitoringElementKind.Sensor] = "sensor",
    };

    /// <summary>
    /// Flattens the topology into picker options with full path and cascading
    /// effective tags. <paramref name="kinds"/> limits which element kinds are
    /// offered (null = all).
    /// </summary>
    public static IReadOnlyList<ElementPickerOption> Build(
        MonitoringElement? root,
        IReadOnlySet<MonitoringElementKind>? kinds = null)
    {
        var options = new List<ElementPickerOption>();
        if (root is not null)
        {
            Walk(root, parentPath: string.Empty, inheritedTags: [], kinds, options);
        }

        return options;
    }

    private static void Walk(
        MonitoringElement element,
        string parentPath,
        IReadOnlyList<string> inheritedTags,
        IReadOnlySet<MonitoringElementKind>? kinds,
        List<ElementPickerOption> options)
    {
        var path = string.IsNullOrWhiteSpace(parentPath) ? element.Name : $"{parentPath} / {element.Name}";
        var effectiveTags = MonitoringTagResolver.Normalize(inheritedTags.Concat(element.Tags));

        if (kinds is null || kinds.Contains(element.Kind))
        {
            options.Add(new ElementPickerOption(
                element.Id,
                element.Name,
                path,
                element.Kind,
                element.Kind.ToString(),
                IconKeys.GetValueOrDefault(element.Kind, "sensor"),
                effectiveTags));
        }

        if (element is MonitoringContainerElement container)
        {
            foreach (var child in container.Children)
            {
                Walk(child, path, effectiveTags, kinds, options);
            }
        }
    }
}

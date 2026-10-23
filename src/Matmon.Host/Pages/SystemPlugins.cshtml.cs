using System.Globalization;
using Matmon.Core.Domain;
using Matmon.Host.Services;
using Matmon.Host.Services.Plugins;
using Matmon.Probe.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

public sealed class PluginAssignmentInput
{
    public Guid? Id { get; set; }

    public string PluginId { get; set; } = string.Empty;

    public Guid ProbeElementId { get; set; }

    public bool Enabled { get; set; } = true;

    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// System > Plugins (admins): which plugins this instance knows (built in, or loaded from its plugins folder), where each
/// runs - on the primary or on a probe / agent - with which settings, and what each reported last. A probe picks up a
/// change within seconds; its events (syslog lines, ...) arrive in the same places as the primary's own.
/// </summary>
public sealed class SystemPluginsModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _store;
    private readonly PluginCatalog _catalog;
    private readonly PluginStatusRegistry? _registry;
    private readonly MatmonRuntimeOptions _options;

    public SystemPluginsModel(IMonitoringWorkspaceStore store, PluginCatalog catalog, MatmonRuntimeOptions options, PluginStatusRegistry? registry = null)
    {
        _store = store;
        _catalog = catalog;
        _options = options;
        _registry = registry;
    }

    [BindProperty]
    public PluginAssignmentInput Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public string? FormError { get; private set; }

    public IReadOnlyList<IMatmonPlugin> Plugins => _catalog.Plugins;

    public IReadOnlyList<string> LoadErrors => _catalog.LoadErrors;

    public string PluginsPath => _options.PluginsPath ?? "plugins";

    public IReadOnlyList<PluginLocation> Locations { get; private set; } = [];

    public IReadOnlyList<PluginAssignmentRow> Rows { get; private set; } = [];

    public bool Editing { get; private set; }

    public PluginDefinition? EditingDefinition => _catalog.Find(Input.PluginId)?.Definition;

    public static bool IsBuiltIn(IMatmonPlugin plugin) => plugin.GetType().Assembly == typeof(SyslogCollectorPlugin).Assembly;

    public IActionResult OnGet(Guid? edit, string? add)
    {
        if (_registry is null)
        {
            return NotFound();
        }

        Load();
        if (edit is Guid id && _store.GetPluginAssignments().FirstOrDefault(assignment => assignment.Id == id) is { } existing)
        {
            Input = new PluginAssignmentInput
            {
                Id = existing.Id,
                PluginId = existing.PluginId,
                ProbeElementId = existing.ProbeElementId,
                Enabled = existing.Enabled,
                Settings = new Dictionary<string, string>(existing.Settings, StringComparer.OrdinalIgnoreCase)
            };
            Editing = true;
        }
        else if (_catalog.Find(add) is { } plugin)
        {
            Input = new PluginAssignmentInput
            {
                PluginId = plugin.Definition.Id,
                ProbeElementId = LocationsFor(plugin.Definition).FirstOrDefault()?.Id ?? Guid.Empty,
                Settings = plugin.Definition.Settings
                    .Where(setting => setting.DefaultValue is not null)
                    .ToDictionary(setting => setting.Key, setting => setting.DefaultValue!, StringComparer.OrdinalIgnoreCase)
            };
            Editing = true;
        }

        return Page();
    }

    public IActionResult OnPostSave()
    {
        if (_registry is null)
        {
            return NotFound();
        }

        Load();
        if (!TryBuild(Input, out var assignment, out var error))
        {
            FormError = error;
            Editing = true;
            return Page();
        }

        try
        {
            _store.SavePluginAssignment(assignment);
        }
        catch (InvalidOperationException exception)
        {
            FormError = exception.Message;
            Editing = true;
            return Page();
        }

        var location = Locations.FirstOrDefault(candidate => candidate.Id == assignment.ProbeElementId);
        StatusMessage = $"Saved. {(location?.IsPrimary == true ? "The primary" : $"The probe '{location?.Name}'")} applies it within a few seconds.";
        return RedirectToPage();
    }

    public IActionResult OnPostDelete(Guid id)
    {
        if (_registry is null)
        {
            return NotFound();
        }

        StatusMessage = _store.DeletePluginAssignment(id) ? "Removed - the plugin stops there within a few seconds." : null;
        return RedirectToPage();
    }

    /// <summary>Checks the form against the plugin's definition: a known plugin, a place it can run, settings of the right
    /// kind. Refuses rather than corrects - a port typed wrong would otherwise open somewhere unexpected.</summary>
    public bool TryBuild(PluginAssignmentInput input, out PluginAssignment assignment, out string? error)
    {
        assignment = new PluginAssignment();
        error = null;
        var plugin = _catalog.Find(input.PluginId);
        if (plugin is null)
        {
            error = "Unknown plugin.";
            return false;
        }

        var location = Locations.FirstOrDefault(candidate => candidate.Id == input.ProbeElementId);
        if (location is null || !LocationsFor(plugin.Definition).Contains(location))
        {
            error = $"'{plugin.Definition.DisplayName}' cannot run there - pick {(plugin.Definition.Placement == PluginPlacement.Probe ? "a probe or agent" : "a place from the list")}.";
            return false;
        }

        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in plugin.Definition.Settings)
        {
            var value = input.Settings.TryGetValue(definition.Key, out var posted) ? posted?.Trim() ?? string.Empty : string.Empty;
            switch (definition.Kind)
            {
                case PluginSettingKind.Boolean:
                    value = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
                    break;
                case PluginSettingKind.Port when value.Length > 0 && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535):
                    error = $"{definition.Label}: a port from 1 to 65535.";
                    return false;
                case PluginSettingKind.Number when value.Length > 0 && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _):
                    error = $"{definition.Label}: a whole number.";
                    return false;
            }

            if (definition.Required && value.Length == 0)
            {
                error = $"{definition.Label} is required.";
                return false;
            }

            if (value.Length > 0)
            {
                settings[definition.Key] = value;
            }
        }

        assignment = new PluginAssignment
        {
            Id = input.Id ?? Guid.NewGuid(),
            PluginId = plugin.Definition.Id,
            ProbeElementId = location.Id,
            Enabled = input.Enabled,
            Settings = settings
        };
        return true;
    }

    public IReadOnlyList<PluginLocation> LocationsFor(PluginDefinition definition) =>
        Locations.Where(location => location.IsPrimary
            ? definition.Placement.HasFlag(PluginPlacement.Primary)
            : definition.Placement.HasFlag(PluginPlacement.Probe)).ToList();

    private void Load()
    {
        var probes = _store.GetAllElements().OfType<ProbeElement>().ToList();
        Locations = probes
            .Select(probe => new PluginLocation(probe.Id, probe.ParentId is null ? $"{probe.Name} (this primary)" : probe.Name, probe.ParentId is null, probe.ProbeId))
            .OrderBy(location => !location.IsPrimary)
            .ThenBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Rows = _store.GetPluginAssignments()
            .Select(assignment =>
            {
                var location = Locations.FirstOrDefault(candidate => candidate.Id == assignment.ProbeElementId);
                var state = _registry?.Assignment(assignment.Id);
                var probeState = location is { IsPrimary: false } ? _registry?.Probe(location.ProbeId) : null;
                return new PluginAssignmentRow(assignment, _catalog.Find(assignment.PluginId)?.Definition, location, state, probeState);
            })
            .OrderBy(row => row.Definition?.DisplayName ?? row.Assignment.PluginId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Location?.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed record PluginLocation(Guid Id, string Name, bool IsPrimary, string ProbeId);

public sealed record PluginAssignmentRow(
    PluginAssignment Assignment,
    PluginDefinition? Definition,
    PluginLocation? Location,
    PluginAssignmentState? State,
    ProbePluginState? ProbeState)
{
    /// <summary>The pill: off / running / error / waiting (the probe has not reported it yet).</summary>
    public (string Tone, string Label) Pill => !Assignment.Enabled
        ? ("paused", "Off")
        : State is null
            ? ("plain", "Waiting")
            : State.State switch
            {
                "running" => ("ok", "Running"),
                "error" => ("error", "Error"),
                _ => ("info", State.State)
            };

    public string Detail => !Assignment.Enabled
        ? "Switched off."
        : State is not null
            ? State.Detail ?? string.Empty
            : Location?.IsPrimary == true
                ? "Starting..."
                : "The probe has not reported it yet - it is offline, or an agent from before plugins (update it).";
}

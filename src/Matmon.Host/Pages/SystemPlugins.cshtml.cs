using System.Globalization;
using Matmon.Core.Domain;
using Matmon.Core.Syslog;
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
/// runs - on the primary (the main probe) or on a probe / agent - with which settings, and what each reported last. A probe
/// picks up a change within seconds; its events (syslog lines, ...) arrive in the same places as the primary's own. A
/// plugin that runs somewhere brings its menu entries (System > Syslog, ...); one that runs nowhere adds none.
/// </summary>
public sealed class SystemPluginsModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _store;
    private readonly PluginCatalog _catalog;
    private readonly PluginUi _ui;
    private readonly PluginStatusRegistry? _registry;
    private readonly MatmonRuntimeOptions _options;

    public SystemPluginsModel(IMonitoringWorkspaceStore store, PluginCatalog catalog, PluginUi ui, MatmonRuntimeOptions options, PluginStatusRegistry? registry = null)
    {
        _store = store;
        _catalog = catalog;
        _ui = ui;
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

    public IReadOnlyList<PluginPlacementView> Rows { get; private set; } = [];

    /// <summary>The page a plugin brings along (its System tab), for the "open" link on its rows.</summary>
    public static PluginUiLink? PageOf(PluginDefinition? definition) =>
        definition?.Links.FirstOrDefault(link => link.Area == PluginUiArea.System);

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
        var page = PageOf(_catalog.Find(assignment.PluginId)?.Definition);
        StatusMessage = $"Saved. {(location?.IsPrimary == true ? "The primary" : $"The probe '{location?.Name}'")} applies it within a few seconds." +
            (assignment.Enabled && page is not null ? $" Its page is under System > {page.Title}." : string.Empty);
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

        // One assignment per plugin and place: a second "Add" of the same collector on the same probe would only fail
        // with "port in use" - the existing one is the one to change.
        var existingId = input.Id ?? Guid.Empty;
        if (_store.GetPluginAssignments().Any(other => other.Id != existingId && other.ProbeElementId == location.Id &&
            string.Equals(other.PluginId, plugin.Definition.Id, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"'{plugin.Definition.DisplayName}' is already set up on {location.Name} - change that one instead.";
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
                case PluginSettingKind.AddressList when value.Length > 0:
                    // Refused, not ignored: an entry dropped from an allow-list makes it wider than the admin meant.
                    IpAllowList.Parse(value, out var invalid);
                    if (invalid.Count > 0)
                    {
                        error = $"{definition.Label}: not an IP address or range: {string.Join(", ", invalid)}. Use addresses like 192.168.1.2 or ranges like 192.168.1.0/24, separated by commas or new lines.";
                        return false;
                    }

                    break;
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
        Locations = _ui.Locations();
        Rows = _ui.Placements();
    }
}

using Matmon.Core.Domain;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>
/// Admin authoring for custom script sensor TYPES: a reusable type defined once (script + language + output
/// format) and instantiable like any built-in sensor. Types are stored in the workspace (so they ride in the
/// cloud config backup) and executed by the Local Script engine. Same host-code-execution trust surface as the
/// Local Script sensor, so this page is admin-only.
/// </summary>
public class CustomSensorTypesModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;

    public CustomSensorTypesModel(IMonitoringWorkspaceStore workspaceStore)
    {
        _workspaceStore = workspaceStore;
    }

    public IReadOnlyList<SensorDefinition> Types { get; private set; } = [];
    public bool IsEditing => !string.IsNullOrEmpty(EditKey);

    [BindProperty] public string? EditKey { get; set; }
    [BindProperty] public string Name { get; set; } = string.Empty;
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string Language { get; set; } = "pwsh";
    [BindProperty] public string OutputFormat { get; set; } = "auto";
    [BindProperty] public string ScriptBody { get; set; } = string.Empty;
    [BindProperty] public string? RegexPattern { get; set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public void OnGet(string? key)
    {
        Load();
        if (!string.IsNullOrEmpty(key) && _workspaceStore.GetCustomSensorType(key) is { } existing)
        {
            EditKey = existing.Key;
            Name = existing.DisplayName;
            Description = existing.Description;
            Language = existing.ScriptLanguage ?? "pwsh";
            OutputFormat = existing.ScriptOutputFormat ?? "auto";
            ScriptBody = existing.ScriptBody ?? string.Empty;
            RegexPattern = existing.ScriptRegexPattern;
        }
    }

    public IActionResult OnPostSave()
    {
        try
        {
            if (string.IsNullOrEmpty(EditKey))
            {
                _workspaceStore.CreateCustomSensorType(Name, Description, Language, OutputFormat, ScriptBody, RegexPattern);
                StatusMessage = "Custom sensor type created.";
            }
            else
            {
                if (_workspaceStore.UpdateCustomSensorType(EditKey, Name, Description, Language, OutputFormat, ScriptBody, RegexPattern) is null)
                {
                    ErrorMessage = "That custom sensor type no longer exists.";
                }
                else
                {
                    StatusMessage = "Custom sensor type saved.";
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            Load();
            return Page();
        }

        return RedirectToPage();
    }

    public IActionResult OnPostDelete(string key)
    {
        try
        {
            if (!_workspaceStore.DeleteCustomSensorType(key))
            {
                ErrorMessage = "That custom sensor type no longer exists.";
            }
            else
            {
                StatusMessage = "Custom sensor type deleted.";
            }
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }

    private void Load()
    {
        Types = _workspaceStore.GetCustomSensorTypes();
    }
}

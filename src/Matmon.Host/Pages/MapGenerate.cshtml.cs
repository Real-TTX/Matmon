using Matmon.Host.Services;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>Maps > Generate: a first map from an element or a tag, then straight into the designer.</summary>
public sealed class MapGenerateModel(IMonitoringWorkspaceStore store, MapGenerator generator) : PageModel
{
    [BindProperty]
    public string Name { get; set; } = string.Empty;

    [BindProperty]
    public string? TargetToken { get; set; }

    [BindProperty]
    public bool Overview { get; set; } = true;

    [BindProperty]
    public bool TilePerMachine { get; set; } = true;

    public string? FormError { get; private set; }

    public IReadOnlyList<ElementPickerOption> PickerOptions { get; private set; } = [];

    public void OnGet() => LoadOptions();

    public IActionResult OnPost()
    {
        if (string.IsNullOrWhiteSpace(TargetToken))
        {
            FormError = "Pick the element or the tag the map is about.";
        }
        else if (!Overview && !TilePerMachine)
        {
            FormError = "Choose at least one thing to put on the map.";
        }
        else
        {
            try
            {
                var name = string.IsNullOrWhiteSpace(Name) ? "Generated map" : Name.Trim();
                var map = generator.Generate(name, TargetToken.Trim(), Overview, TilePerMachine);
                return RedirectToPage("/MapEditor", new { mapId = map.Id });
            }
            catch (InvalidOperationException exception)
            {
                FormError = exception.Message;
            }
        }

        LoadOptions();
        return Page();
    }

    private void LoadOptions()
    {
        var root = store.GetAllElements().FirstOrDefault(element => element.ParentId is null);
        PickerOptions = ElementPickerOptions.Build(root);
    }
}

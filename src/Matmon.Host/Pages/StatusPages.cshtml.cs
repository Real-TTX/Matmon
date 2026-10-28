using Matmon.Core.Domain;
using Matmon.Host.Services;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

public sealed class StatusComponentInput
{
    public Guid? Id { get; set; }

    public string? Name { get; set; }

    public string? Group { get; set; }

    public string? Description { get; set; }

    public string? TargetToken { get; set; }
}

public sealed class StatusPageInput
{
    public Guid? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool PublicEnabled { get; set; }

    public int UptimeDays { get; set; } = 90;

    public bool ShowMaintenance { get; set; } = true;

    public List<StatusComponentInput> Components { get; set; } = [];
}

public sealed class StatusIncidentInput
{
    public string? Title { get; set; }

    public StatusIncidentImpact Impact { get; set; } = StatusIncidentImpact.PartialOutage;

    public List<Guid> ComponentIds { get; set; } = [];

    public StatusIncidentPhase Phase { get; set; } = StatusIncidentPhase.Investigating;

    public string? Message { get; set; }
}

public sealed record StatusPageRow(StatusPage Page, StatusComponentState Overall, string OverallText);

/// <summary>
/// Maps > Status pages: the list, the editor (<c>?edit=new</c> / <c>?edit=&lt;id&gt;</c>) and a page as its readers
/// see it (<c>?view=&lt;id&gt;</c>) with the incident messages posted from there. Everyone may look; changes are an
/// admin's (the page write guard).
/// </summary>
public sealed class StatusPagesModel(IMonitoringWorkspaceStore store, StatusPageProvider provider) : PageModel
{
    /// <summary>Blank component rows the editor offers below the existing ones - the add affordance, no script needed.</summary>
    public const int BlankComponentRows = 3;

    [BindProperty(SupportsGet = true)]
    public string? Edit { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? View { get; set; }

    [BindProperty]
    public StatusPageInput Input { get; set; } = new();

    [BindProperty]
    public StatusIncidentInput Incident { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public string? FormError { get; private set; }

    public bool Editing => Edit is not null;

    public bool IsNew => Input.Id is null;

    public IReadOnlyList<StatusPageRow> Rows { get; private set; } = [];

    public StatusPage? Current { get; private set; }

    public StatusPageView? Preview { get; private set; }

    public IReadOnlyList<ElementPickerOption> PickerOptions { get; private set; } = [];

    public static IReadOnlyList<(StatusIncidentImpact Value, string Label)> Impacts { get; } =
    [
        (StatusIncidentImpact.Degraded, "Degraded"),
        (StatusIncidentImpact.PartialOutage, "Partial outage"),
        (StatusIncidentImpact.MajorOutage, "Major outage")
    ];

    public static IReadOnlyList<(StatusIncidentPhase Value, string Label)> Phases { get; } =
    [
        (StatusIncidentPhase.Investigating, "Investigating"),
        (StatusIncidentPhase.Identified, "Identified"),
        (StatusIncidentPhase.Monitoring, "Monitoring"),
        (StatusIncidentPhase.Resolved, "Resolved")
    ];

    public IActionResult OnGet()
    {
        if (View is { } viewId)
        {
            return LoadView(viewId) ? Page() : NotFoundRedirect();
        }

        if (Edit is null)
        {
            LoadList();
            return Page();
        }

        if (string.Equals(Edit, "new", StringComparison.OrdinalIgnoreCase))
        {
            Input = new StatusPageInput { Name = "Status" };
        }
        else if (Guid.TryParse(Edit, out var id) && store.FindStatusPage(id) is { } page)
        {
            Current = page;
            Input = ToInput(page);
        }
        else
        {
            return NotFoundRedirect();
        }

        LoadEditor();
        return Page();
    }

    public IActionResult OnPostSave()
    {
        var existing = Input.Id is { } id ? store.FindStatusPage(id) : null;
        if (Input.Id is not null && existing is null)
        {
            return NotFoundRedirect();
        }

        var page = existing ?? new StatusPage();
        page.Name = Input.Name ?? string.Empty;
        page.Description = Input.Description;
        page.PublicEnabled = Input.PublicEnabled;
        page.UptimeDays = Input.UptimeDays;
        page.ShowMaintenance = Input.ShowMaintenance;
        // A row without a name is a blank row the editor offered, not a component.
        page.Components = Input.Components
            .Where(row => !string.IsNullOrWhiteSpace(row.Name))
            .Select(row => new StatusPageComponent
            {
                Id = row.Id is { } rowId && rowId != Guid.Empty ? rowId : Guid.NewGuid(),
                Name = row.Name!,
                Group = row.Group,
                Description = row.Description,
                TargetToken = row.TargetToken
            })
            .ToList();

        try
        {
            var saved = store.SaveStatusPage(page);
            StatusMessage = $"Saved '{saved.Name}'.";
            return RedirectToPage(new { view = saved.Id, edit = (string?)null });
        }
        catch (InvalidOperationException exception)
        {
            FormError = exception.Message;
            Current = existing;
            LoadEditor();
            return Page();
        }
    }

    public IActionResult OnPostDelete(Guid id)
    {
        if (store.DeleteStatusPage(id))
        {
            StatusMessage = "Status page deleted.";
        }

        return RedirectToPage(new { edit = (string?)null, view = (Guid?)null });
    }

    public IActionResult OnPostRotate(Guid id)
    {
        StatusMessage = store.RotateStatusPageToken(id) is null
            ? null
            : "New public address - the old one no longer works.";
        return RedirectToPage(new { edit = id.ToString(), view = (Guid?)null });
    }

    public IActionResult OnPostIncident(Guid id)
    {
        try
        {
            store.PostStatusIncident(id, Incident.Title ?? string.Empty, Incident.Impact, Incident.ComponentIds, Incident.Phase, Incident.Message ?? string.Empty, User.Identity?.Name);
            StatusMessage = "Incident posted.";
            return RedirectToPage(new { view = id, edit = (string?)null });
        }
        catch (InvalidOperationException exception)
        {
            FormError = exception.Message;
            return LoadView(id) ? Page() : NotFoundRedirect();
        }
    }

    public IActionResult OnPostUpdate(Guid id, Guid incidentId, StatusIncidentPhase phase, string? message)
    {
        try
        {
            StatusMessage = store.AddStatusIncidentUpdate(id, incidentId, phase, message ?? string.Empty, User.Identity?.Name)
                ? "Update posted."
                : null;
            return RedirectToPage(new { view = id, edit = (string?)null });
        }
        catch (InvalidOperationException exception)
        {
            FormError = exception.Message;
            return LoadView(id) ? Page() : NotFoundRedirect();
        }
    }

    public IActionResult OnPostDeleteIncident(Guid id, Guid incidentId)
    {
        if (store.DeleteStatusIncident(id, incidentId))
        {
            StatusMessage = "Incident deleted.";
        }

        return RedirectToPage(new { view = id, edit = (string?)null });
    }

    /// <summary>The full public address (see PublicLinks) - the path alone only when the instance's address cannot be known here.</summary>
    public string PublicUrl(StatusPage page)
    {
        var path = Url.Page("/StatusPublic", null, new { token = page.PublicToken }) ?? $"/status/{page.PublicToken}";
        return PublicLinks.Absolute(HttpContext, store, path) ?? path;
    }

    private bool LoadView(Guid id)
    {
        var page = store.FindStatusPage(id);
        if (page is null)
        {
            return false;
        }

        Current = page;
        View = id;
        Preview = provider.Build(page, DateTimeOffset.UtcNow);
        return true;
    }

    private void LoadList()
    {
        Rows = store.GetStatusPages()
            .Select(page =>
            {
                var view = provider.BuildCached(page);
                return new StatusPageRow(page, view.Overall, view.OverallText);
            })
            .ToList();
    }

    private void LoadEditor()
    {
        var root = store.GetAllElements().FirstOrDefault(element => element.ParentId is null);
        PickerOptions = ElementPickerOptions.Build(root);
        Input.Components = Input.Components
            .Where(row => !string.IsNullOrWhiteSpace(row.Name))
            .Concat(Enumerable.Range(0, BlankComponentRows).Select(_ => new StatusComponentInput()))
            .ToList();
    }

    private IActionResult NotFoundRedirect()
    {
        ErrorMessage = "That status page no longer exists.";
        return RedirectToPage(new { edit = (string?)null, view = (Guid?)null });
    }

    private static StatusPageInput ToInput(StatusPage page) => new()
    {
        Id = page.Id,
        Name = page.Name,
        Description = page.Description,
        PublicEnabled = page.PublicEnabled,
        UptimeDays = page.UptimeDays,
        ShowMaintenance = page.ShowMaintenance,
        Components = page.Components.Select(component => new StatusComponentInput
        {
            Id = component.Id,
            Name = component.Name,
            Group = component.Group,
            Description = component.Description,
            TargetToken = component.TargetToken
        }).ToList()
    };
}

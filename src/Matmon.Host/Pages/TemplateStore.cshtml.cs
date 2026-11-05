using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>
/// Templates > Store: the template store of the connected Matmon Cloud - search it, read what a set contains, add it. Adding
/// fetches the file from the cloud and imports it exactly like an uploaded file (<see cref="TemplateTransfer.Read"/> checks and
/// strips it again, then the workspace's import gives the templates new ids and unique names). Admins only, like Import.
/// </summary>
public sealed class TemplateStoreModel(CloudTemplateStoreClient cloud, IMonitoringWorkspaceStore workspace) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Tag { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;

    /// <summary>The set whose details are open (<c>?show=slug</c>).</summary>
    [BindProperty(SupportsGet = true)] public string? Show { get; set; }

    public CloudTemplateStoreClient.StorePage? Catalogue { get; private set; }
    public CloudTemplateStoreClient.StoreDetail? Detail { get; private set; }
    public bool Connected { get; private set; }

    /// <summary>Why the store could not be read (not connected, cloud unreachable, an older cloud).</summary>
    public string? Problem { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    /// <summary>Names of the templates this instance already has - a set whose names are all here is marked "added".</summary>
    public IReadOnlySet<string> ExistingNames { get; private set; } = new HashSet<string>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Connected = cloud.IsConnected;
        ExistingNames = workspace.GetAllTemplates().Select(template => template.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            Catalogue = await cloud.ListAsync(Q, Tag, Sort, Math.Max(1, PageNumber), cancellationToken);
            if (!string.IsNullOrWhiteSpace(Show))
            {
                Detail = await cloud.GetAsync(Show, cancellationToken);
            }
        }
        catch (CloudTemplateStoreClient.StoreUnavailableException ex)
        {
            Problem = ex.Message;
        }
    }

    public async Task<IActionResult> OnPostAddAsync(string slug, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await cloud.FileAsync(slug, cancellationToken);
            if (bytes is null)
            {
                ErrorMessage = "This template set is no longer in the store.";
                return RedirectToPage(new { Q, Tag, Sort, p = PageNumber });
            }

            var read = TemplateTransfer.Read(
                bytes,
                workspace.GetAllTemplates().Select(template => template.Name).ToArray(),
                workspace.GetSensorDefinitions().Select(definition => definition.Key).ToHashSet(StringComparer.OrdinalIgnoreCase),
                workspace.GetSecretParameterKeys());
            var added = workspace.ImportTemplates(read.Templates);
            StatusMessage = $"Added {added.Count} template{(added.Count == 1 ? string.Empty : "s")} from the store: {string.Join(", ", added.Select(template => template.Name))}."
                + (read.Notes.Count == 0 ? string.Empty : " " + string.Join(" ", read.Notes));
            return RedirectToPage("/Templates");
        }
        catch (CloudTemplateStoreClient.StoreUnavailableException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (InvalidDataException ex)
        {
            ErrorMessage = "The file from the store could not be imported: " + ex.Message;
        }

        return RedirectToPage(new { Q, Tag, Sort, p = PageNumber, show = slug });
    }

    /// <summary>The address of this page with one value changed (search, keyword, sort, page and open set are kept).</summary>
    public string Link(string? tag = null, string? sort = null, int? page = null, string? show = null, bool clearTag = false)
    {
        var parts = new List<string>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) parts.Add(key + "=" + Uri.EscapeDataString(value));
        }

        Add("q", Q);
        Add("tag", clearTag ? null : tag ?? Tag);
        Add("sort", sort ?? Sort);
        var p = page ?? 1;
        if (p > 1) parts.Add("p=" + p);
        Add("show", show);
        return "/templates/store" + (parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts));
    }

    /// <summary>Whether every template of a set already exists here by name (it was most likely added before).</summary>
    public bool AlreadyAdded(CloudTemplateStoreClient.StoreDetail detail) =>
        detail.Templates.Count > 0 && detail.Templates.All(template => ExistingNames.Contains(template.Name));
}

using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>
/// Templates > Store: the template store of the connected Matmon Cloud - search it, read what a set contains (its scripts
/// included), add it, and bring a set added before to the store's newer version. Adding fetches the file from the cloud and imports
/// it exactly like an uploaded file (<see cref="TemplateTransfer.Read"/> checks and strips it again, then the workspace's import
/// gives the templates new ids and unique names, remembering the set and version). Admins only, like Import.
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

    /// <summary>The store sets added here (slug -> version, template count).</summary>
    public IReadOnlyDictionary<string, (string? Version, int Count)> Installed { get; private set; } = new Dictionary<string, (string?, int)>();

    /// <summary>The sets added here for which the store has another version now.</summary>
    public IReadOnlyList<TemplateStoreSet> Updates { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Connected = cloud.IsConnected;
        ExistingNames = workspace.GetAllTemplates().Select(template => template.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Installed = TemplateStoreUpdates.Installed(workspace);
        try
        {
            Catalogue = await cloud.ListAsync(Q, Tag, Sort, Math.Max(1, PageNumber), cancellationToken);
            if (!string.IsNullOrWhiteSpace(Show))
            {
                Detail = await cloud.GetAsync(Show, cancellationToken);
            }

            Updates = (await TemplateStoreUpdates.FindAsync(workspace, cloud, cancellationToken)).Where(set => set.UpdateAvailable).ToList();
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
            var file = await cloud.FileAsync(slug, cancellationToken);
            if (file is null)
            {
                ErrorMessage = "This template set is no longer in the store.";
                return RedirectToPage(new { Q, Tag, Sort, p = PageNumber });
            }

            var read = Read(file, workspace.GetAllTemplates().Select(template => template.Name).ToArray());
            var added = workspace.ImportTemplates(read.Templates, slug, file.Version);
            cloud.ForgetVersions();
            StatusMessage = $"Added {added.Count} template{(added.Count == 1 ? string.Empty : "s")} from the store{(file.Version is null ? "" : " (version " + file.Version + ")")}: "
                + string.Join(", ", added.Select(template => template.Name)) + "."
                + ScriptNote(read.Templates)
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

    /// <summary>
    /// Brings the templates added from a set to the store's current version in place (same ids, names and filled-in secrets; new
    /// templates of the set are added). The sensors made from them stay as they are - "Restore from template" applies the new values.
    /// </summary>
    public async Task<IActionResult> OnPostUpdateAsync(string slug, CancellationToken cancellationToken)
    {
        try
        {
            var file = await cloud.FileAsync(slug, cancellationToken);
            if (file is null)
            {
                ErrorMessage = "This template set is no longer in the store - the templates added from it stay as they are.";
                return RedirectToPage(new { Q, Tag, Sort, p = PageNumber });
            }

            // Names are made unique against everything EXCEPT this set's own templates, so its names stay and match.
            var others = workspace.GetAllTemplates()
                .Where(template => !string.Equals(template.StoreSlug, slug, StringComparison.OrdinalIgnoreCase))
                .Select(template => template.Name).ToArray();
            var read = Read(file, others);
            var result = workspace.UpdateTemplatesFromStore(slug, file.Version, read.Templates);
            cloud.ForgetVersions();
            StatusMessage = $"Updated to {(file.Version is null ? "the store's current version" : "version " + file.Version)}: "
                + (result.Updated.Count == 0 ? "no template changed in place" : string.Join(", ", result.Updated)) + "."
                + (result.Added.Count == 0 ? string.Empty : $" New in this version: {string.Join(", ", result.Added)}.")
                + (result.NoLongerInSet.Count == 0 ? string.Empty : $" No longer in the set (left as they are): {string.Join(", ", result.NoLongerInSet)}.")
                + " Sensors made from them keep their values until you use \"Restore from template\" on them."
                + ScriptNote(read.Templates);
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

    private TemplateTransfer.ImportResult Read(CloudTemplateStoreClient.StoreFile file, IReadOnlyCollection<string> existingNames) =>
        TemplateTransfer.Read(
            file.Bytes,
            existingNames,
            workspace.GetSensorDefinitions().Select(definition => definition.Key).ToHashSet(StringComparer.OrdinalIgnoreCase),
            workspace.GetSecretParameterKeys());

    /// <summary>The scripts the file carries, counted HERE from the file itself (not from what the cloud said).</summary>
    private static string ScriptNote(IReadOnlyList<Matmon.Core.Domain.MonitoringTemplate> templates)
    {
        var scripts = TemplateTransfer.Scripts(templates);
        if (scripts.Count == 0)
        {
            return string.Empty;
        }

        var names = scripts.Select(script => script.Template).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return $" Contains script{(scripts.Count == 1 ? "" : "s")} ({string.Join(", ", names)}) - a sensor made from {(names.Count == 1 ? "it" : "them")} runs the script; read it on the template first.";
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
        InstalledVersion(detail.Slug) is not null
        || (detail.Templates.Count > 0 && detail.Templates.All(template => ExistingNames.Contains(template.Name)));

    /// <summary>The version of a set added here ("" for one added before versions were remembered), null when not added.</summary>
    public string? InstalledVersion(string slug) =>
        Installed.TryGetValue(slug.Trim().ToLowerInvariant(), out var installed) ? installed.Version ?? string.Empty : null;
}

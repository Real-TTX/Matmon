using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>System → SNMP MIBs: the shipped standard set plus uploads, with what each module is missing.</summary>
// On the page model, not on the upload handler: RequestSizeLimit is ignored on a handler method (MVC1001), which
// left the attribute that used to sit there doing nothing.
[RequestSizeLimit(25 * 1024 * 1024)]
public sealed class MibsModel(MibLibrary mibs) : PageModel
{
    public IReadOnlyList<Matmon.Core.Domain.MibModuleStatus> Modules { get; private set; } = [];
    public IReadOnlyDictionary<string, DateTimeOffset> UploadedAt { get; private set; } = new Dictionary<string, DateTimeOffset>();
    public int NodeCount { get; private set; }

    /// <summary>Modules some loaded module imports from but which are not loaded - what to upload next.</summary>
    public IReadOnlyList<(string Module, IReadOnlyList<string> NeededBy)> Missing { get; private set; } = [];

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public void OnGet()
    {
        Modules = mibs.Registry.Modules;
        UploadedAt = mibs.UploadedAt;
        NodeCount = mibs.Registry.NodeCount;
        Missing = Modules
            .SelectMany(module => module.MissingImports.Select(missing => (Missing: missing, By: module.Name)))
            .GroupBy(pair => pair.Missing)
            .Select(group => (group.Key, (IReadOnlyList<string>)group.Select(pair => pair.By).Order().ToList()))
            .OrderBy(pair => pair.Key)
            .ToList();
    }

    /// <summary>Upload one or more MIB files or zips. Answers JSON when called from the walk (fetch), else redirects.</summary>
    public async Task<IActionResult> OnPostUploadAsync(List<IFormFile> files, CancellationToken cancellationToken)
    {
        var contents = new List<(string, byte[])>();
        foreach (var file in files.Where(file => file.Length > 0))
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, cancellationToken);
            contents.Add((Path.GetFileName(file.FileName), buffer.ToArray()));
        }

        var result = contents.Count == 0
            ? new MibUploadResult([], ["Choose at least one MIB file."])
            : mibs.Upload(contents);

        if (Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonResult(new
            {
                added = result.Added,
                rejected = result.Rejected,
                modules = mibs.Registry.Modules.Count,
                missing = mibs.Registry.Modules.Where(module => result.Added.Contains(module.Name)).SelectMany(module => module.MissingImports).Distinct().ToList()
            });
        }

        if (result.Added.Count > 0)
        {
            StatusMessage = $"Loaded {string.Join(", ", result.Added)}.";
        }
        if (result.Rejected.Count > 0)
        {
            ErrorMessage = string.Join(" ", result.Rejected);
        }
        return RedirectToPage();
    }

    public IActionResult OnPostDelete(string module)
    {
        if (mibs.Delete(module))
        {
            StatusMessage = $"Removed {module}.";
        }
        else
        {
            ErrorMessage = $"{module} is not an uploaded MIB.";
        }
        return RedirectToPage();
    }
}

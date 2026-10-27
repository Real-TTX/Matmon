using System.Globalization;
using Matmon.Core.Domain;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>System > API tokens > New token (admins): a token is made on a page of its own (CRUD as a sub-page); the list
/// shows the secret once afterwards.</summary>
public sealed class SystemApiTokenCreateModel(IMonitoringWorkspaceStore store) : PageModel
{
    /// <summary>The expiries offered (days); "never" = until revoked, "date" = the date field.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> ExpiryOptions =
    [
        ("30", "30 days"), ("90", "90 days"), ("365", "1 year"), ("date", "On a date"), ("never", "Never (until revoked)")
    ];

    [BindProperty] public string Name { get; set; } = string.Empty;
    [BindProperty] public List<string> Scopes { get; set; } = [ApiTokenScopes.Maintenance];
    [BindProperty] public string Expiry { get; set; } = "90";
    [BindProperty] public DateOnly? ExpiresOn { get; set; }

    /// <summary>Handed to the list, which shows the secret once.</summary>
    [TempData] public string? NewSecret { get; set; }
    [TempData] public string? NewTokenName { get; set; }

    public string? FormError { get; private set; }

    public void OnGet()
    {
    }

    public IActionResult OnPost()
    {
        DateTimeOffset? expires;
        var now = DateTimeOffset.UtcNow;
        switch (Expiry)
        {
            case "never":
                expires = null;
                break;
            case "date":
                if (ExpiresOn is not { } day)
                {
                    return Refuse("Choose the date the token expires.");
                }
                expires = new DateTimeOffset(day.ToDateTime(new TimeOnly(23, 59)), TimeSpan.Zero);
                break;
            default:
                if (!int.TryParse(Expiry, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) || days is <= 0 or > 730)
                {
                    return Refuse("Choose when the token expires.");
                }
                expires = now.AddDays(days);
                break;
        }

        try
        {
            var issue = store.CreateApiToken(Name, Scopes, expires, User.Identity?.Name);
            NewSecret = issue.Secret;
            NewTokenName = issue.Token.Name;
            return RedirectToPage("/SystemApiTokens");
        }
        catch (InvalidOperationException ex)
        {
            return Refuse(ex.Message);
        }
    }

    private IActionResult Refuse(string message)
    {
        FormError = message;
        return Page();
    }
}

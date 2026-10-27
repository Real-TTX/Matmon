using System.Globalization;
using Matmon.Core.Domain;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>
/// System > API tokens (admins): the tokens a CI/CD job or a script presents to the instance's own API
/// (<c>/api/v1/...</c>) - each with a name, what it may do (scopes), an optional expiry, when and from where it was last
/// used. The secret is shown once, right after it was made; afterwards only its first characters. Revoking stops it at once.
/// </summary>
public sealed class SystemApiTokensModel(IMonitoringWorkspaceStore store) : PageModel
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

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    /// <summary>The secret of the token just made - shown on this one page view, never stored in the clear.</summary>
    [TempData] public string? NewSecret { get; set; }
    [TempData] public string? NewTokenName { get; set; }

    public string? FormError { get; private set; }

    public IReadOnlyList<ApiToken> Tokens { get; private set; } = [];

    /// <summary>The instance's own address, for the examples.</summary>
    public string BaseUrl => $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

    public void OnGet() => Load();

    public IActionResult OnPostCreate()
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
                expires = int.TryParse(Expiry, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days is > 0 and <= 730
                    ? now.AddDays(days)
                    : null;
                if (expires is null)
                {
                    return Refuse("Choose when the token expires.");
                }
                break;
        }

        try
        {
            var issue = store.CreateApiToken(Name, Scopes, expires, User.Identity?.Name);
            NewSecret = issue.Secret;
            NewTokenName = issue.Token.Name;
            return RedirectToPage();
        }
        catch (InvalidOperationException ex)
        {
            return Refuse(ex.Message);
        }
    }

    public IActionResult OnPostRevoke(Guid id)
    {
        if (store.RevokeApiToken(id))
        {
            StatusMessage = "Token revoked. Whatever uses it is turned away from now on.";
        }
        else
        {
            ErrorMessage = "That token is revoked already or no longer exists.";
        }
        return RedirectToPage();
    }

    public IActionResult OnPostDelete(Guid id)
    {
        StatusMessage = store.DeleteApiToken(id) ? "Token removed from the list." : null;
        return RedirectToPage();
    }

    private IActionResult Refuse(string message)
    {
        FormError = message;
        Load();
        return Page();
    }

    private void Load() => Tokens = store.GetApiTokens();

    public static string State(ApiToken token, DateTimeOffset now) =>
        token.RevokedUtc is not null ? "Revoked" : token.IsExpired(now) ? "Expired" : "Active";

    public static string Tone(ApiToken token, DateTimeOffset now) =>
        token.RevokedUtc is not null ? "neutral" : token.IsExpired(now) ? "warn" : "ok";
}

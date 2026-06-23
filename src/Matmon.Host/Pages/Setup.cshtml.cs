using System.Security.Claims;
using Matmon.Core.Domain;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

[AllowAnonymous]
public class SetupModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;

    public SetupModel(IMonitoringWorkspaceStore workspaceStore)
    {
        _workspaceStore = workspaceStore;
    }

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public string ConfirmPassword { get; set; } = string.Empty;

    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet()
    {
        // Setup is a one-time, first-run page. Once an account exists, send people to login.
        if (!_workspaceStore.IsSetupRequired())
        {
            return RedirectToPage("/Login");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!_workspaceStore.IsSetupRequired())
        {
            return RedirectToPage("/Login");
        }

        var email = Email?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            ErrorMessage = "Please enter a valid e-mail address.";
            return Page();
        }

        if (string.IsNullOrWhiteSpace(Password) || Password.Length < 8)
        {
            ErrorMessage = "The password must be at least 8 characters.";
            return Page();
        }

        if (!string.Equals(Password, ConfirmPassword, StringComparison.Ordinal))
        {
            ErrorMessage = "The passwords do not match.";
            return Page();
        }

        MatmonUser admin;
        try
        {
            admin = _workspaceStore.CompleteInitialSetup(email, Password);
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, admin.Id.ToString()),
            new(ClaimTypes.Name, admin.Username),
            new(ClaimTypes.Role, admin.Role.ToString())
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                AllowRefresh = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12)
            });

        // The optional setup wizard (networks / discovery / probes / notifications) lands here next;
        // for now go straight to the dashboard.
        return RedirectToPage("/Index");
    }
}

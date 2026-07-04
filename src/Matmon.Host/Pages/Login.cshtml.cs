using System.Security.Claims;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;

    public LoginModel(IMonitoringWorkspaceStore workspaceStore)
    {
        _workspaceStore = workspaceStore;
    }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? Error { get; set; }

    public string? ErrorMessage { get; private set; }

    /// <summary>Whether "Sign in with Matmon Cloud" can be offered (the cloud link is configured + enabled).</summary>
    public bool CloudSsoAvailable { get; private set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToLocal(ReturnUrl);
        }

        var cloud = _workspaceStore.GetCloudConnectionSettings();
        CloudSsoAvailable = cloud.Enabled && !string.IsNullOrWhiteSpace(cloud.Url) && !string.IsNullOrWhiteSpace(cloud.InstanceId);

        ErrorMessage = Error switch
        {
            "cloud-not-connected" => "Matmon.Cloud sign-in isn't available — this instance isn't connected to the cloud.",
            "cloud-denied" => "Cloud sign-in was cancelled.",
            "cloud-state" => "Cloud sign-in expired or was invalid — please try again.",
            "cloud-exchange" => "Cloud sign-in failed. Check that your account has access to this instance.",
            _ => ErrorMessage
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var enteredEmail = Email?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(enteredEmail) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "E-mail and password are required.";
            return Page();
        }

        // ValidateUser matches the e-mail (or a legacy username) so older accounts still work.
        var user = _workspaceStore.ValidateUser(enteredEmail, Password);
        if (user is null)
        {
            ErrorMessage = "Invalid credentials.";
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role.ToString())
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

        return RedirectToLocal(ReturnUrl);
    }

    private IActionResult RedirectToLocal(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        return RedirectToPage("/Index");
    }
}

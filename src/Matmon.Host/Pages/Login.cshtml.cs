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
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToLocal(ReturnUrl);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var enteredUsername = Username?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(enteredUsername) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Username and password are required.";
            return Page();
        }

        var user = _workspaceStore.ValidateUser(enteredUsername, Password);
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

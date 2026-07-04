using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Matmon.Core.Domain;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>
/// "Sign in with Matmon Cloud" (OAuth2 authorization-code, cloud = IdP).
/// <c>?handler=Start</c> redirects to the cloud authorize endpoint (with a CSRF state cookie);
/// the plain callback exchanges the returned code for the identity (using the instance token as the
/// client secret), maps the cloud role to a local role, provisions/links the local user and signs in.
/// </summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public class SigninCloudModel : PageModel
{
    private const string StateCookie = "matmon_cloud_oauth_state";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly ILogger<SigninCloudModel> _logger;

    public SigninCloudModel(IMonitoringWorkspaceStore workspaceStore, ILogger<SigninCloudModel> logger)
    {
        _workspaceStore = workspaceStore;
        _logger = logger;
    }

    public IActionResult OnGetStart()
    {
        var settings = _workspaceStore.GetCloudConnectionSettings();
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Url) || string.IsNullOrWhiteSpace(settings.InstanceId))
        {
            return RedirectToPage("/Login", new { error = "cloud-not-connected" });
        }

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        Response.Cookies.Append(StateCookie, state, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(10)
        });

        var cloudBase = settings.Url!.Trim().TrimEnd('/');
        var redirectUri = RedirectUri();
        var authorizeUrl = $"{cloudBase}/oauth/authorize" +
            $"?instance_id={Uri.EscapeDataString(settings.InstanceId!.Trim())}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&state={Uri.EscapeDataString(state)}";
        return Redirect(authorizeUrl);
    }

    public async Task<IActionResult> OnGetAsync(string? code, string? state, string? error, CancellationToken cancellationToken)
    {
        var expectedState = Request.Cookies[StateCookie];
        Response.Cookies.Delete(StateCookie);

        if (!string.IsNullOrEmpty(error))
        {
            return RedirectToPage("/Login", new { error = "cloud-denied" });
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state) ||
            string.IsNullOrWhiteSpace(expectedState) || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(state), System.Text.Encoding.UTF8.GetBytes(expectedState)))
        {
            return RedirectToPage("/Login", new { error = "cloud-state" });
        }

        var settings = _workspaceStore.GetCloudConnectionSettings();
        var token = _workspaceStore.GetCloudConnectionToken();
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Url) || string.IsNullOrWhiteSpace(settings.InstanceId) || string.IsNullOrWhiteSpace(token))
        {
            return RedirectToPage("/Login", new { error = "cloud-not-connected" });
        }

        try
        {
            var cloudBase = settings.Url!.Trim().TrimEnd('/');
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{cloudBase}/oauth/token")
            {
                Content = JsonContent.Create(new TokenRequest(settings.InstanceId!.Trim(), code, RedirectUri()))
            };
            request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", token);

            using var response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Cloud SSO token exchange failed: {Status}", response.StatusCode);
                return RedirectToPage("/Login", new { error = "cloud-exchange" });
            }

            var identity = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            if (identity is null || string.IsNullOrWhiteSpace(identity.Email))
            {
                return RedirectToPage("/Login", new { error = "cloud-exchange" });
            }

            var user = _workspaceStore.UpsertCloudUser(identity.Email, MapRole(identity.Role));
            await SignInAsync(user);
            _logger.LogInformation("Cloud SSO sign-in for {Email} as {Role}", user.Email, user.Role);
            return RedirectToPage("/Index");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cloud SSO sign-in failed");
            return RedirectToPage("/Login", new { error = "cloud-exchange" });
        }
    }

    private string RedirectUri() => $"{Request.Scheme}://{Request.Host}/signin-cloud";

    private static MatmonUserRole MapRole(string? cloudRole) => cloudRole switch
    {
        "Owner" or "Admin" => MatmonUserRole.Admin,
        _ => MatmonUserRole.Viewer
    };

    private async Task SignInAsync(MatmonUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(user.Email) ? user.Username : user.Email),
            new(ClaimTypes.Role, user.Role.ToString())
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = true, AllowRefresh = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12) });
    }

    private sealed record TokenRequest(string InstanceId, string Code, string RedirectUri);

    private sealed record TokenResponse(string Email, string Role);
}

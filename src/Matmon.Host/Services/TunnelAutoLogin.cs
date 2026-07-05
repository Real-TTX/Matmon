using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Matmon.Core.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Matmon.Host.Services;

/// <summary>
/// Per-process shared secret the Full Access <see cref="TunnelClient"/> stamps on every request it replays
/// locally (the <c>X-Matmon-Tunnel-Auth</c> header). It is random per start and never leaves the process, so a
/// request carrying it is proven to have arrived through <em>this</em> instance's own tunnel — not forged by a
/// direct caller against the (publicly reachable) instance. This is the trust anchor for the auto-login
/// middleware: the cloud's identity assertion is only honoured when accompanied by this secret.
/// </summary>
public sealed class TunnelAuthSecret
{
    public string Value { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}

/// <summary>
/// Seamless Full Access sign-in. When a cloud user opens the instance console through the tunnel they are
/// already authenticated on the cloud and a member of the instance, so the cloud injects an identity assertion
/// (<c>X-Matmon-Cloud-User = "email|role"</c>) that this middleware turns into a local sign-in — no second
/// login, no OAuth round-trip inside the iframe.
/// </summary>
public static class TunnelAutoLogin
{
    public const string CloudUserHeader = "X-Matmon-Cloud-User";
    public const string TunnelAuthHeader = "X-Matmon-Tunnel-Auth";

    /// <summary>
    /// Register between <c>UseAuthentication</c> and <c>UseAuthorization</c>. Every request that arrives through
    /// the tunnel carries the cloud's identity assertion + our tunnel secret, so we authenticate the request
    /// <em>from the assertion itself</em> — we do NOT rely on the auth cookie round-tripping through the
    /// cross-origin console iframe (that dependency made sign-in "stick only after navigating back and forth").
    /// A backup cookie is still issued once per identity for any rare assertion-less follow-up. No secret / wrong
    /// secret / no assertion ⇒ no-op.
    /// </summary>
    public static IApplicationBuilder UseTunnelAutoLogin(this WebApplication app) => app.Use(async (context, next) =>
    {
        if (TryReadTrustedAssertion(context, out var email, out var role))
        {
            try
            {
                var store = context.RequestServices.GetRequiredService<IMonitoringWorkspaceStore>();
                var user = store.UpsertCloudUser(email, role);
                var principal = BuildPrincipal(user);

                // Did this request already carry a valid cookie for the SAME user?
                var alreadySignedIn = context.User?.Identity?.IsAuthenticated == true &&
                    string.Equals(context.User.Identity!.Name, principal.Identity!.Name, StringComparison.OrdinalIgnoreCase);

                // Authenticate THIS request straight from the trusted assertion — no cookie dependency.
                context.User = principal;

                // Only (re)issue the backup cookie when the identity actually changed, so we don't emit a
                // Set-Cookie on every AJAX poll.
                if (!alreadySignedIn)
                {
                    await context.SignInAsync(
                        CookieAuthenticationDefaults.AuthenticationScheme,
                        principal,
                        new AuthenticationProperties { IsPersistent = false, AllowRefresh = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12) });
                }
            }
            catch (Exception ex)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(TunnelAutoLogin))
                    .LogWarning(ex, "Full Access auto-login failed for {Email}", email);
            }
        }

        await next();
    });

    private static bool TryReadTrustedAssertion(HttpContext context, out string email, out MatmonUserRole role)
    {
        email = string.Empty;
        role = MatmonUserRole.Viewer;

        var assertion = context.Request.Headers[CloudUserHeader].ToString();
        var presented = context.Request.Headers[TunnelAuthHeader].ToString();
        if (string.IsNullOrEmpty(assertion) || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        // The assertion is only trustworthy when it arrived through OUR tunnel: a direct caller to the instance
        // cannot know this in-process secret, so it cannot forge an identity by setting the headers itself.
        var expected = context.RequestServices.GetRequiredService<TunnelAuthSecret>().Value;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected)))
        {
            return false;
        }

        var sep = assertion.IndexOf('|');
        if (sep <= 0)
        {
            return false;
        }

        email = assertion[..sep].Trim();
        var roleText = assertion[(sep + 1)..].Trim();
        if (email.Length == 0 || !email.Contains('@'))
        {
            return false;
        }

        role = MapRole(roleText);
        return true;
    }

    // Mirror of SigninCloudModel.MapRole: cloud Owner/Admin → local Admin, everything else → Viewer.
    private static MatmonUserRole MapRole(string? cloudRole) => cloudRole switch
    {
        "Owner" or "Admin" => MatmonUserRole.Admin,
        _ => MatmonUserRole.Viewer
    };

    private static ClaimsPrincipal BuildPrincipal(MatmonUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(user.Email) ? user.Username : user.Email),
            new(ClaimTypes.Role, user.Role.ToString())
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}

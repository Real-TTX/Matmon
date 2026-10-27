using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Matmon.Core;
using Microsoft.AspNetCore.DataProtection;

namespace Matmon.Host.Services;

/// <summary>
/// The cloud CLAIM flow (OAuth-style, PKCE): the browser goes to the cloud's consent page and comes back with a one-time
/// code, which the instance redeems - with the verifier only it holds - for its id + token. Two places start it: System →
/// Cloud (and the setup wizard through it), whose callback is <c>/CloudClaim</c>, and the first-run setup "with Matmon
/// Cloud", whose callback is <c>/setup/cloud</c> and which also signs in the account that connected.
/// </summary>
public static class CloudClaimFlow
{
    public const string CookieName = "matmon_cloud_claim";
    public const string ProtectorPurpose = "Matmon.CloudClaim.v1";

    private static readonly HttpClient CloudHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>The pending claim kept (data-protected) between the redirect out and the callback. <paramref name="ReturnUrl"/>
    /// = where to go after the claim; <paramref name="ForSetup"/> = started by the first-run setup (only its callback takes it).</summary>
    public sealed record State(string Nonce, string Verifier, string Url, string? ReturnUrl = null, bool ForSetup = false);

    /// <param name="AccountEmail">Who connected the instance and <paramref name="Role"/> their access (newer clouds only).</param>
    public sealed record ExchangeResult(string InstanceId, string Token, bool RemoteAccess, string? AccountEmail, string? Role);

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>PKCE S256 challenge: base64url(SHA-256(verifier)). Must match the cloud's ClaimCodeStore.</summary>
    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Checks a cloud address the way every connect does: absolute http(s), and https for a public host (a
    /// plain-http link would carry the sign-in unencrypted). Null = fine, else why not.</summary>
    public static string? CheckCloudUrl(string url, out Uri? cloudUri)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out cloudUri)
            || (cloudUri.Scheme != Uri.UriSchemeHttp && cloudUri.Scheme != Uri.UriSchemeHttps))
        {
            cloudUri = null;
            return "The cloud URL is not a valid address.";
        }

        return CloudUrlPolicy.IsPlainHttpAllowed(cloudUri)
            ? null
            : "Use https for a public cloud address - a plain-http link would send the sign-in over the network unencrypted.";
    }

    /// <summary>Starts a claim: stores the pending state in a short-lived protected cookie and returns the cloud's consent
    /// address to redirect the browser to. <paramref name="url"/> must have passed <see cref="CheckCloudUrl"/>.</summary>
    public static string Start(HttpContext http, IDataProtectionProvider dataProtection, string url, string name, string callbackPath,
        string? returnUrl = null, string? reconnectInstanceId = null, bool forSetup = false)
    {
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(24));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var payload = JsonSerializer.Serialize(new State(nonce, verifier, url, returnUrl, forSetup));
        http.Response.Cookies.Append(CookieName, dataProtection.CreateProtector(ProtectorPurpose).Protect(payload), new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            MaxAge = TimeSpan.FromMinutes(10)
        });

        var redirectUri = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}{callbackPath}";
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString($"{url}/instances/claim", new Dictionary<string, string?>
        {
            ["redirect_uri"] = redirectUri,
            ["state"] = nonce,
            ["name"] = name,
            ["code_challenge"] = Challenge(verifier),
            // Only a "disconnect & reconnect" knows which instance it is re-linking; a fresh install sends nothing,
            // and the cloud then asks the admin instead of guessing from the name.
            ["reconnect"] = Guid.TryParse(reconnectInstanceId, out var reconnectId) ? reconnectId.ToString() : null
        });
    }

    /// <summary>Reads (and always deletes) the pending claim cookie. Null when missing, tampered or the state differs.</summary>
    public static State? TakePending(HttpContext http, IDataProtectionProvider dataProtection, string? state)
    {
        var cookie = http.Request.Cookies[CookieName];
        http.Response.Cookies.Delete(CookieName);
        if (string.IsNullOrWhiteSpace(cookie) || string.IsNullOrWhiteSpace(state))
        {
            return null;
        }

        State? pending;
        try
        {
            pending = JsonSerializer.Deserialize<State>(dataProtection.CreateProtector(ProtectorPurpose).Unprotect(cookie));
        }
        catch
        {
            return null;
        }

        return pending is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(pending.Nonce), Encoding.ASCII.GetBytes(state))
            ? pending
            : null;
    }

    /// <summary>Redeems the code. Returns the result, or null with <paramref name="error"/> saying why.</summary>
    public static async Task<(ExchangeResult? Result, string? Error)> ExchangeAsync(State pending, string code, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await CloudHttp.PostAsJsonAsync($"{pending.Url}/api/instances/claim/exchange",
                new { code, codeVerifier = pending.Verifier }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (null, $"Matmon.Cloud rejected the connection ({(int)response.StatusCode}). Please try again.");
            }

            var result = await response.Content.ReadFromJsonAsync<ExchangeResult>(cancellationToken);
            return result is null || string.IsNullOrWhiteSpace(result.InstanceId) || string.IsNullOrWhiteSpace(result.Token)
                ? (null, "Matmon.Cloud returned an unexpected response.")
                : (result, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return (null, $"Could not reach Matmon.Cloud: {ex.Message}");
        }
    }
}

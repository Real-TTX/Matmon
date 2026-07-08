using Matmon.Host.Services;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Matmon.Host.Pages;

/// <summary>
/// Callback for the OAuth-style cloud CLAIM flow (started by <c>Config.OnPostCloudClaim</c>). The cloud sends
/// the browser back here with a one-time code; we validate the state against the data-protected cookie, redeem
/// the code (with the PKCE verifier) for the instance id + token, and store the cloud link. Admin-only.
/// </summary>
public class CloudClaimModel : PageModel
{
    private static readonly HttpClient CloudHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly IDataProtectionProvider _dataProtection;

    public CloudClaimModel(IMonitoringWorkspaceStore workspaceStore, IDataProtectionProvider dataProtection)
    {
        _workspaceStore = workspaceStore;
        _dataProtection = dataProtection;
    }

    public async Task<IActionResult> OnGetAsync(string? code, string? state, CancellationToken cancellationToken)
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        // Always clear the one-shot cookie, whatever the outcome.
        var cookie = Request.Cookies[CloudClaimFlow.CookieName];
        Response.Cookies.Delete(CloudClaimFlow.CookieName);

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(cookie))
        {
            return Fail("The cloud connection was cancelled or the link expired. Please try again.");
        }

        CloudClaimFlow.State? pending;
        try
        {
            var json = _dataProtection.CreateProtector(CloudClaimFlow.ProtectorPurpose).Unprotect(cookie);
            pending = JsonSerializer.Deserialize<CloudClaimFlow.State>(json);
        }
        catch
        {
            pending = null;
        }

        if (pending is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(pending.Nonce), Encoding.ASCII.GetBytes(state)))
        {
            return Fail("The cloud connection could not be verified (state mismatch). Please try again.");
        }

        try
        {
            using var response = await CloudHttp.PostAsJsonAsync(
                $"{pending.Url}/api/instances/claim/exchange",
                new { code, codeVerifier = pending.Verifier },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return Fail($"Matmon.Cloud rejected the connection ({(int)response.StatusCode}). Please try again.", pending.ReturnUrl);
            }

            var result = await response.Content.ReadFromJsonAsync<ClaimExchangeResult>(cancellationToken);
            if (result is null || string.IsNullOrWhiteSpace(result.InstanceId) || string.IsNullOrWhiteSpace(result.Token))
            {
                return Fail("Matmon.Cloud returned an unexpected response.", pending.ReturnUrl);
            }

            _workspaceStore.SetCloudConnectionSettings(pending.Url, result.InstanceId, result.Token, enabled: true);
            TempData["StatusMessage"] = "Connected to Matmon.Cloud - the first heartbeat is sent within a few seconds.";
        }
        catch (Exception ex)
        {
            return Fail($"Could not reach Matmon.Cloud: {ex.Message}", pending.ReturnUrl);
        }

        return Done(pending.ReturnUrl);
    }

    /// <summary>Return to where the flow started - the setup wizard when it kicked off the claim, else Cloud settings.</summary>
    private IActionResult Done(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToPage("/Config", new { tab = "cloud" });

    private IActionResult Fail(string message, string? returnUrl = null)
    {
        TempData["ErrorMessage"] = message;
        return Done(returnUrl);
    }

    private sealed record ClaimExchangeResult(string? InstanceId, string? Token);
}

/// <summary>Shared constants + PKCE helpers for the cloud claim flow (start handler + callback).</summary>
internal static class CloudClaimFlow
{
    public const string CookieName = "matmon_cloud_claim";
    public const string ProtectorPurpose = "Matmon.CloudClaim.v1";

    /// <summary>The pending claim kept (data-protected) between the redirect out and the callback.
    /// <paramref name="ReturnUrl"/> carries where to send the browser after the claim (e.g. back into the
    /// setup wizard); null falls back to the System → Cloud page.</summary>
    public sealed record State(string Nonce, string Verifier, string Url, string? ReturnUrl = null);

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>PKCE S256 challenge: base64url(SHA-256(verifier)). Must match the cloud's ClaimCodeStore.</summary>
    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}

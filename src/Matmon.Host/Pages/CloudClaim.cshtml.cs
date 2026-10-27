using Matmon.Host.Services;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>
/// Callback for the OAuth-style cloud CLAIM flow (started by <c>Config.OnPostCloudClaim</c>). The cloud sends
/// the browser back here with a one-time code; we validate the state against the data-protected cookie, redeem
/// the code (with the PKCE verifier) for the instance id + token, and store the cloud link. Admin-only.
/// (The first-run setup "with Matmon Cloud" has a callback of its own, <c>/setup/cloud</c>.)
/// </summary>
public class CloudClaimModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly IDataProtectionProvider _dataProtection;

    public CloudClaimModel(IMonitoringWorkspaceStore workspaceStore, IDataProtectionProvider dataProtection)
    {
        _workspaceStore = workspaceStore;
        _dataProtection = dataProtection;
    }

    public async Task<IActionResult> OnGetAsync(string? code, string? state, string? error, CancellationToken cancellationToken)
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        // Always clears the one-shot cookie, whatever the outcome.
        var pending = CloudClaimFlow.TakePending(HttpContext, _dataProtection, state);

        // The cloud consent page sends ?error=access_denied when the user hits Cancel there - report it cleanly.
        if (!string.IsNullOrWhiteSpace(error))
        {
            return Fail("Connection cancelled in the cloud. Nothing was changed.", pending?.ReturnUrl);
        }

        if (string.IsNullOrWhiteSpace(code) || pending is null)
        {
            return Fail("The cloud connection was cancelled, expired or could not be verified. Please try again.");
        }

        var (result, problem) = await CloudClaimFlow.ExchangeAsync(pending, code, cancellationToken);
        if (result is null)
        {
            return Fail(problem!, pending.ReturnUrl);
        }

        _workspaceStore.SetCloudConnectionSettings(pending.Url, result.InstanceId, result.Token, enabled: true);
        if (result.RemoteAccess)
        {
            _workspaceStore.SetCloudFullAccess(true);
        }
        TempData["StatusMessage"] = result.RemoteAccess
            ? "Connected to Matmon.Cloud for your customer - remote access is on, so you can open this instance from the cloud."
            : "Connected to Matmon.Cloud - the first heartbeat is sent within a few seconds.";
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
}

using Matmon.Core.Domain;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>
/// The first run: who administers this instance. Either a Matmon Cloud account - the instance is connected to the cloud
/// (the usual claim with PKCE) and the account that connected it becomes its first administrator, signing in through the
/// cloud with no local password - or a local account (e-mail + password). The way back in that never depends on the
/// cloud is the recovery file in the data folder (<see cref="RecoveryAdmin"/>), whichever was chosen.
/// </summary>
[AllowAnonymous]
public class SetupModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly MatmonRuntimeOptions _runtimeOptions;

    public SetupModel(IMonitoringWorkspaceStore workspaceStore, IDataProtectionProvider dataProtection, MatmonRuntimeOptions runtimeOptions)
    {
        _workspaceStore = workspaceStore;
        _dataProtection = dataProtection;
        _runtimeOptions = runtimeOptions;
    }

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public string ConfirmPassword { get; set; } = string.Empty;

    [BindProperty]
    public string? CloudUrl { get; set; }

    [BindProperty]
    public string? InstanceName { get; set; }

    public string? ErrorMessage { get; private set; }

    /// <summary>Which way is open: "cloud" (default) or "local" - a refused local form comes back on the local one.</summary>
    public string Mode { get; private set; } = "cloud";

    /// <summary>The cloud link was made but the cloud did not say who connected (an older cloud): only the local account is left.</summary>
    public bool CloudAlreadyLinked { get; private set; }

    public IActionResult OnGet()
    {
        // Setup is a one-time, first-run page. Once an account exists, send people to login.
        if (!_workspaceStore.IsSetupRequired())
        {
            return RedirectToPage("/Login");
        }

        FillCloudDefaults();
        return Page();
    }

    /// <summary>"Sign in with Matmon Cloud": off to the cloud's consent page; it comes back to <see cref="OnGetCloudAsync"/>.</summary>
    public IActionResult OnPostCloud()
    {
        if (!_workspaceStore.IsSetupRequired())
        {
            return RedirectToPage("/Login");
        }

        var url = (CloudUrl ?? string.Empty).Trim().TrimEnd('/');
        if (CloudClaimFlow.CheckCloudUrl(url, out _) is { } problem)
        {
            ErrorMessage = problem;
            FillCloudDefaults();
            return Page();
        }

        var name = string.IsNullOrWhiteSpace(InstanceName) ? SuggestedName() : InstanceName.Trim();
        return Redirect(CloudClaimFlow.Start(HttpContext, _dataProtection, url, name, "/setup/cloud", forSetup: true));
    }

    /// <summary>Back from the cloud: link the instance, make the account that connected it the first administrator and sign it in.</summary>
    public async Task<IActionResult> OnGetCloudAsync(string? code, string? state, string? error, CancellationToken cancellationToken)
    {
        if (!_workspaceStore.IsSetupRequired())
        {
            return RedirectToPage("/Login");
        }

        var pending = CloudClaimFlow.TakePending(HttpContext, _dataProtection, state);
        FillCloudDefaults();
        if (!string.IsNullOrWhiteSpace(error))
        {
            ErrorMessage = "The connection was cancelled in Matmon Cloud. Nothing was changed - connect again, or create a local account.";
            return Page();
        }
        if (string.IsNullOrWhiteSpace(code) || pending is not { ForSetup: true })
        {
            ErrorMessage = "The connection to Matmon Cloud expired or could not be verified. Please try again.";
            return Page();
        }

        var (result, problem) = await CloudClaimFlow.ExchangeAsync(pending, code, cancellationToken);
        if (result is null)
        {
            ErrorMessage = problem;
            return Page();
        }

        _workspaceStore.SetCloudConnectionSettings(pending.Url, result.InstanceId, result.Token, enabled: true);
        if (result.RemoteAccess)
        {
            _workspaceStore.SetCloudFullAccess(true);
        }

        if (string.IsNullOrWhiteSpace(result.AccountEmail))
        {
            // An older cloud does not say who connected: the link is made, but somebody still has to be able to sign in.
            Mode = "local";
            CloudAlreadyLinked = true;
            ErrorMessage = "Connected to Matmon Cloud, but this cloud does not tell which account connected. Create a local administrator to finish - you can sign in with the cloud later.";
            return Page();
        }

        MatmonUser admin;
        try
        {
            admin = _workspaceStore.CompleteInitialSetupWithCloud(result.AccountEmail);
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }

        await InstanceSignIn.SignInAsync(HttpContext, admin);
        TempData["StatusMessage"] = $"Connected to Matmon Cloud - signed in as {admin.Email}.";
        return RedirectToPage("/Wizard");
    }

    /// <summary>The local account (e-mail + password).</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        if (!_workspaceStore.IsSetupRequired())
        {
            return RedirectToPage("/Login");
        }

        Mode = "local";
        FillCloudDefaults();
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

        await InstanceSignIn.SignInAsync(HttpContext, admin);

        // Hand off to the optional setup wizard (networks / discovery / probes / notifications).
        return RedirectToPage("/Wizard");
    }

    private void FillCloudDefaults()
    {
        CloudUrl = string.IsNullOrWhiteSpace(CloudUrl)
            ? (string.IsNullOrWhiteSpace(_runtimeOptions.CloudUrl) ? ConfigModel.DefaultCloudUrl : _runtimeOptions.CloudUrl.Trim().TrimEnd('/'))
            : CloudUrl;
        InstanceName = string.IsNullOrWhiteSpace(InstanceName) ? SuggestedName() : InstanceName;
    }

    private string SuggestedName() =>
        Matmon.Host.Ui.CloudInstanceNameSuggestion.Suggest(
            Request.Host.Host,
            _workspaceStore.GetAllElements().OfType<ProbeElement>().FirstOrDefault(probe => probe.ParentId is null)?.Name,
            Environment.MachineName);
}

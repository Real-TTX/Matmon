using System.Security.Claims;
using Matmon.Host.Services;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>
/// Self-service account page for the signed-in user: set/change a local password. This is the offline
/// fallback for SSO ("Sign in with Matmon Cloud") accounts - once they set a local password they can log
/// in with e-mail + password even when the cloud is unreachable.
/// </summary>
public class AccountModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly LoginCodeStore _codes;
    private readonly INotificationEmailSender _emailSender;

    public AccountModel(IMonitoringWorkspaceStore workspaceStore, LoginCodeStore codes, INotificationEmailSender emailSender)
    {
        _workspaceStore = workspaceStore;
        _codes = codes;
        _emailSender = emailSender;
    }

    private const string TotpIssuer = "Matmon";

    [BindProperty] public string? CurrentPassword { get; set; }
    [BindProperty] public string NewPassword { get; set; } = string.Empty;
    [BindProperty] public string ConfirmPassword { get; set; } = string.Empty;

    [BindProperty] public string? TimeZoneId { get; set; }
    public IReadOnlyList<SelectListItem> TimeZoneItems { get; private set; } = [];

    // --- Two-factor (TOTP) ---
    [BindProperty] public string? TotpCode { get; set; }
    public bool TwoFactorEnabled { get; private set; }
    public DateTimeOffset? TwoFactorSince { get; private set; }
    public TotpEnrollmentInfo? Enrollment { get; private set; }
    public string? QrSvg => Enrollment is null ? null : TotpQr.Svg(Enrollment.OtpauthUri);
    public bool FocusSecurity { get; private set; }
    /// <summary>The Security tab should open when there's an error, an active enrollment, or 2FA feedback.</summary>
    public bool ShowSecurityTab => ErrorMessage is not null || Enrollment is not null || FocusSecurity;

    public bool HasPassword { get; private set; }
    public string AccountName => User.Identity?.Name ?? "Account";
    public string Role => User.FindFirstValue(ClaimTypes.Role) ?? "Viewer";

    public string? ErrorMessage { get; private set; }
    [TempData] public string? StatusMessage { get; set; }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>This user's browsers with notifications on (the Notifications tab).</summary>
    public IReadOnlyList<PushDevice> Devices { get; private set; } = [];

    public void OnGet() => LoadAll();

    /// <summary>Forgets one of the user's devices (a lost phone, an old laptop) - that browser gets nothing more.</summary>
    public IActionResult OnPostRemoveDevice(Guid id)
    {
        StatusMessage = _workspaceStore.RemovePushDevice(id, UserId) ? "Device removed - it gets no more notifications." : null;
        return RedirectToPage(null, null, null, "notifications");
    }

    /// <summary>One test notification to one of the user's devices - the way to know it arrives before an alert depends on it.</summary>
    public async Task<IActionResult> OnPostTestDeviceAsync(Guid id, [FromServices] Matmon.Host.Services.WebPush.BrowserPushNotifier notifier, CancellationToken cancellationToken)
    {
        var device = _workspaceStore.GetPushDevices(UserId).FirstOrDefault(candidate => candidate.Id == id);
        if (device is not null)
        {
            var result = await notifier.SendAsync([device], new Matmon.Host.Services.WebPush.BrowserNotification(
                "Matmon: test notification", "Notifications reach this device.", "/account#notifications", "matmon-test"), cancellationToken);
            StatusMessage = result.Delivered > 0 ? $"Test sent to {device.Name} - it shows up in a moment."
                : result.Gone > 0 ? $"{device.Name} had switched notifications off - it was removed."
                : "The push service did not take it - try again in a while.";
        }

        return RedirectToPage(null, null, null, "notifications");
    }

    /// <summary>The cloud link is there (stored, switched on) - the cloud sign-in works.</summary>
    public bool CloudLinked { get; private set; }

    /// <summary>The account has signed in with Matmon Cloud before (it is cloud-linked).</summary>
    public bool UserCloudLinked { get; private set; }

    /// <summary>The local password may be switched off: the account signs in through the cloud while the link is there.</summary>
    public bool CanRemovePassword => HasPassword && UserCloudLinked && CloudLinked;

    /// <summary>Switch the local password off - only the cloud sign-in remains (the recovery file still gets someone in).</summary>
    public IActionResult OnPostRemovePassword(string? removeCurrentPassword)
    {
        LoadAll();
        FocusSecurity = true;
        switch (_workspaceStore.RemoveOwnPassword(UserId, removeCurrentPassword ?? string.Empty))
        {
            case RemovePasswordResult.Success:
                StatusMessage = "Local password switched off - you sign in with Matmon Cloud now.";
                return RedirectToPage(null, null, null, "security");
            case RemovePasswordResult.WrongCurrent:
                ErrorMessage = "The current password is incorrect.";
                return Page();
            case RemovePasswordResult.NoCloudSignIn:
                ErrorMessage = "Sign in with Matmon Cloud once first (and keep the cloud link) - without it nobody could sign in with this account any more.";
                return Page();
            default:
                ErrorMessage = "This account has no local password.";
                return Page();
        }
    }

    private void LoadAll()
    {
        HasPassword = _workspaceStore.HasLocalPassword(UserId);
        var cloud = _workspaceStore.GetCloudConnectionSettings();
        CloudLinked = cloud.Enabled && !string.IsNullOrWhiteSpace(cloud.Url) && !string.IsNullOrWhiteSpace(cloud.InstanceId);
        LoadTimeZone();
        var user = _workspaceStore.FindUser(UserId);
        UserCloudLinked = user?.CloudLinked ?? false;
        TwoFactorEnabled = user?.TwoFactorEnabled ?? false;
        TwoFactorSince = user?.TotpEnrolledUtc;
        Devices = _workspaceStore.GetPushDevices(UserId);
    }

    private void LoadTimeZone()
    {
        TimeZoneId = _workspaceStore.FindUser(UserId)?.TimeZoneId;
        TimeZoneItems = TimeZoneOptions.Build(TimeZoneId, "System default");
    }

    /// <summary>Begin TOTP enrollment: generate + store a secret and show its QR + manual key (2FA stays OFF until confirmed).</summary>
    public IActionResult OnPostStartTotp()
    {
        LoadAll();
        if (TwoFactorEnabled) { return RedirectToPage(); }
        Enrollment = _workspaceStore.BeginTotpEnrollment(UserId, TotpIssuer);
        return Page();
    }

    /// <summary>Confirm enrollment with a code from the authenticator; turns 2FA on.</summary>
    public IActionResult OnPostConfirmTotp()
    {
        LoadAll();
        if (_workspaceStore.ConfirmTotp(UserId, TotpCode ?? string.Empty))
        {
            StatusMessage = "Two-factor authentication is now enabled.";
            return RedirectToPage();
        }
        ErrorMessage = "That code was not valid. Enter a fresh code from your authenticator.";
        Enrollment = _workspaceStore.GetPendingTotpEnrollment(UserId, TotpIssuer);
        return Page();
    }

    /// <summary>Disable 2FA - authorized by EITHER a current authenticator code OR an e-mailed one-time code.</summary>
    public IActionResult OnPostDisableTotp()
    {
        LoadAll();
        var code = (TotpCode ?? string.Empty).Trim();
        var authorized = _workspaceStore.VerifyTotp(UserId, code) || _codes.Verify(UserId, code, DateTimeOffset.UtcNow);
        if (authorized && _workspaceStore.DisableTotp(UserId))
        {
            StatusMessage = "Two-factor authentication disabled.";
            return RedirectToPage();
        }
        FocusSecurity = true;
        ErrorMessage = "Enter a valid authenticator code, or a code sent to your e-mail, to disable two-factor.";
        return Page();
    }

    /// <summary>Fallback: e-mail a one-time code the user can use to disable 2FA (lost-authenticator path).</summary>
    public async Task<IActionResult> OnPostSendDisableCodeAsync(CancellationToken cancellationToken)
    {
        LoadAll();
        FocusSecurity = true;
        if (!TwoFactorEnabled) { return Page(); }
        var email = _workspaceStore.GetUserEmail(UserId);
        if (string.IsNullOrWhiteSpace(email))
        {
            ErrorMessage = "No e-mail is on file for your account - use your authenticator to disable.";
            return Page();
        }
        var settings = _workspaceStore.Workspace.NotificationConfiguration.Email;
        if (string.IsNullOrWhiteSpace(settings.SmtpHost))
        {
            ErrorMessage = "E-mail isn't configured on this instance - use your authenticator to disable.";
            return Page();
        }
        var code = _codes.Issue(UserId, DateTimeOffset.UtcNow);
        if (code is not null)
        {
            var subject = "Your Matmon verification code";
            var text = $"Your verification code is {code}. It expires in 10 minutes.";
            var html = "<p style=\"margin:0 0 6px;\">Your verification code is:</p>" +
                       $"<p style=\"font-size:26px;font-weight:700;letter-spacing:4px;margin:0 0 10px;\">{code}</p>" +
                       "<p style=\"margin:0;color:#6b7280;\">Use it to disable two-factor. It expires in 10 minutes.</p>";
            try { await _emailSender.SendAsync(settings, email, subject, text, html, cancellationToken); }
            catch { /* transient SMTP issue - the code is still valid once delivered */ }
            StatusMessage = "We e-mailed you a verification code.";
        }
        else
        {
            StatusMessage = "A code was sent recently - check your inbox.";
        }
        return Page();
    }

    /// <summary>Save the signed-in user's display-timezone override (empty = use the system default).</summary>
    public IActionResult OnPostTimeZone()
    {
        _workspaceStore.SetUserTimeZone(UserId, TimeZoneId);
        StatusMessage = "Display timezone updated.";
        return RedirectToPage();
    }

    public IActionResult OnPost()
    {
        LoadAll();

        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "The new passwords do not match.";
            return Page();
        }

        var result = _workspaceStore.ChangeOwnPassword(UserId, CurrentPassword, NewPassword);
        switch (result)
        {
            case ChangePasswordResult.Success:
                StatusMessage = HasPassword ? "Password changed." : "Local password set - you can now sign in offline.";
                return RedirectToPage();
            case ChangePasswordResult.WrongCurrent:
                ErrorMessage = "The current password is incorrect.";
                return Page();
            case ChangePasswordResult.TooShort:
                ErrorMessage = "The new password must be at least 8 characters.";
                return Page();
            default:
                ErrorMessage = "Could not change the password.";
                return Page();
        }
    }
}

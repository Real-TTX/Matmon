using Matmon.Host.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>Second login step (anonymous): reached only after a valid password when the user has 2FA on. Accepts a
/// TOTP code from the authenticator OR a one-time code e-mailed to the account (the fallback).</summary>
[AllowAnonymous]
public class LoginTwoFactorModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly Pending2faCookie _pending;
    private readonly LoginCodeStore _codes;
    private readonly INotificationEmailSender _emailSender;

    public LoginTwoFactorModel(
        IMonitoringWorkspaceStore workspaceStore, Pending2faCookie pending, LoginCodeStore codes, INotificationEmailSender emailSender)
    {
        _workspaceStore = workspaceStore;
        _pending = pending;
        _codes = codes;
        _emailSender = emailSender;
    }

    [BindProperty] public string? Code { get; set; }
    public string? ErrorMessage { get; private set; }
    public string? InfoMessage { get; private set; }

    public IActionResult OnGet() =>
        _pending.Read(HttpContext) is null ? RedirectToPage("/Login") : Page();

    public async Task<IActionResult> OnPostVerifyAsync()
    {
        if (_pending.Read(HttpContext) is not { } pending)
        {
            return RedirectToPage("/Login");
        }

        var code = (Code ?? string.Empty).Trim();
        var ok = _workspaceStore.VerifyTotp(pending.UserId, code)
                 || _codes.Verify(pending.UserId, code, DateTimeOffset.UtcNow);
        if (!ok)
        {
            ErrorMessage = "That code is invalid or expired. Try again, or send a code to your e-mail.";
            return Page();
        }

        var user = _workspaceStore.FindUser(pending.UserId);
        if (user is null)
        {
            _pending.Clear(HttpContext);
            return RedirectToPage("/Login");
        }

        await InstanceSignIn.SignInAsync(HttpContext, user);
        _pending.Clear(HttpContext);
        return !string.IsNullOrWhiteSpace(pending.ReturnUrl) && Url.IsLocalUrl(pending.ReturnUrl)
            ? LocalRedirect(pending.ReturnUrl)
            : RedirectToPage("/Index");
    }

    public async Task<IActionResult> OnPostSendEmailAsync(CancellationToken cancellationToken)
    {
        if (_pending.Read(HttpContext) is not { } pending)
        {
            return RedirectToPage("/Login");
        }

        var email = _workspaceStore.GetUserEmail(pending.UserId);
        if (string.IsNullOrWhiteSpace(email))
        {
            InfoMessage = "No e-mail is on file for this account - please use your authenticator app.";
            return Page();
        }

        var settings = _workspaceStore.Workspace.NotificationConfiguration.Email;
        if (string.IsNullOrWhiteSpace(settings.SmtpHost))
        {
            InfoMessage = "E-mail isn't configured on this instance - please use your authenticator app.";
            return Page();
        }

        var code = _codes.Issue(pending.UserId, DateTimeOffset.UtcNow);
        if (code is not null)
        {
            var subject = "Your Matmon login code";
            var text = $"Your login code is {code}. It expires in 10 minutes. If you didn't try to sign in, ignore this e-mail.";
            var html = "<p style=\"margin:0 0 6px;\">Your login code is:</p>" +
                       $"<p style=\"font-size:26px;font-weight:700;letter-spacing:4px;margin:0 0 10px;\">{code}</p>" +
                       "<p style=\"margin:0;color:#6b7280;\">It expires in 10 minutes.</p>";
            try { await _emailSender.SendAsync(settings, email, subject, text, html, cancellationToken); }
            catch { /* transient SMTP issue - TOTP remains available; don't leak details */ }
            InfoMessage = "We e-mailed you a login code (check spam too). Enter it above.";
        }
        else
        {
            InfoMessage = "A code was sent recently - please use that one (check your inbox).";
        }
        return Page();
    }
}

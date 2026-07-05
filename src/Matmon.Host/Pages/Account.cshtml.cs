using System.Security.Claims;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>
/// Self-service account page for the signed-in user: set/change a local password. This is the offline
/// fallback for SSO ("Sign in with Matmon Cloud") accounts — once they set a local password they can log
/// in with e-mail + password even when the cloud is unreachable.
/// </summary>
public class AccountModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;

    public AccountModel(IMonitoringWorkspaceStore workspaceStore)
    {
        _workspaceStore = workspaceStore;
    }

    [BindProperty] public string? CurrentPassword { get; set; }
    [BindProperty] public string NewPassword { get; set; } = string.Empty;
    [BindProperty] public string ConfirmPassword { get; set; } = string.Empty;

    public bool HasPassword { get; private set; }
    public string AccountName => User.Identity?.Name ?? "Account";
    public string Role => User.FindFirstValue(ClaimTypes.Role) ?? "Viewer";

    public string? ErrorMessage { get; private set; }
    [TempData] public string? StatusMessage { get; set; }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public void OnGet()
    {
        HasPassword = _workspaceStore.HasLocalPassword(UserId);
    }

    public IActionResult OnPost()
    {
        HasPassword = _workspaceStore.HasLocalPassword(UserId);

        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "The new passwords do not match.";
            return Page();
        }

        var result = _workspaceStore.ChangeOwnPassword(UserId, CurrentPassword, NewPassword);
        switch (result)
        {
            case ChangePasswordResult.Success:
                StatusMessage = HasPassword ? "Password changed." : "Local password set — you can now sign in offline.";
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

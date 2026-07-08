using Matmon.Core.Domain;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

public sealed class UserCreateModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;

    public UserCreateModel(IMonitoringWorkspaceStore workspaceStore)
    {
        _workspaceStore = workspaceStore;
    }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public UserCreateInput UserInput { get; set; } = new();

    public IReadOnlyList<SelectListItem> RoleOptions { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        LoadView();
        return Page();
    }

    public IActionResult OnPost()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        // Local accounts: the password + its confirmation must match (Cloud/SSO accounts have no local password).
        if (UserInput.AccountType == UserAccountType.Local &&
            (UserInput.Password ?? string.Empty) != (UserInput.PasswordConfirm ?? string.Empty))
        {
            ErrorMessage = "The passwords do not match.";
            LoadView();
            return Page();
        }

        try
        {
            var user = UserInput.AccountType == UserAccountType.Cloud
                ? _workspaceStore.UpsertCloudUser(UserInput.Email ?? string.Empty, UserInput.Role)
                : _workspaceStore.CreateUser(UserInput.Username, UserInput.Password ?? string.Empty, UserInput.Role);
            StatusMessage = UserInput.AccountType == UserAccountType.Cloud
                ? $"Cloud user '{user.Username}' added. They sign in with Matmon Cloud (no local password)."
                : $"User '{user.Username}' created.";
            return Redirect(GetSafeReturnUrl("/Config?tab=users"));
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadView();
            return Page();
        }
    }

    public string GetSafeReturnUrl(string fallbackPage)
    {
        if (!string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return ReturnUrl;
        }

        return fallbackPage;
    }

    private void LoadView()
    {
        RoleOptions = Enum.GetValues<MatmonUserRole>()
            .OrderByDescending(role => role)
            .Select(role => new SelectListItem(role.ToString(), role.ToString()))
            .ToArray();
    }
}

public sealed class UserCreateInput
{
    /// <summary>Local = username + local password; Cloud = link a Matmon Cloud account (e-mail, SSO, no local password).</summary>
    public UserAccountType AccountType { get; set; } = UserAccountType.Local;

    public string Username { get; set; } = string.Empty;

    public string? Password { get; set; }

    public string? PasswordConfirm { get; set; }

    public string Email { get; set; } = string.Empty;

    public MatmonUserRole Role { get; set; } = MatmonUserRole.Viewer;
}

public enum UserAccountType
{
    Local,
    Cloud
}

using Matmon.Core.Domain;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

public sealed class UserEditModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;

    public UserEditModel(IMonitoringWorkspaceStore workspaceStore)
    {
        _workspaceStore = workspaceStore;
    }

    [BindProperty(SupportsGet = true)]
    public Guid? UserId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public UserEditInput UserInput { get; set; } = new();

    public IReadOnlyList<SelectListItem> RoleOptions { get; private set; } = [];

    public MatmonUser? UserRecord { get; private set; }

    public bool CanEdit => UserRecord is not null && !MatmonSecurity.IsCurrentUser(User, UserRecord.Id);

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        LoadView(populateInput: true);
        return Page();
    }

    public IActionResult OnPost()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        LoadView(populateInput: false);
        if (UserRecord is null)
        {
            ErrorMessage = "User not found.";
            return Page();
        }

        if (!CanEdit)
        {
            ErrorMessage = "Your own account cannot be edited here.";
            return Page();
        }

        try
        {
            _workspaceStore.UpdateUser(UserRecord.Id, UserInput.Username, UserInput.Role, UserInput.IsEnabled, UserInput.Password);
            StatusMessage = $"User '{UserInput.Username}' updated.";
            return Redirect(GetSafeReturnUrl("/Config?tab=users"));
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadView(populateInput: false);
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

    private void LoadView(bool populateInput)
    {
        RoleOptions = Enum.GetValues<MatmonUserRole>()
            .OrderByDescending(role => role)
            .Select(role => new SelectListItem(role.ToString(), role.ToString()))
            .ToArray();

        if (UserId is null)
        {
            UserRecord = null;
            return;
        }

        UserRecord = _workspaceStore.FindUser(UserId.Value);
        if (UserRecord is null)
        {
            return;
        }

        if (populateInput)
        {
            UserInput.Username = UserRecord.Username;
            UserInput.Role = UserRecord.Role;
            UserInput.IsEnabled = UserRecord.IsEnabled;
            UserInput.Password = string.Empty;
        }
    }
}

public sealed class UserEditInput
{
    public string Username { get; set; } = string.Empty;

    public string? Password { get; set; }

    public MatmonUserRole Role { get; set; } = MatmonUserRole.Viewer;

    public bool IsEnabled { get; set; } = true;
}

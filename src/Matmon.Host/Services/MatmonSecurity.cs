using System.Security.Claims;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public static class MatmonSecurity
{
    public const string AdminPolicy = "Matmon.Admin";
    public const string AlertOperatorPolicy = "Matmon.AlertOperator";

    public static bool IsAdmin(ClaimsPrincipal user)
    {
        return user.IsInRole(MatmonUserRole.Admin.ToString());
    }

    public static bool CanOperateAlerts(ClaimsPrincipal user)
    {
        return user.IsInRole(MatmonUserRole.Admin.ToString()) ||
            user.IsInRole(MatmonUserRole.User.ToString());
    }

    public static bool TryGetCurrentUserId(ClaimsPrincipal user, out Guid userId)
    {
        userId = Guid.Empty;
        var claim = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(claim, out userId);
    }

    public static bool IsCurrentUser(ClaimsPrincipal user, Guid userId)
    {
        return TryGetCurrentUserId(user, out var currentUserId) && currentUserId == userId;
    }
}

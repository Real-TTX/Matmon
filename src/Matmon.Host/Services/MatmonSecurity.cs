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
}


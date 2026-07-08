using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Matmon.Host.Services;

public sealed class MatmonPageWriteGuard : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context)
    {
        return Task.CompletedTask;
    }

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (!HttpMethods.IsPost(context.HttpContext.Request.Method) ||
            CanPost(context))
        {
            await next();
            return;
        }

        context.Result = new ForbidResult();
    }

    private static bool CanPost(PageHandlerExecutingContext context)
    {
        var page = context.ActionDescriptor.ViewEnginePath ?? string.Empty;
        if (string.Equals(page, "/Login", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // First-run setup creates the very first admin while still anonymous - must be postable.
        if (string.Equals(page, "/Setup", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(page, "/Logout", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(page, "/Alerts", StringComparison.OrdinalIgnoreCase))
        {
            return MatmonSecurity.CanOperateAlerts(context.HttpContext.User);
        }

        // Self-service account page (e.g. set/change your own local password) - any signed-in user.
        if (string.Equals(page, "/Account", StringComparison.OrdinalIgnoreCase))
        {
            return context.HttpContext.User.Identity?.IsAuthenticated == true;
        }

        return MatmonSecurity.IsAdmin(context.HttpContext.User);
    }
}

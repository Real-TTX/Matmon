using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace Matmon.Host.Services;

public sealed class MatmonClaimsTransformation : IClaimsTransformation
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;

    public MatmonClaimsTransformation(IMonitoringWorkspaceStore workspaceStore)
    {
        _workspaceStore = workspaceStore;
    }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return Task.FromResult(principal);
        }

        var username = principal.Identity.Name;
        if (string.IsNullOrWhiteSpace(username))
        {
            return Task.FromResult(principal);
        }

        var user = _workspaceStore.GetUsers()
            .FirstOrDefault(candidate => string.Equals(candidate.Username, username, StringComparison.OrdinalIgnoreCase));
        var sourceIdentity = principal.Identities.FirstOrDefault(identity => identity.IsAuthenticated);
        if (sourceIdentity is null)
        {
            return Task.FromResult(principal);
        }

        var claims = sourceIdentity.Claims
            .Where(claim => claim.Type is not (ClaimTypes.Role or ClaimTypes.NameIdentifier))
            .ToList();

        if (user is { IsEnabled: true })
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
            claims.Add(new Claim(ClaimTypes.Role, user.Role.ToString()));
        }

        var identity = new ClaimsIdentity(
            claims,
            sourceIdentity.AuthenticationType,
            sourceIdentity.NameClaimType,
            sourceIdentity.RoleClaimType);

        return Task.FromResult(new ClaimsPrincipal(identity));
    }
}


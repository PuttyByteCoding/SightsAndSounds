using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace VideoOrganizer.API.Auth;

/// <summary>
/// Flattens Keycloak's <c>realm_access.roles</c> JSON claim into standard role
/// claims so <c>User.IsInRole("admin")</c> and role policies work (#124).
/// </summary>
public sealed class KeycloakRolesClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
            return Task.FromResult(principal);

        var realmAccess = principal.FindFirst("realm_access")?.Value;
        foreach (var role in KeycloakRoles.Extract(realmAccess))
        {
            if (!identity.HasClaim(ClaimTypes.Role, role))
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }
        return Task.FromResult(principal);
    }
}

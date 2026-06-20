using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using VideoOrganizer.API.Auth;
using VideoOrganizer.Shared.Dto;

namespace VideoOrganizer.API;

public static partial class ApiEndpoints
{
    // Public auth config for the SPA (#124, Phase 3). Reachable without a token
    // (see AuthRules.PublicApiPaths) so the browser can learn whether auth is on
    // and how to reach Keycloak before it logs in.
    private static void MapAuthEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/auth/config", (IConfiguration config) =>
        {
            var enabled = config.GetValue("Auth:Enabled", false);
            return Results.Ok(new AuthConfigDto(
                Enabled: enabled,
                Authority: config["Auth:Authority"] ?? string.Empty,
                ClientId: config["Auth:SpaClientId"] ?? "sightsandsounds-spa",
                Audience: config["Auth:Audience"] ?? "sightsandsounds-api"));
        }).Produces<AuthConfigDto>(StatusCodes.Status200OK)
          .WithName("GetAuthConfig");
    }
}

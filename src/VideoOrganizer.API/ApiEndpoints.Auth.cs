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

        // Media cookie session (#124). Browser media elements (<video>/<img>/
        // <track>/CSS url()) can't send a Bearer header, so the SPA POSTs its
        // already-validated token here and we mirror it into an HttpOnly cookie
        // that those same-origin requests carry automatically. Reaching this
        // endpoint already required a valid token (the auth middleware), and it
        // only ever stores the caller's OWN token — viewers included (it's in
        // AuthRules.ReadPostPaths so it doesn't need the admin role).
        api.MapPost("/auth/session", (HttpContext http, IConfiguration config) =>
        {
            var authz = http.Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            if (!authz.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Missing bearer token." });
            var token = authz[prefix.Length..].Trim();
            if (token.Length == 0)
                return Results.BadRequest(new { error = "Empty bearer token." });

            // Cookie lifetime tracks the token: use its 'exp' claim when present,
            // else 5 min (the realm's default access-token lifespan). The SPA
            // re-POSTs on each silent renew, so the cookie stays fresh.
            var maxAge = TimeSpan.FromMinutes(5);
            if (long.TryParse(http.User.FindFirst("exp")?.Value, out var expUnix))
            {
                var remaining = DateTimeOffset.FromUnixTimeSeconds(expUnix) - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero) maxAge = remaining;
            }

            http.Response.Cookies.Append(AuthSetup.MediaCookieName, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = config.GetValue("Auth:RequireHttpsMetadata", false),
                Path = "/api",
                MaxAge = maxAge,
            });
            return Results.NoContent();
        }).WithName("SetAuthSession");

        api.MapDelete("/auth/session", (HttpContext http) =>
        {
            http.Response.Cookies.Delete(AuthSetup.MediaCookieName, new CookieOptions { Path = "/api" });
            return Results.NoContent();
        }).WithName("ClearAuthSession");
    }
}

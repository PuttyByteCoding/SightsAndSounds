using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace VideoOrganizer.API.Auth;

/// <summary>
/// Wires Keycloak JWT auth into the API (#124, Phase 2). Everything is gated on
/// <c>Auth:Enabled</c> (default false) so this can ship without changing the
/// app's current no-auth behavior — flip it on once Keycloak is running and the
/// SPA login (Phase 3) is in place.
///
/// When enabled:
///   · validate Bearer JWTs against the realm (signature/issuer/audience/expiry);
///   · flatten realm_access.roles into role claims;
///   · require an authenticated user for every /api request, and the <c>admin</c>
///     role for writes (see <see cref="AuthRules"/>). Read-only users (the
///     <c>viewer</c> role) can read everything but change nothing.
/// </summary>
public static class AuthSetup
{
    public static bool IsEnabled(IConfiguration config) =>
        config.GetValue("Auth:Enabled", false);

    public static void AddSightsAuth(IServiceCollection services, IConfiguration config)
    {
        if (!IsEnabled(config)) return;

        var authority = config["Auth:Authority"]
            ?? "http://localhost:8080/realms/sightsandsounds";
        var audience = config["Auth:Audience"] ?? "sightsandsounds-api";
        var requireHttps = config.GetValue("Auth:RequireHttpsMetadata", false);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.Audience = audience;
                options.RequireHttpsMetadata = requireHttps;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    RoleClaimType = ClaimTypes.Role,
                    NameClaimType = "preferred_username",
                };
            });
        services.AddAuthorization();
        services.AddTransient<IClaimsTransformation, KeycloakRolesClaimsTransformation>();
    }

    public static void UseSightsAuth(WebApplication app)
    {
        if (!IsEnabled(app.Configuration)) return;

        app.UseAuthentication();

        // Single gate for /api: authenticated for reads, admin for writes.
        // Runs after authentication so ctx.User (and its flattened roles) is set.
        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments("/api"))
            {
                await next();
                return;
            }

            var user = ctx.User;
            if (user?.Identity?.IsAuthenticated != true)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (AuthRules.IsWriteRequest(ctx.Request.Method, ctx.Request.Path.Value)
                && !user.IsInRole(AuthRules.AdminRole))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = "Read-only account: this action requires the admin role."
                });
                return;
            }

            await next();
        });
    }
}

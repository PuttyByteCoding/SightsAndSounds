using System.Net;

namespace VideoOrganizer.API.Access;

/// <summary>
/// IP allowlist middleware (#124 — "limit access by device IP"). Gated on
/// <c>Network:RestrictByIp</c> (default false). When on, every request whose
/// client IP isn't in <c>Network:AllowedCidrs</c> gets a 403 before anything
/// else runs — so a disallowed device can't even load the SPA. Loopback is
/// always allowed so the host itself is never locked out.
///
/// Runs FIRST in the pipeline. Uses the direct connection IP; behind a reverse
/// proxy (Option 3) add ForwardedHeaders so the real client IP is seen.
/// </summary>
public static class IpAccessSetup
{
    private static readonly string[] DefaultAllow = ["127.0.0.1/32", "::1/128"];

    public static void UseIpAllowlist(WebApplication app)
    {
        if (!app.Configuration.GetValue("Network:RestrictByIp", false)) return;

        var entries = app.Configuration.GetSection("Network:AllowedCidrs").Get<string[]>();
        // Always include loopback so the host can't lock itself out.
        var networks = IpAllowList.Parse((entries ?? []).Concat(DefaultAllow));

        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("VideoOrganizer.Access.IpAllowList");
        logger.LogInformation(
            "IP allowlist ENABLED — {Count} allowed network(s); all other client IPs get 403.",
            networks.Count);

        app.Use(async (ctx, next) =>
        {
            var ip = ctx.Connection.RemoteIpAddress;
            if (!IpAllowList.IsAllowed(ip, networks))
            {
                logger.LogWarning("Blocked {Ip} -> {Method} {Path}",
                    ip, ctx.Request.Method, ctx.Request.Path);
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next();
        });
    }
}

namespace VideoOrganizer.API.Auth;

/// <summary>
/// The read-vs-write classification behind the viewer/admin split (#124,
/// Phase 2). A read-only ("viewer") user may call any read; only "admin" may
/// call a write.
///
/// Writes are detected by HTTP method (anything that isn't GET/HEAD/OPTIONS),
/// with a small allowlist of POSTs that are actually QUERIES (they take a
/// filter body but mutate nothing). This fails CLOSED: a write that isn't on
/// the allowlist requires admin, so a missed read-POST only ever over-restricts
/// (a viewer gets 403 on a read) — it never lets a viewer through to a write.
/// </summary>
public static class AuthRules
{
    /// <summary>Realm role granting write access.</summary>
    public const string AdminRole = "admin";

    /// <summary>
    /// /api paths reachable WITHOUT a token even when auth is on — the SPA needs
    /// the auth config before it can log in (#124, Phase 3).
    /// </summary>
    public static readonly IReadOnlySet<string> PublicApiPaths =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/api/auth/config" };

    /// <summary>True if the path is public (no authentication required).</summary>
    public static bool IsPublic(string? path) => PublicApiPaths.Contains(path ?? string.Empty);

    /// <summary>
    /// POST endpoints that are reads (query bodies, no persistence): the browse
    /// filter queries and the ad-hoc playlist generators (which only build an
    /// in-memory ordering). Full request paths.
    ///
    /// Also includes /api/auth/session — it doesn't touch library data, it only
    /// mirrors the caller's OWN already-validated token into an HttpOnly cookie
    /// so browser media elements (&lt;video&gt;/&lt;img&gt;, which can't send a
    /// Bearer header) can stream. Read-only viewers need it too, so it must not
    /// require the admin role. Listing it here exempts every verb on the path
    /// (POST set / DELETE clear) from the write-check.
    /// </summary>
    public static readonly IReadOnlySet<string> ReadPostPaths =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "/api/videos/filter",
            "/api/videos/filter-page",
            "/api/playlists/random",
            "/api/playlists/even",
            "/api/auth/session",
        };

    /// <summary>
    /// True if the request mutates state (and therefore needs the admin role).
    /// GET/HEAD/OPTIONS are reads; any other verb is a write unless its path is
    /// an allowlisted read-POST.
    /// </summary>
    public static bool IsWriteRequest(string method, string? path)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
            return false;
        return !ReadPostPaths.Contains(path ?? string.Empty);
    }
}

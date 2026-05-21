namespace VideoOrganizer.Shared.Helpers;

// Small SQL-shaped helpers. Lives in Shared (not API) so unit tests
// can reference just this project — without that decoupling, a test
// project pulling in API also pulls in Infrastructure / Npgsql /
// everything else, and any DLL locked by a running API process
// blocks the test build.
//
// Add new helpers here when they're (a) pure functions, (b) used by
// more than one endpoint, or (c) non-obvious enough to be worth
// covering with tests. Anything that requires a DbContext or
// HttpContext belongs in the endpoint or a service, not here.
public static class SqlHelpers
{
    // Escape the three Postgres LIKE/ILIKE metacharacters so a raw
    // user query like "50%_off" matches the literal string instead of
    // being interpreted as wildcards. Backslash also needs escaping
    // because we use it as the LIKE escape character (Npgsql's
    // default).
    //
    // Shared by /api/search and POST /api/videos/filter (when its
    // SearchQuery field is set) — both wrap the result in %…% for a
    // substring match against trigram-indexed columns.
    public static string EscapeLikePattern(string s) => s
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");
}

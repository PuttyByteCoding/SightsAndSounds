using System.Text.Json;

namespace VideoOrganizer.API.Auth;

/// <summary>
/// Keycloak puts realm roles in a JSON <c>realm_access</c> claim
/// (<c>{"roles":["admin","viewer"]}</c>), not the flat role claims .NET's
/// <c>IsInRole</c> expects. This extracts them so a claims transformation can
/// re-add them as role claims (#124, Phase 2).
/// </summary>
public static class KeycloakRoles
{
    /// <summary>
    /// Parses the realm roles out of a <c>realm_access</c> claim value. Returns
    /// empty on null/blank/malformed input (never throws).
    /// </summary>
    public static IReadOnlyList<string> Extract(string? realmAccessJson)
    {
        if (string.IsNullOrWhiteSpace(realmAccessJson)) return [];
        try
        {
            using var doc = JsonDocument.Parse(realmAccessJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("roles", out var roles) ||
                roles.ValueKind != JsonValueKind.Array)
                return [];

            var result = new List<string>();
            foreach (var r in roles.EnumerateArray())
            {
                if (r.ValueKind == JsonValueKind.String)
                {
                    var role = r.GetString();
                    if (!string.IsNullOrEmpty(role)) result.Add(role);
                }
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

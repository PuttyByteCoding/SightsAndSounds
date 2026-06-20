namespace VideoOrganizer.Shared.Dto;

// Public auth configuration the SPA fetches on startup to decide whether to
// require login and how to reach Keycloak (#124, Phase 3). When Enabled is
// false the other fields are empty/defaults and the SPA runs without auth.
public record AuthConfigDto(
    bool Enabled,
    string Authority,
    string ClientId,
    string Audience
);

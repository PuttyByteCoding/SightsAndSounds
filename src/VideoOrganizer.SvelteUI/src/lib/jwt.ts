// JWT claim helpers (#124). Pure functions split out of the auth store so they
// can be unit-tested without the Svelte runes runtime.

// Decode a JWT's payload (no verification — the API verifies; this is just to
// read claims for shaping the UI). Returns null on any malformed input.
export function decodeJwtClaims(token: string): Record<string, unknown> | null {
  try {
    const payload = token.split('.')[1];
    if (!payload) return null;
    const json = atob(payload.replace(/-/g, '+').replace(/_/g, '/'));
    return JSON.parse(json) as Record<string, unknown>;
  } catch {
    return null;
  }
}

// Keycloak puts realm roles in realm_access.roles on the ACCESS token, not the
// ID token. oidc-client-ts's user.profile is the ID token's claims, so roles
// must be read from the access token instead.
export function rolesFromAccessToken(accessToken: string | undefined): string[] {
  const claims = accessToken ? decodeJwtClaims(accessToken) : null;
  const realm = claims?.['realm_access'] as { roles?: string[] } | undefined;
  return Array.isArray(realm?.roles) ? realm!.roles! : [];
}

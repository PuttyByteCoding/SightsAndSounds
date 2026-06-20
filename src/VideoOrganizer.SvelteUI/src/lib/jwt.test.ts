import { describe, it, expect } from 'vitest';
import { decodeJwtClaims, rolesFromAccessToken } from './jwt';

// Build a JWT-shaped string (header.payload.signature) whose payload is the
// base64url encoding of `claims`. Signature is irrelevant — these helpers read
// claims without verifying (the API verifies).
function makeJwt(claims: Record<string, unknown>): string {
  const b64url = (obj: unknown) =>
    btoa(JSON.stringify(obj)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `${b64url({ alg: 'RS256', typ: 'JWT' })}.${b64url(claims)}.sig`;
}

describe('rolesFromAccessToken', () => {
  it('reads realm_access.roles from the access token (where Keycloak puts them)', () => {
    // Regression: roles must come from the ACCESS token, not the ID token —
    // owner (admin role) was being shown as read-only because the store read
    // u.profile (ID token), which has no realm_access.
    const token = makeJwt({ realm_access: { roles: ['admin'] }, preferred_username: 'owner' });
    expect(rolesFromAccessToken(token)).toEqual(['admin']);
  });

  it('returns the viewer role for a read-only user', () => {
    const token = makeJwt({ realm_access: { roles: ['viewer'] } });
    expect(rolesFromAccessToken(token)).toEqual(['viewer']);
  });

  it('returns [] when realm_access is absent (e.g. an ID token)', () => {
    expect(rolesFromAccessToken(makeJwt({ preferred_username: 'owner' }))).toEqual([]);
  });

  it('returns [] for undefined / malformed tokens instead of throwing', () => {
    expect(rolesFromAccessToken(undefined)).toEqual([]);
    expect(rolesFromAccessToken('')).toEqual([]);
    expect(rolesFromAccessToken('not-a-jwt')).toEqual([]);
    expect(rolesFromAccessToken('a.b.c')).toEqual([]);
  });
});

describe('decodeJwtClaims', () => {
  it('decodes a well-formed payload', () => {
    const token = makeJwt({ sub: '123', scope: 'profile email' });
    expect(decodeJwtClaims(token)).toMatchObject({ sub: '123', scope: 'profile email' });
  });

  it('returns null on garbage', () => {
    expect(decodeJwtClaims('garbage')).toBeNull();
  });
});

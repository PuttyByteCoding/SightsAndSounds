# Authentication with Keycloak (research / options for #124)

Status: **decided — implementation underway.** Chosen: **Approach A** (SPA
public client + Bearer, §3A), **Deployment Option 2** (Compose + Postgres,
persistent, realm-imported, §4) reusing the app's Postgres with a dedicated
`keycloak` DB, two users (`viewer` read-only / `owner` full control), localhost
+ LAN over HTTP with IP-allowlisting to follow. The how-to-run runbook lives in
[`auth-keycloak-setup.md`](./auth-keycloak-setup.md). This file is kept as the
research/rationale record.

Current Keycloak release at time of writing: **26.5.2** (Quarkus-based, image
`quay.io/keycloak/keycloak`).

---

## 1. Where we are today

Per `ARCHITECTURE.md`, this is a **local-first** tool with **no authentication** —
"anyone who can reach the port has full access." The only existing protection is
that OS-level endpoints (reveal, terminal, ffprobe) are gated to loopback.

The shape that matters for auth:

```
SvelteKit SPA (adapter-static — pure browser, NO Node server)
        │  served as static files from
        ▼
.NET 10 minimal API (5098)  ──▶  PostgreSQL
```

Two facts drive the design:

1. The SPA is **static** (`@sveltejs/adapter-static`). There is no SvelteKit
   server process, so a SvelteKit-side BFF isn't available — the BFF, if we want
   one, lives in the .NET API (which already serves the SPA's `wwwroot`).
2. The .NET API and the SPA are **same-origin** (API serves the SPA). That makes
   a cookie-based session (BFF) clean — no CORS, no cross-site cookie issues.

---

## 2. Industry standards & best practices (the non-negotiables)

- **Protocol: OpenID Connect** (OIDC, built on OAuth 2.0). Keycloak is an OIDC
  provider; we are the Relying Party (SPA) + Resource Server (API).
- **Flow: Authorization Code + PKCE (`S256`)** for the browser. This is the
  current best practice for SPAs and the only one we should use.
  - **Do NOT use** the Implicit flow or Resource Owner Password (ROPC) — both are
    deprecated by the OAuth 2.0 Security BCP.
- **Public client** for the SPA: client authentication **off**, standard flow
  **on**, PKCE challenge method **S256**. Public clients can't hold a secret, so
  PKCE is what protects the code exchange.
- **The API validates JWTs** (it's a resource server): verify signature against
  the realm's JWKS, check `iss` (issuer = realm URL) and `aud` (audience).
- **Token handling**: short-lived access tokens (~5 min), refresh via silent
  renew or refresh tokens. **Storing tokens in browser JS is the weak point** —
  see the BFF option in §3 for the more secure alternative (tokens never reach
  the browser).
- **HTTPS everywhere** in production; secrets via env/secret store, never in the
  image or git.
- **Realm-as-code**: export the realm to JSON and version it, so the IdP config
  is reproducible and reviewable (not click-ops). Keycloak imports it on startup
  with `--import-realm`.
- **Never run `start-dev` in production** — it uses an in-memory H2 DB (data lost
  on restart), disables HTTPS, and enables insecure defaults.

---

## 3. App-integration approaches (pick one — affects Keycloak client config)

### Approach A — SPA public client + Bearer tokens (simplest, most common)

The browser does Auth Code + PKCE (via `oidc-client-ts`), holds the access
token in memory, and sends it as `Authorization: Bearer <jwt>` to the .NET API.
The API validates the JWT.

- **Pros**: least moving parts; canonical Keycloak+SPA tutorial path; good for
  *getting experience* with Keycloak quickly.
- **Cons**: tokens live in the browser (XSS exposure); refresh-token handling in
  JS is fiddly; logout/session is more manual.

### Approach B — BFF (Backend-For-Frontend) in the .NET API (more secure)

The .NET API performs the OIDC login (`AddOpenIdConnect` + cookie), keeps the
tokens server-side, and the SPA just rides a **same-origin HttpOnly session
cookie**. The API attaches the access token to downstream calls itself.

- **Pros**: tokens never touch browser JS (XSS-resistant); aligns with the OAuth
  2.0 *Browser-Based Apps* BCP; logout/session is standard cookie auth; works
  cleanly because the API already serves the SPA same-origin.
- **Cons**: more wiring in the API; the SPA must use cookie auth + CSRF
  protection for state-changing requests.

> **Recommendation:** start with **A** to learn Keycloak, but know that **B** is
> the better long-term posture for this app given the API already hosts the SPA
> same-origin. The Keycloak server setup (§4) is identical either way — only the
> *client* definition differs (public for A; confidential for B).

---

## 4. Keycloak server: three deployment options (Docker)

All three use the official image and a realm we export to
`keycloak/realm-export.json` so config is reproducible.

### Option 1 — Dev quick-start (learning / throwaway)

One container, in-memory H2, no persistence. Great for a first hour with the
admin console; **not** for keeping any config.

```bash
docker run --rm -p 8080:8080 \
  -e KC_BOOTSTRAP_ADMIN_USERNAME=admin \
  -e KC_BOOTSTRAP_ADMIN_PASSWORD=admin \
  quay.io/keycloak/keycloak:26.5.2 start-dev
```

Admin console at http://localhost:8080. Everything you create vanishes when the
container stops.

- **Use when**: clicking around to learn the realm/client/role model.
- **Don't use for**: anything you want to keep or commit.

### Option 2 — Compose + PostgreSQL, persistent, realm-imported (recommended start)

Mirrors production shape (real DB, realm-as-code) but stays HTTP-on-localhost for
local dev. This is the one I'd actually wire into our `docker-compose`. It can
**reuse the existing Postgres container** with a dedicated `keycloak` database
(lighter), or run its own — shown here as its own for isolation.

```yaml
# docker-compose.keycloak.yml — merge into the project compose or run alongside
services:
  keycloak-db:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: keycloak
      POSTGRES_USER: keycloak
      POSTGRES_PASSWORD: ${KC_DB_PASSWORD}
    volumes:
      - keycloak-db-data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U keycloak"]
      interval: 10s
      timeout: 5s
      retries: 5

  keycloak:
    image: quay.io/keycloak/keycloak:26.5.2
    command: ["start-dev", "--import-realm"]   # start-dev is fine for LOCAL dev
    depends_on:
      keycloak-db:
        condition: service_healthy
    environment:
      KC_BOOTSTRAP_ADMIN_USERNAME: ${KC_ADMIN_USER}
      KC_BOOTSTRAP_ADMIN_PASSWORD: ${KC_ADMIN_PASSWORD}
      KC_DB: postgres
      KC_DB_URL: jdbc:postgresql://keycloak-db:5432/keycloak
      KC_DB_USERNAME: keycloak
      KC_DB_PASSWORD: ${KC_DB_PASSWORD}
      KC_HEALTH_ENABLED: "true"
    ports:
      - "8080:8080"
    volumes:
      - ./keycloak/realm-export.json:/opt/keycloak/data/import/realm-export.json:ro

volumes:
  keycloak-db-data:
```

- **Use when**: developing the integration; config persists and is reproducible.
- Note: still `start-dev` (HTTP, no optimized build) — correct for *local* only.

### Option 3 — Production-hardened (optimized build + Postgres + reverse proxy + TLS)

For exposing the app beyond localhost. Two phases: a **build** that bakes config
into an optimized image, then `start --optimized` at runtime. Runs behind a
TLS-terminating reverse proxy (Caddy/Traefik/nginx).

```dockerfile
# keycloak/Dockerfile — pre-built optimized image
FROM quay.io/keycloak/keycloak:26.5.2 AS builder
ENV KC_DB=postgres
RUN /opt/keycloak/bin/kc.sh build

FROM quay.io/keycloak/keycloak:26.5.2
COPY --from=builder /opt/keycloak/ /opt/keycloak/
ENTRYPOINT ["/opt/keycloak/bin/kc.sh"]
```

```yaml
  keycloak:
    build: ./keycloak
    command: ["start", "--optimized", "--import-realm"]
    environment:
      KC_DB: postgres
      KC_DB_URL: jdbc:postgresql://keycloak-db:5432/keycloak
      KC_DB_USERNAME: keycloak
      KC_DB_PASSWORD: ${KC_DB_PASSWORD}
      # Hostname v2: full public URL; proxy terminates TLS and forwards HTTP.
      KC_HOSTNAME: https://auth.example.com
      KC_PROXY_HEADERS: xforwarded
      KC_HTTP_ENABLED: "true"
      KC_HEALTH_ENABLED: "true"
      KC_BOOTSTRAP_ADMIN_USERNAME: ${KC_ADMIN_USER}
      KC_BOOTSTRAP_ADMIN_PASSWORD: ${KC_ADMIN_PASSWORD}
```

Production checklist: stable version tag; dedicated Postgres with backups;
memory limit ≥ 1 GB (Keycloak grabs all it can otherwise); rotate the bootstrap
admin password immediately; secrets from env/secret store; `/health/ready`
wired to the container healthcheck.

> **Suggested path:** Option 2 now (learn + build the integration against a
> persistent, reproducible realm), graduate the *same* realm JSON to Option 3
> when/if we expose the app.

---

## 5. Realm & client configuration (the same for all options)

Realm: `sightsandsounds`. Define two clients:

| Client | Type | Flow | Purpose |
|--------|------|------|---------|
| `sightsandsounds-spa` | **public** | Standard + PKCE (S256) | the browser app (Approach A) |
| `sightsandsounds-api` | bearer-only / confidential | — | audience the API validates tokens against |

(For Approach B, replace the public SPA client with a **confidential** client the
.NET API uses for the server-side code flow.)

Set per client: valid redirect URIs (`http://localhost:5173/*` dev,
`https://<app>/*` prod), web origins, and **PKCE = S256** on the SPA client.
Roles: realm roles like `admin`, `viewer`; assign to users/groups.

---

## 6. .NET API: validating the token (resource server)

```csharp
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Realm issuer URL. In Docker, the API reaches Keycloak by service name,
        // but the token's `iss` is the *public* URL — Authority must match `iss`.
        options.Authority = "http://localhost:8080/realms/sightsandsounds";
        options.Audience  = "sightsandsounds-api";
        options.RequireHttpsMetadata = false; // dev only — true in production
        options.TokenValidationParameters = new()
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
        };
    });
builder.Services.AddAuthorization();
// app.UseAuthentication(); app.UseAuthorization();  // before MapApiEndpoints
```

**Two Keycloak-specific gotchas to plan for:**

1. **Roles aren't standard claims.** Keycloak puts realm roles in
   `realm_access.roles` (and client roles in `resource_access.<client>.roles`),
   not the `role`/`ClaimTypes.Role` .NET expects. We need a small
   `IClaimsTransformation` to flatten `realm_access.roles` into role claims, or
   use the community `Keycloak.AuthServices.Authentication` package which does it.
2. **Lightweight access tokens (Keycloak 24+).** If the API client uses
   lightweight tokens, the JWT omits `realm_access`/`resource_access` and
   role checks silently 403. Fix: add a roles mapper to the API client's
   dedicated scope, or disable lightweight tokens for that client.

---

## 7. SvelteKit SPA (Approach A): the login flow

`oidc-client-ts` (maintained successor to `oidc-client`) handles Auth Code +
PKCE in the browser:

```ts
import { UserManager } from 'oidc-client-ts';
export const userManager = new UserManager({
  authority: 'http://localhost:8080/realms/sightsandsounds',
  client_id: 'sightsandsounds-spa',
  redirect_uri: `${location.origin}/auth/callback`,
  response_type: 'code',         // Authorization Code…
  scope: 'openid profile email', // …+ PKCE is automatic for public clients
});
// login: userManager.signinRedirect(); callback: signinRedirectCallback();
// then attach user.access_token as the Bearer header on api.ts fetches.
```

For Approach B instead, this disappears: the SPA hits `/login` on the .NET API,
which runs the OIDC handshake and drops a session cookie; `api.ts` just sends
`credentials: 'include'`.

---

## 8. Open questions to decide before coding

1. **Approach A (Bearer) or B (BFF cookie)?** A = faster to learn; B = more
   secure and a natural fit since the API hosts the SPA.
2. **Keycloak's database**: reuse the existing Postgres container (separate
   `keycloak` DB) or a dedicated one? Reuse is lighter for a single box.
3. **Single-user or multi-user?** If it's only ever you, a single realm user +
   `admin` role is enough; otherwise plan groups/roles.
4. **Exposure**: localhost-only (then Option 2 is plenty) vs internet-exposed
   (then Option 3 + TLS).

---

## Sources

- [Keycloak — Configuring the hostname (v2)](https://www.keycloak.org/server/hostname)
- [Is Keycloak Production Ready? A Practical Checklist (skycloak.io)](https://skycloak.io/blog/keycloak-production-ready-checklist/)
- [Keycloak with Docker (2025) — Mastertheboss](https://www.mastertheboss.com/keycloak/keycloak-with-docker/)
- [Securing Modern Applications with Keycloak in 2026 (Medium)](https://lalatenduswain.medium.com/securing-modern-applications-with-keycloak-in-2026-a-complete-guide-to-open-source-identity-8c66fcf3ea14)
- [Choosing the Best Authorization Flows for Your App (skycloak.io)](https://skycloak.io/blog/choosing-the-best-authorization-flows-for-your-app/)
- [Keycloak: How to create a PKCE Authorization Flow client (skycloak.io)](https://skycloak.io/blog/keycloak-how-to-create-a-pkce-authorization-flow-client/)
- [Configure Authorization — Keycloak.AuthServices (.NET)](https://nikiforovall.blog/keycloak-authorization-services-dotnet/configuration/configuration-authorization.html)
- [Accessing realm_access roles in JWT in .NET Core (codegenes.net)](https://www.codegenes.net/blog/can-t-access-roles-in-jwt-token-net-core/)
- [OAuth 2.0 Security Best Practice (IBM)](https://docs.verify.ibm.com/ibm-security-verify-access/docs/tasks-oauth2bestpractice)

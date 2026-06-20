# Keycloak setup & runbook (#124, Phase 1 — infrastructure)

The decisions are made (see [`auth-keycloak.md`](./auth-keycloak.md) for the
research behind them):

- **Approach A** — SPA public client + Bearer tokens.
- **Deployment Option 2** — Docker Compose + PostgreSQL, persistent,
  realm-imported. **Reusing the app's Postgres** with a dedicated `keycloak`
  database (not a separate DB container).
- **Multi-user** — two app users: `viewer` (read-only) and `owner` (full
  control), backed by realm roles `viewer` / `admin`.
- **Exposure** — localhost + LAN over HTTP for now (`sslRequired: none`); IP
  allowlisting comes in a later phase. TLS is Option 3, later.

> **Phase 1 only stands the server up.** The app is **not** wired to Keycloak
> yet — Sights & Sounds still runs with no auth. Phases 2–4 add API token
> validation, the SPA login, and the IP allowlist.

## What's here

| Path | Purpose |
| --- | --- |
| `docker-compose.yml` → `keycloak`, `keycloak-db-init` | the Keycloak server + a one-shot that creates the `keycloak` DB in the shared Postgres |
| `keycloak/realm-export.json` | realm-as-code: realm `sightsandsounds`, clients, roles, the two users |
| `.env` → `KC_ADMIN_USER` / `KC_ADMIN_PASSWORD` | bootstrap admin for the Keycloak **admin console** |

## Start it

```bash
# Postgres must be up (start.sh starts it). Then:
docker compose up -d keycloak

# First boot: pulls the image, keycloak-db-init creates the `keycloak` DB,
# Keycloak imports the realm, then listens on http://localhost:8080
docker compose logs -f keycloak     # watch for "Imported realm sightsandsounds"
```

- **Admin console:** http://localhost:8080 → log in with `KC_ADMIN_USER` /
  `KC_ADMIN_PASSWORD` (from `.env`). This is the master-realm superuser — your
  place to *learn Keycloak* and tweak config. Switch the realm dropdown to
  **sightsandsounds** to see the imported clients/roles/users.
- **Realm discovery:** http://localhost:8080/realms/sightsandsounds/.well-known/openid-configuration

## The realm at a glance

- **Realm:** `sightsandsounds`
- **Clients:**
  - `sightsandsounds-spa` — public, Auth Code + PKCE (S256). The browser app.
    (`directAccessGrantsEnabled` is on as a *dev convenience* so you can `curl` a
    token without a browser; the OAuth BCP discourages it — turn it off once the
    SPA login is the only path.)
  - `sightsandsounds-api` — bearer-only. The audience the API will validate
    (`aud`); the SPA client has an audience mapper that injects it.
- **Realm roles:** `admin` (full control), `viewer` (read-only).
- **Users (DEV passwords — change before exposing):**

  | Username | Password | Role | Access |
  | --- | --- | --- | --- |
  | `viewer` | `viewer` | `viewer` | read-only |
  | `owner`  | `owner`  | `admin`  | full control |

## Get a token (sanity check, no browser)

```bash
curl -s -d client_id=sightsandsounds-spa -d grant_type=password \
  -d username=viewer -d password=viewer \
  http://localhost:8080/realms/sightsandsounds/protocol/openid-connect/token | jq .access_token
```

Paste the JWT into https://jwt.io — you should see `realm_access.roles`
containing `viewer` and `aud` containing `sightsandsounds-api`.

## Accessing from other computers on your LAN

The realm allows HTTP for LAN (`sslRequired: none`). To log in from another
machine you must:

1. Add that origin to the SPA client's **Valid redirect URIs** and **Web
   origins** — either edit `keycloak/realm-export.json`
   (`http://<lan-ip>:5173/*`, `http://<lan-ip>:5098/*`) and re-import, or add
   them live in the admin console (Clients → sightsandsounds-spa).
2. Tokens carry the issuer URL the browser used, so for LAN you'll set
   `KC_HOSTNAME` on the Keycloak service to a stable URL the API can also reach
   (planned for the API-integration phase).

## Reset / re-import

`--import-realm` imports only if the realm is absent (it persists in Postgres
after first import). To re-apply an edited `realm-export.json` from scratch:

```bash
docker compose rm -sf keycloak
docker exec sights-and-sounds-postgres psql -U "$POSTGRES_USER" -d postgres -c "DROP DATABASE keycloak"
docker compose up -d keycloak
```

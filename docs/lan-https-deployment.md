# LAN deployment: auth + IP allowlist + HTTPS

How to expose Sights and Sounds to other devices on your LAN with Keycloak
login, an IP allowlist, and forced HTTPS. Covers issues #124, #221, #222.

The app ships **off** on all three (no auth, no IP restriction, plain HTTP) so
the local quickstart needs no certificate. Everything here is opt-in via `.env`;
no tracked defaults change. The worked example uses:

| Role | Address |
|---|---|
| Host (this server) | `192.168.4.38` |
| Allowed client devices | `192.168.4.120`, `192.168.4.24` |

Substitute your own IPs throughout.

## Serve model: single origin on :5098 (not the dev server)

In normal development (`start.sh`) the UI is served by the **Vite dev server on
`:5173` over plain HTTP**, which proxies `/api` to the API on `:5098`. That dev
server is **not** part of a secured deployment — it has no TLS, and browsing it
with `https://…:5173` fails with `SSL_ERROR_RX_RECORD_TOO_LONG` (HTTPS spoken to
an HTTP socket).

For the LAN deployment, `serve.sh` builds the SPA into the API's `wwwroot` and
runs **only the API**, so the UI and `/api` share **one HTTPS origin on `:5098`**.
You browse **`https://192.168.4.38:5098`** — there is no `:5173`. Single-origin
also keeps the media cookie (`Path=/api`, `SameSite=Lax`) and CORS trivial.

## Why the issuer/hostname matters (#221)

Keycloak stamps an **issuer** (`iss`) into every token, derived from the URL the
browser used to reach it. The API validates that issuer against `Auth:Authority`.
If they don't match — the classic symptom being "login only works on the host" —
every LAN device gets a 401. So three things must name the **same** host+scheme:

1. `Auth__Authority` (the API — what it expects the issuer to be),
2. `KC_HOSTNAME` (Keycloak — what issuer it actually stamps),
3. the `redirectUris` / `webOrigins` registered on the `sightsandsounds-spa`
   client in `keycloak/realm-export.json` (already include `192.168.4.38`).

`KC_HOSTNAME` defaults to `localhost`, which matches the default `Auth:Authority`
— that's why on-host login works untouched. For LAN access you point both at the
host's LAN URL.

## Steps

### 1. Generate a certificate (#222)

```bash
./gen-cert.sh 192.168.4.38 <choose-a-pfx-password>
```

Writes `certs/` (gitignored): a **Root CA** (`ca.crt.pem` / `ca.key.pem`) and a
**server leaf** (`sights.crt.pem` / `sights.key.pem` for Keycloak, `sights.pfx`
for Kestrel). The leaf carries an **IP SAN** plus the `serverAuth` EKU so browsers
accept it when you reach the host by raw IP.

**Trust `certs/ca.crt.pem` — the Root CA — on every device** that will browse the
app (commands printed by the script). Do **not** trust the leaf directly: a cert
used as the server leaf must be `CA:FALSE` with `serverAuth`, so the trust anchor
has to be the separate CA. If you previously trusted a `sights.crt.pem`, remove it
from the trust stores first.

### 2. Fill in `.env`

Uncomment and adjust the LAN block in `.env` (see `.env.example`):

```sh
Auth__Enabled=true
Auth__Authority=https://192.168.4.38:8443/realms/sightsandsounds
Auth__RequireHttpsMetadata=true

KC_HOSTNAME=https://192.168.4.38:8443
KC_DISCOVERY_URL=https://192.168.4.38:8443/realms/sightsandsounds/.well-known/openid-configuration

Network__RestrictByIp=true
Network__AllowedCidrs__0=192.168.4.120/32
Network__AllowedCidrs__1=192.168.4.24/32
Network__AllowedCidrs__2=192.168.4.38/32

Network__ForceHttps=true
ASPNETCORE_URLS=https://0.0.0.0:5098
Kestrel__Certificates__Default__Path=certs/sights.pfx
Kestrel__Certificates__Default__Password=<same-pfx-password>
```

### 3. Serve over HTTPS

```bash
./serve.sh
```

`serve.sh` builds the SPA into the API's `wwwroot`, brings up Postgres, starts
Keycloak (over TLS via `docker-compose.tls.yml` when `certs/` exists), and runs
**only the API** on HTTPS `:5098`. It does **not** start the Vite dev server.

(If you prefer to drive the pieces by hand: `npm run build` in
`src/VideoOrganizer.SvelteUI`, copy `build/` into
`src/VideoOrganizer.API/wwwroot/`, `docker compose -f docker-compose.yml -f
docker-compose.tls.yml up -d keycloak`, then run the API with the `.env` above.)

### 4. Verify

- From `192.168.4.120` / `192.168.4.24` / the host (`192.168.4.38`): browse
  **`https://192.168.4.38:5098`** — the SPA loads over HTTPS, Keycloak login
  completes, `viewer` can read but is 403'd on writes, `owner` can write, and
  video/images stream.
- From any other LAN IP: `403` before the SPA even loads.
- `http://192.168.4.38:5098` redirects to HTTPS; no mixed-content errors in the console.
- Do **not** use `:5173` — that's the HTTP-only dev server, not part of this deployment.

## Recovering from a lockout

The IP allowlist runs first and 403s anything not on the list — including the SPA.
Loopback is always allowed, so SSH to the host and browse via `http://localhost`
(or fix `.env` and `./start.sh`) to recover. Double-check the allowlist before
enabling it remotely.

## Editing the realm later ("Invalid parameter: redirect_uri")

Keycloak's `--import-realm` only imports a realm the **first** time, when it
doesn't yet exist in the database. After that, edits to `keycloak/realm-export.json`
(redirect URIs, web origins, clients, users, roles) are ignored — a symptom is
Keycloak showing **"Invalid parameter: redirect_uri"** at login because the
running realm still has the old URIs. Re-import with:

```bash
./reset-keycloak.sh
```

It drops + recreates the dedicated `keycloak` database and restarts Keycloak so
the realm re-imports. The app's `videoorganizer` database (videos, tags) is
untouched; the Keycloak admin password resets to your `.env` values.

## Notes / limits

- The realm's `sslRequired` is `external`: HTTPS is required for external clients
  but HTTP is still tolerated from private/loopback IPs, which keeps bring-up and
  debugging easy. The TLS overlay leaves Keycloak's plain-HTTP `:8080` listener
  mapped (start-dev serves both) — point clients at `:8443`. Dropping HTTP
  entirely means moving Keycloak to production `start` mode, a follow-up noted in #222.
- Behind a reverse proxy, enable `ForwardedHeaders` so the IP allowlist sees the
  real client IP rather than the proxy's.

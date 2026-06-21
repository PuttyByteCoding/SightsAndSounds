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

Writes `certs/` (gitignored): `sights.crt.pem` + `sights.key.pem` (Keycloak) and
`sights.pfx` (Kestrel). The cert has an **IP SAN** so browsers accept it when you
reach the host by raw IP. It's self-signed, so **trust `certs/sights.crt.pem` once
on every device** that will browse the app (commands printed by the script).

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

### 3. Start Keycloak with the TLS overlay

`start.sh` brings up Keycloak automatically when `Auth__Enabled=true`, but the
base compose file serves it over plain HTTP. For HTTPS on Keycloak, start it with
the overlay before (or instead of) letting `start.sh` do it:

```bash
docker compose -f docker-compose.yml -f docker-compose.tls.yml up -d keycloak
./start.sh
```

### 4. Verify

- From `192.168.4.120` / `192.168.4.24` / the host (`192.168.4.38`): the SPA loads
  over HTTPS, Keycloak login completes, `viewer` can read but is 403'd on writes,
  `owner` can write, and video/images stream.
- From any other LAN IP: `403` before the SPA even loads.
- `http://192.168.4.38:5098` redirects to HTTPS; no mixed-content errors in the console.

## Recovering from a lockout

The IP allowlist runs first and 403s anything not on the list — including the SPA.
Loopback is always allowed, so SSH to the host and browse via `http://localhost`
(or fix `.env` and `./start.sh`) to recover. Double-check the allowlist before
enabling it remotely.

## Notes / limits

- The realm's `sslRequired` is `external`: HTTPS is required for external clients
  but HTTP is still tolerated from private/loopback IPs, which keeps bring-up and
  debugging easy. The TLS overlay leaves Keycloak's plain-HTTP `:8080` listener
  mapped (start-dev serves both) — point clients at `:8443`. Dropping HTTP
  entirely means moving Keycloak to production `start` mode, a follow-up noted in #222.
- Behind a reverse proxy, enable `ForwardedHeaders` so the IP allowlist sees the
  real client IP rather than the proxy's.
- The PowerShell scripts (`*.ps1`) don't yet mirror the auth/Keycloak startup
  wiring that `start.sh` has; on Windows start Keycloak via `docker compose` manually.

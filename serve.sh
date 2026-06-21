#!/usr/bin/env bash
# Single-origin HTTPS serve (#222). Builds the SvelteKit SPA into the API's
# wwwroot and runs ONLY the API (Kestrel), so the UI and /api share one TLS
# origin on :5098 — no Vite dev server on the LAN. Use this for a secured LAN
# deployment; use start.sh for the plain-HTTP dev loop with hot-reload.
#
# Prereqs (see docs/lan-https-deployment.md): a cert from ./gen-cert.sh and a
# .env with ASPNETCORE_URLS=https://0.0.0.0:5098, the Kestrel cert vars, and
# (typically) Auth__Enabled / Network__RestrictByIp / Network__ForceHttps on.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=./_lib.sh
source "$SCRIPT_DIR/_lib.sh"

cd "$REPO_ROOT"

if [ -f .env ]; then
  set -a; source .env; set +a
else
  warn env ".env missing — copy .env.example to .env first"
  exit 1
fi

# 1. Build the SPA and copy it into the API's wwwroot (what Kestrel serves via
#    UseStaticFiles + MapFallbackToFile). Plain `dotnet run` does NOT do this —
#    the copy only happens on `dotnet publish` (the BuildSvelteUI target) — so
#    serve.sh does it explicitly. Keep .gitkeep; replace everything else.
log build "building SvelteKit SPA"
( cd src/VideoOrganizer.SvelteUI && npm ci && npm run build )
WWWROOT="src/VideoOrganizer.API/wwwroot"
log build "copying build/ -> $WWWROOT"
find "$WWWROOT" -mindepth 1 ! -name '.gitkeep' -delete
cp -r src/VideoOrganizer.SvelteUI/build/. "$WWWROOT"/

# 2. Postgres.
log compose "starting postgres"
docker compose up -d postgres
wait_postgres

# 3. Keycloak — only when auth is on. With a cert present, serve it over TLS
#    (:8443) via the overlay; KC_DISCOVERY_URL in .env points the readiness
#    poll at the matching scheme/port.
if [ "${Auth__Enabled:-false}" = "true" ]; then
  if [ -f certs/sights.crt.pem ]; then
    log compose "Auth on + cert present — starting keycloak over TLS (:8443)"
    docker compose -f docker-compose.yml -f docker-compose.tls.yml up -d keycloak
  else
    log compose "Auth on (no certs/ found) — starting keycloak over HTTP (:8080)"
    docker compose up -d keycloak
  fi
  wait_keycloak
fi

# 4. API only — Kestrel binds HTTPS from ASPNETCORE_URLS + the cert in .env.
start_bg api dotnet run --project src/VideoOrganizer.API

log done "App:  ${ASPNETCORE_URLS:-https://0.0.0.0:5098}  (browse the host's LAN IP, e.g. https://192.168.4.38:5098)"
if [ "${Auth__Enabled:-false}" = "true" ]; then
  log done "Auth: keycloak up (realm 'sightsandsounds')"
fi
log done "Logs: tail -f $RUN_DIR/api.log"
log done "Stop: ./stop.sh"

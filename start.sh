#!/usr/bin/env bash
# Start postgres (Docker), API (dotnet), and SvelteKit dev server (npm).
# The DB is left intact — use start-fresh.sh if you want to wipe data.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=./_lib.sh
source "$SCRIPT_DIR/_lib.sh"

cd "$REPO_ROOT"

# Load .env so the API/ImportTool inherit POSTGRES_* (they build the
# connection string from these). docker compose reads .env on its own.
if [ -f .env ]; then
  set -a; source .env; set +a
else
  warn env ".env missing — copy .env.example to .env first"
  exit 1
fi

log compose "starting postgres + seq"
docker compose up -d postgres seq
wait_postgres

# Keycloak is only needed when auth is on (Auth__Enabled=true in .env). It
# defaults off, so by default we skip it and the app runs with no auth — same
# as before. When enabled, bring up Keycloak (depends_on pulls in the
# keycloak-db-init one-shot) and wait for the realm to import before starting
# the API, so the API's JWT authority is reachable on first request.
if [ "${Auth__Enabled:-false}" = "true" ]; then
  log compose "Auth__Enabled=true — starting keycloak"
  docker compose up -d keycloak
  wait_keycloak
fi

start_bg api dotnet run --project src/VideoOrganizer.API
start_bg ui  bash -c 'cd src/VideoOrganizer.SvelteUI && npm run dev'

log done "API:  http://localhost:5098  (Swagger at /swagger)"
log done "UI:   http://localhost:5173  (Vite proxies /api to :5098)"
log done "Seq:  http://localhost:5341  (structured logs)"
if [ "${Auth__Enabled:-false}" = "true" ]; then
  log done "Auth: http://localhost:8080  (Keycloak admin console; realm 'sightsandsounds')"
fi
log done "Logs: tail -f $RUN_DIR/api.log  |  tail -f $RUN_DIR/ui.log"
log done "Stop: ./stop.sh"

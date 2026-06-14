#!/usr/bin/env bash
# Wipe the postgres volume, then start postgres + API + UI.
# Useful after schema changes that aren't covered by a migration, or just to
# start with a clean slate. EF Core re-applies all migrations on first boot.
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

# Stop anything currently running so the DB volume can be removed cleanly.
log reset "stopping any existing services"
"$SCRIPT_DIR/stop.sh" || true

log reset "tearing down postgres + volume"
docker compose down -v

log compose "starting fresh postgres + seq"
docker compose up -d postgres seq
wait_postgres

start_bg api dotnet run --project src/VideoOrganizer.API
start_bg ui  bash -c 'cd src/VideoOrganizer.SvelteUI && npm run dev'

log done "Database wiped. Migrations will run automatically on first API request."
log done "API:  http://localhost:5098  (Swagger at /swagger)"
log done "UI:   http://localhost:5173"
log done "Stop: ./stop.sh"

#!/usr/bin/env bash
# Re-import the Keycloak realm from keycloak/realm-export.json (#124).
#
# Keycloak's `--import-realm` only imports a realm when it does NOT already exist
# in the database, so edits to keycloak/realm-export.json (new redirect URIs,
# web origins, clients, users, roles) are silently ignored once the realm has
# been imported. This drops the dedicated `keycloak` database and restarts
# Keycloak so the realm re-imports from scratch.
#
# SAFE: the `keycloak` database is separate from the app's data — the
# `videoorganizer` database (videos, tags, …) is never touched. The realm and
# its seeded users (owner/viewer) are fully defined by realm-export.json, so
# nothing is lost. NOTE: this resets the Keycloak admin password back to
# KC_ADMIN_USER / KC_ADMIN_PASSWORD from .env.
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
PGUSER="${POSTGRES_USER:-postgresuser}"

log keycloak "stopping keycloak"
docker compose stop keycloak >/dev/null 2>&1 || true

docker compose up -d postgres >/dev/null
wait_postgres

# Drop + recreate the dedicated keycloak database. WITH (FORCE) terminates any
# lingering connections (Postgres 13+). The app's videoorganizer DB is a
# different database in the same instance and is not affected.
log keycloak "dropping + recreating the 'keycloak' database (app data is untouched)"
docker compose exec -T postgres psql -U "$PGUSER" -d postgres \
  -c "DROP DATABASE IF EXISTS keycloak WITH (FORCE);"
docker compose exec -T postgres psql -U "$PGUSER" -d postgres \
  -c "CREATE DATABASE keycloak;"

# Bring Keycloak back so it re-imports the realm into the now-empty DB. Use the
# TLS overlay when a cert is present (matching serve.sh), so the issuer/port line
# up with Auth__Authority.
if [ -f certs/sights.crt.pem ]; then
  log keycloak "starting keycloak over TLS (:8443) — re-importing realm"
  docker compose -f docker-compose.yml -f docker-compose.tls.yml up -d keycloak
else
  log keycloak "starting keycloak over HTTP (:8080) — re-importing realm"
  docker compose up -d keycloak
fi
wait_keycloak

log done "realm re-imported from keycloak/realm-export.json"
log done "admin password reset to KC_ADMIN_USER/KC_ADMIN_PASSWORD"

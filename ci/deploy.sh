#!/usr/bin/env bash
# Local deploy — restart the running stack with the latest build,
# applying any pending EF migrations along the way.
#
# Order matters: check + build first so a broken state isn't shipped;
# then stop the running API + UI; then make sure postgres is up;
# then migrate; then start everything back up.
#
# Flags:
#   --force        proceed even with uncommitted changes
#   --skip-build   skip check + build (assume already done)
#
# Refuses to deploy with uncommitted changes unless --force is passed.

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../_lib.sh
source "$SCRIPT_DIR/../_lib.sh"

cd "$REPO_ROOT"

FORCE=0
SKIP_BUILD=0
for arg in "$@"; do
  case "$arg" in
    --force)      FORCE=1 ;;
    --skip-build) SKIP_BUILD=1 ;;
    *)            warn deploy "unknown flag: $arg"; exit 2 ;;
  esac
done

step() {
  local name="$1"; shift
  log step "$name"
  local started
  started="$(date +%s)"
  if ! "$@"; then
    warn fail "$name"
    return 1
  fi
  log done "$(printf '%s (%ds)' "$name" "$(( $(date +%s) - started ))")"
}

# --- Uncommitted-changes guard --------------------------------------
if [ "$FORCE" -ne 1 ]; then
  if ! git diff --quiet || ! git diff --cached --quiet; then
    warn deploy 'working tree has uncommitted changes:'
    git status --porcelain
    warn deploy 'commit (or stash) first, or re-run with --force.'
    exit 1
  fi
else
  warn deploy '--force set, ignoring dirty working tree.'
fi

if [ "$SKIP_BUILD" -ne 1 ]; then
  step 'check' "$SCRIPT_DIR/check.sh"
  step 'build' "$SCRIPT_DIR/build.sh"
else
  warn deploy '--skip-build set, jumping straight to stop/migrate/start'
fi

# --- Stop the running stack -----------------------------------------
# stop.sh is the source of truth for tearing down the local dev
# processes (it knows about .run/api.pid + .run/ui.pid).
step 'stop running stack (stop.sh)' bash ./stop.sh

# --- Make sure postgres is up ---------------------------------------
# stop.sh stops the postgres container too. Bring it back so the
# migration can connect. wait_postgres() comes from _lib.sh.
docker_up_pg() {
  docker compose up -d postgres
  wait_postgres
}
step 'docker compose up -d postgres' docker_up_pg

# --- Apply pending migrations ---------------------------------------
step 'dotnet ef database update' \
  dotnet ef database update \
    --project src/VideoOrganizer.Infrastructure \
    --startup-project src/VideoOrganizer.API

# --- Restart --------------------------------------------------------
step 'start running stack (start.sh)' bash ./start.sh

log done 'deploy complete'

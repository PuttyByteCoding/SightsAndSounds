#!/usr/bin/env bash
# Stop UI, API, and postgres. Safe to run multiple times.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=./_lib.sh
source "$SCRIPT_DIR/_lib.sh"

cd "$REPO_ROOT"

stop_pidfile ui
stop_pidfile api

log compose "stopping postgres + seq containers"
docker compose stop postgres seq

log done "all stopped"

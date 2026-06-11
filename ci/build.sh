#!/usr/bin/env bash
# Full build — backend (Release config) + frontend static output.
# Slower than ci/check.sh; meant for manual runs and ci/all.sh.
#
# Backend: API and Import are the two entry-point projects; building
# them transitively builds Domain, Shared, and Infrastructure.
# Frontend: `npm ci` for reproducible installs + `npm run build` for
# the SvelteKit static output under src/VideoOrganizer.SvelteUI/build.

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../_lib.sh
source "$SCRIPT_DIR/../_lib.sh"

cd "$REPO_ROOT"

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

backend_build() {
  # Build the two top-level entry-point projects. Each pulls in its
  # dependency csprojs (Domain, Shared, Infrastructure) so nothing
  # is left out.
  dotnet build src/VideoOrganizer.API/VideoOrganizer.API.csproj \
    --configuration Release --nologo
  dotnet build src/VideoOrganizer.Import/VideoOrganizer.Import.csproj \
    --configuration Release --nologo
}

ui_ci() (
  cd "$REPO_ROOT/src/VideoOrganizer.SvelteUI"
  # `ci` not `install` — the former honors package-lock.json strictly
  # and refuses to update it. Reproducible installs.
  npm ci
)

ui_build() (
  cd "$REPO_ROOT/src/VideoOrganizer.SvelteUI"
  npm run build
)

step 'dotnet build (Release) — API + Import' backend_build
step 'npm ci (SvelteUI)'                     ui_ci
step 'npm run build (SvelteUI)'              ui_build

log done 'build succeeded'

#!/usr/bin/env bash
# Lightweight checks — fast enough to live in a pre-commit hook.
# Three stages:
#   1. svelte-check       — TypeScript + template diagnostics for SvelteUI
#   2. dotnet format      — formatting + whitespace + style on every .csproj
#   3. migration smoke    — verify EF migrations compile without applying
#
# Skips integration / network work. Long-running stages (build, test)
# live in ci/build.sh and ci/test.sh.

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../_lib.sh
source "$SCRIPT_DIR/../_lib.sh"

cd "$REPO_ROOT"

# Run a labeled step. On failure: print a clear "[fail] step X" line
# and propagate the exit code so the script aborts.
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

# Collect every .csproj under src/, excluding obj/ output.
list_projects() {
  find "$REPO_ROOT/src" -name '*.csproj' -not -path '*/obj/*' | sort
}

# --- 1. svelte-check ---------------------------------------------------
svelte_check() (
  cd "$REPO_ROOT/src/VideoOrganizer.SvelteUI"
  # `npm run check` runs `svelte-kit sync` + `svelte-check`. sync is
  # a no-op on subsequent runs.
  npm run check
)

# --- 2. dotnet format --verify-no-changes ------------------------------
# `set -e` doesn't propagate from inside `done < <(…)` (process
# substitution opens a subshell), so a failing `dotnet format` here
# would silently get logged and the function would still return 0
# — making the whole check.sh appear green while a real violation
# slipped through. Same shape of bug bit dotnet_test() in test.sh.
# Track failures explicitly and re-raise.
dotnet_format() {
  local csproj
  local failed=0
  while IFS= read -r csproj; do
    log format "$csproj"
    if ! dotnet format "$csproj" --verify-no-changes --severity error; then
      warn format "$csproj — FAILED"
      failed=1
    fi
  done < <(list_projects)
  if [ "$failed" -ne 0 ]; then
    return 1
  fi
}

# --- 3. EF migration smoke ---------------------------------------------
# Generate the migrations as a single .sql file. Doesn't touch the live
# database — purely a compile-time check that every migration class is
# sound, the DbContext is consistent, and the model snapshot is valid.
#
# Cleanup is explicit (not a RETURN trap) because under `set -u` the
# trap re-reads $out after the function returns, by which point the
# local has gone out of scope and bash trips on "unbound variable".
ef_smoke() {
  local out rc=0
  out="$(mktemp)"
  dotnet ef migrations script \
    --project src/VideoOrganizer.Infrastructure \
    --startup-project src/VideoOrganizer.API \
    --output "$out" \
    --idempotent >/dev/null || rc=$?
  rm -f "$out"
  return "$rc"
}

step 'svelte-check'                            svelte_check
step 'dotnet format --verify-no-changes'       dotnet_format
step 'ef migrations smoke (script generation)' ef_smoke

log done 'all checks passed'

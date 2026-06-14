#!/usr/bin/env bash
# Run all tests — backend (.NET) + frontend (SvelteUI).
#
# Discovers .NET test projects by scanning the whole repo for csprojs
# that reference Microsoft.NET.Test.Sdk (the canonical "this is a test
# project" marker) — this covers both src/ (VideoOrganizer.UnitTests)
# and tests/ (VideoOrganizer.Tests). Frontend tests run via `npm test`
# in SvelteUI; the script checks whether that script is defined and
# skips with a warning if not.

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

# Find every csproj that references Microsoft.NET.Test.Sdk (the
# canonical "this is a test project" marker).
list_test_projects() {
  while IFS= read -r csproj; do
    if grep -q 'Microsoft\.NET\.Test\.Sdk' "$csproj" 2>/dev/null; then
      echo "$csproj"
    fi
  done < <(find "$REPO_ROOT" -name '*.csproj' \
    -not -path '*/obj/*' -not -path '*/bin/*' -not -path '*/node_modules/*')
}

dotnet_test() {
  local found=0
  local failed=0
  # `set -e` doesn't propagate from inside `done < <(…)` (process
  # substitution opens a subshell), so a failing `dotnet test` here
  # would silently get logged and the script would still report
  # success at the end. Track failures explicitly and re-raise at
  # the end of the function so the step() wrapper actually sees it.
  while IFS= read -r csproj; do
    found=1
    log test "$csproj"
    # No --no-restore: each test project (and its referenced projects) must
    # restore on a clean checkout — ci/build.sh only restores API + Import, so
    # the test projects wouldn't otherwise be restored (e.g. on a CI runner).
    if ! dotnet test "$csproj" --configuration Release --nologo; then
      warn test "$csproj — FAILED"
      failed=1
    fi
  done < <(list_test_projects)
  if [ "$found" -eq 0 ]; then
    warn tests 'no .NET test projects yet — skipping'
  fi
  if [ "$failed" -ne 0 ]; then
    return 1
  fi
}

ui_test() (
  cd "$REPO_ROOT/src/VideoOrganizer.SvelteUI"
  # Check for a defined `test` script in package.json. `npm test`
  # without one exits with status 1; we want to skip cleanly instead.
  if node -e "process.exit(require('./package.json').scripts?.test ? 0 : 1)"; then
    npm test
  else
    warn tests 'no npm test script defined — skipping'
  fi
)

step 'dotnet test (discover test projects)' dotnet_test
step 'npm test (SvelteUI)'                  ui_test

log done 'tests complete'

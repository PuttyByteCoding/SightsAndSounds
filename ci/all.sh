#!/usr/bin/env bash
# Full local CI run — check + build + test. The "did anything break?"
# button. Use before push, before deploy, or just whenever you want
# to verify the tree is green.
#
# Bails on the first failing stage so the error is easy to find.

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../_lib.sh
source "$SCRIPT_DIR/../_lib.sh"

cd "$REPO_ROOT"

overall_start="$(date +%s)"

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

step 'check' "$SCRIPT_DIR/check.sh"
step 'build' "$SCRIPT_DIR/build.sh"
step 'test'  "$SCRIPT_DIR/test.sh"

log done "$(printf 'CI passed (%ds total)' "$(( $(date +%s) - overall_start ))")"

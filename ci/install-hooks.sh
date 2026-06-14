#!/usr/bin/env bash
# Activate the in-repo pre-commit hook by pointing git at .githooks.
#
# git's default `core.hooksPath` is .git/hooks/ — a per-clone
# directory that lives outside the repo (and thus outside version
# control). Setting `core.hooksPath = .githooks` makes git look at
# the tracked directory instead, so every contributor gets the same
# hooks automatically after running this once.
#
# Also chmod +x the hook in case the clone dropped its execute bit
# (Windows clones over HTTPS sometimes do).
#
# Idempotent — running it twice is a no-op.

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../_lib.sh
source "$SCRIPT_DIR/../_lib.sh"

cd "$REPO_ROOT"

log step 'set core.hooksPath = .githooks'
git config core.hooksPath .githooks

log hooks "core.hooksPath = $(git config --get core.hooksPath)"

if [ -f "$REPO_ROOT/.githooks/pre-commit" ]; then
  chmod +x "$REPO_ROOT/.githooks/pre-commit"
  log hooks 'pre-commit hook present (./.githooks/pre-commit)'
else
  warn hooks 'pre-commit hook missing — expected ./.githooks/pre-commit'
fi

log done 'hooks installed. Bypass with `git commit --no-verify`.'

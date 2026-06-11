# Activate the in-repo pre-commit hook by pointing git at .githooks.
#
# git's default `core.hooksPath` is .git/hooks/ — a per-clone
# directory that lives outside the repo (and thus outside version
# control). Setting `core.hooksPath = .githooks` makes git look at
# the tracked directory instead, so every contributor gets the same
# hooks automatically after running this once.
#
# Idempotent — running it twice is a no-op.

. "$PSScriptRoot/_lib.ps1"

Push-Location $RepoRoot
try {
    Invoke-Step -Name 'set core.hooksPath = .githooks' -Body {
        git config core.hooksPath .githooks
    }
    # Sanity check: read the value back so the script's success
    # message reflects the actual state, not just the exit code of
    # the set command.
    $configured = (& git config --get core.hooksPath) 2>$null
    Write-Tag -Tag 'hooks' -Message "core.hooksPath = $configured"

    if (Test-Path "$RepoRoot/.githooks/pre-commit") {
        Write-Tag -Tag 'hooks' -Message 'pre-commit hook present (./.githooks/pre-commit)'
    } else {
        Write-Warn -Tag 'hooks' -Message 'pre-commit hook missing — expected ./.githooks/pre-commit'
    }

    Write-Tag -Tag 'done' -Message 'hooks installed. Bypass with `git commit --no-verify`.'
}
finally {
    Pop-Location
}

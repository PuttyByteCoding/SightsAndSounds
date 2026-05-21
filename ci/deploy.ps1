# Local deploy — restart the running stack with the latest build,
# applying any pending EF migrations along the way.
#
# Order matters: check + build first so a broken state isn't shipped;
# then stop the running API + UI; then make sure postgres is up;
# then migrate; then start everything back up.
#
# Refuses to deploy with uncommitted changes unless -Force is passed.
# That's a guard against the common "I'll just deploy this real
# quick" → forget to commit → revisit hours later and can't tell
# what's in the running build.

[CmdletBinding()]
param(
    [switch]$Force,
    # When set, skip the check + build stages (assume already done).
    # Useful when you've just finished a manual debug cycle and
    # know the working tree is clean.
    [switch]$SkipBuild
)

. "$PSScriptRoot/_lib.ps1"

Push-Location $RepoRoot
try {
    # --- Uncommitted-changes guard -----------------------------------
    if (-not $Force) {
        $dirty = git status --porcelain 2>$null
        if ($LASTEXITCODE -eq 0 -and $dirty) {
            Write-Warn -Tag 'deploy' -Message 'working tree has uncommitted changes:'
            Write-Host $dirty
            Write-Warn -Tag 'deploy' -Message 'commit (or stash) first, or re-run with -Force.'
            throw 'aborting deploy due to dirty working tree'
        }
    } elseif ($Force) {
        Write-Warn -Tag 'deploy' -Message '-Force set, ignoring dirty working tree.'
    }

    if (-not $SkipBuild) {
        Invoke-Step -Name 'check'  -Body { & "$PSScriptRoot/check.ps1" }
        Invoke-Step -Name 'build'  -Body { & "$PSScriptRoot/build.ps1" }
    } else {
        Write-Warn -Tag 'deploy' -Message '-SkipBuild set, jumping straight to stop/migrate/start'
    }

    # --- Stop the running stack --------------------------------------
    # stop.sh is the source of truth for tearing down the local dev
    # processes (it knows about .run/api.pid + .run/ui.pid). Reuse it
    # rather than duplicating the kill_tree logic in PowerShell.
    Invoke-Step -Name 'stop running stack (stop.sh)' -Body {
        bash ./stop.sh
    }

    # --- Make sure postgres is up ------------------------------------
    # stop.sh stops the postgres container too. Bring it back so
    # the migration can connect.
    Invoke-Step -Name 'docker compose up -d postgres' -Body {
        docker compose up -d postgres
        # Lightweight wait — pg_isready inside the container. Matches
        # what start.sh does so the migration doesn't race the startup.
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-Date) -lt $deadline) {
            $r = docker compose exec -T postgres pg_isready -U postgresuser -d videoorganizer 2>$null
            if ($LASTEXITCODE -eq 0) { return }
            Start-Sleep -Seconds 1
        }
        throw 'postgres did not become ready within 60s'
    }

    # --- Apply pending migrations ------------------------------------
    Invoke-Step -Name 'dotnet ef database update' -Body {
        dotnet ef database update `
            --project src/VideoOrganizer.Infrastructure `
            --startup-project src/VideoOrganizer.API
    }

    # --- Restart -----------------------------------------------------
    Invoke-Step -Name 'start running stack (start.sh)' -Body {
        bash ./start.sh
    }

    Write-Tag -Tag 'done' -Message 'deploy complete'
}
finally {
    Pop-Location
}

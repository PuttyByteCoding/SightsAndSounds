# Lightweight checks — fast enough to live in a pre-commit hook.
# Three stages:
#   1. svelte-check       — TypeScript + template diagnostics for SvelteUI
#   2. dotnet format      — formatting + whitespace + style on every .csproj
#   3. migration smoke    — verify EF migrations compile without applying
#
# Skips integration / network work. Long-running stages (build, test)
# live in ci/build.ps1 and ci/test.ps1.

. "$PSScriptRoot/_lib.ps1"

Push-Location $RepoRoot
try {
    Invoke-Step -Name 'svelte-check' -Body {
        Push-Location (Join-Path $RepoRoot 'src/VideoOrganizer.SvelteUI')
        try {
            # `npm run check` runs `svelte-kit sync` + `svelte-check`.
            # The sync is needed in fresh clones; on subsequent runs
            # it's a no-op.
            npm run check
        } finally {
            Pop-Location
        }
    }

    Invoke-Step -Name 'dotnet format --verify-no-changes' -Body {
        # `dotnet format` against each csproj. --verify-no-changes
        # exits non-zero if any file would be modified; that's the
        # signal we want for CI.
        #
        # Accumulate failures across iterations: Invoke-Step's wrapper
        # checks $LASTEXITCODE *once* after the body completes, so if
        # an early csproj fails and a later one succeeds, the global
        # $LASTEXITCODE could be 0 and the step would pass silently.
        # Tracking a local $anyFailed and re-raising at the end fixes
        # that — same shape as dotnet_format / dotnet_test in test.sh.
        $anyFailed = $false
        foreach ($csproj in Get-Projects) {
            Write-Tag -Tag 'format' -Message $csproj
            dotnet format $csproj --verify-no-changes --severity error
            if ($LASTEXITCODE -ne 0) {
                Write-Warn -Tag 'format' -Message "$csproj — FAILED"
                $anyFailed = $true
            }
        }
        if ($anyFailed) {
            # Setting $LASTEXITCODE here is what Invoke-Step picks up.
            # `exit 1` would abort the entire script; throwing would
            # also work but loses the per-file warn lines above.
            $global:LASTEXITCODE = 1
        }
    }

    Invoke-Step -Name 'ef migrations smoke (script generation)' -Body {
        # Generate the migrations as a single .sql file. Doesn't touch
        # the live database — purely a compile-time check that every
        # migration class is sound, the DbContext is consistent, and
        # the model snapshot is valid. Output is discarded.
        $out = New-TemporaryFile
        try {
            dotnet ef migrations script `
                --project src/VideoOrganizer.Infrastructure `
                --startup-project src/VideoOrganizer.API `
                --output $out.FullName `
                --idempotent | Out-Null
        } finally {
            Remove-Item $out -ErrorAction SilentlyContinue
        }
    }

    Write-Tag -Tag 'done' -Message 'all checks passed'
}
finally {
    Pop-Location
}

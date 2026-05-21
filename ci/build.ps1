# Full build — backend (Release config) + frontend static output.
# Slower than ci/check.ps1; meant for manual runs and ci/all.ps1.
#
# Backend: API and Import are the two entry-point projects; building
# them transitively builds Domain, Shared, and Infrastructure.
# Frontend: `npm ci` for reproducible installs + `npm run build` for
# the SvelteKit static output under src/VideoOrganizer.SvelteUI/build.

. "$PSScriptRoot/_lib.ps1"

Push-Location $RepoRoot
try {
    Invoke-Step -Name 'dotnet build (Release) — API + Import' -Body {
        # Build the two top-level entry-point projects. Each pulls in
        # its dependency csprojs (Domain, Shared, Infrastructure) so
        # nothing is left out.
        dotnet build src/VideoOrganizer.API/VideoOrganizer.API.csproj `
            --configuration Release --nologo
        dotnet build src/VideoOrganizer.Import/VideoOrganizer.Import.csproj `
            --configuration Release --nologo
    }

    Invoke-Step -Name 'npm ci (SvelteUI)' -Body {
        Push-Location (Join-Path $RepoRoot 'src/VideoOrganizer.SvelteUI')
        try {
            # `ci` not `install` — the former honors package-lock.json
            # strictly and refuses to update it. That's what we want
            # from a CI/build path: reproducible installs.
            npm ci
        } finally {
            Pop-Location
        }
    }

    Invoke-Step -Name 'npm run build (SvelteUI)' -Body {
        Push-Location (Join-Path $RepoRoot 'src/VideoOrganizer.SvelteUI')
        try {
            npm run build
        } finally {
            Pop-Location
        }
    }

    Write-Tag -Tag 'done' -Message 'build succeeded'
}
finally {
    Pop-Location
}

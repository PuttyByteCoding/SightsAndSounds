# Run all tests. Placeholder for now — no test projects exist.
#
# Discovers .NET test projects by scanning src/ for csprojs that
# reference Microsoft.NET.Test.Sdk; that's the canonical "this is a
# test project" marker. Frontend tests would run via `npm test` in
# SvelteUI; the script checks whether that script is defined and
# skips with a warning if not.
#
# Once test projects exist, this will run them. Today it's a no-op
# that exits 0 with a friendly note so the pipeline still wires up.

. "$PSScriptRoot/_lib.ps1"

Push-Location $RepoRoot
try {
    Invoke-Step -Name 'dotnet test (discover test projects)' -Body {
        # A csproj is a test project if it references Microsoft.NET.Test.Sdk.
        $testProjects = Get-Projects | Where-Object {
            (Get-Content $_ -Raw -ErrorAction SilentlyContinue) -match 'Microsoft\.NET\.Test\.Sdk'
        }
        if ($testProjects.Count -eq 0) {
            Write-Warn -Tag 'tests' -Message 'no .NET test projects yet — skipping'
        } else {
            # Same accumulation pattern as check.ps1's dotnet_format step:
            # Invoke-Step inspects $LASTEXITCODE *once*, so an early test
            # project failure followed by a passing one would slip through.
            $anyFailed = $false
            foreach ($csproj in $testProjects) {
                Write-Tag -Tag 'test' -Message $csproj
                dotnet test $csproj --configuration Release --nologo --no-restore
                if ($LASTEXITCODE -ne 0) {
                    Write-Warn -Tag 'test' -Message "$csproj — FAILED"
                    $anyFailed = $true
                }
            }
            if ($anyFailed) { $global:LASTEXITCODE = 1 }
        }
    }

    Invoke-Step -Name 'npm test (SvelteUI)' -Body {
        Push-Location (Join-Path $RepoRoot 'src/VideoOrganizer.SvelteUI')
        try {
            # package.json's `scripts` block is JSON — parse it and check
            # for a `test` key instead of running `npm test` which would
            # exit 1 if undefined.
            $pkg = Get-Content package.json -Raw | ConvertFrom-Json
            if (-not $pkg.scripts.PSObject.Properties.Name -contains 'test') {
                Write-Warn -Tag 'tests' -Message 'no npm test script defined — skipping'
            } else {
                npm test
            }
        } finally {
            Pop-Location
        }
    }

    Write-Tag -Tag 'done' -Message 'tests complete'
}
finally {
    Pop-Location
}

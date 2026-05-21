# Full local CI run — check + build + test. The "did anything break?"
# button. Use before push, before deploy, or just whenever you want
# to verify the tree is green.
#
# Bails on the first failing stage so the error is easy to find.

. "$PSScriptRoot/_lib.ps1"

$overallStart = Get-Date

Push-Location $RepoRoot
try {
    Invoke-Step -Name 'check' -Body { & "$PSScriptRoot/check.ps1" }
    Invoke-Step -Name 'build' -Body { & "$PSScriptRoot/build.ps1" }
    Invoke-Step -Name 'test'  -Body { & "$PSScriptRoot/test.ps1"  }

    $elapsed = (Get-Date) - $overallStart
    Write-Tag -Tag 'done' -Message ("CI passed ({0:F1}s total)" -f $elapsed.TotalSeconds)
}
finally {
    Pop-Location
}

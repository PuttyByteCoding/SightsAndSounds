# Shared helpers for ci/*.ps1. Mirrors the bash _lib.sh at the repo
# root so the PowerShell and bash sides of each task look the same
# from the outside. dot-source this at the top of every ci/*.ps1:
#
#   . "$PSScriptRoot/_lib.ps1"

# Fail fast: any uncaught cmdlet error aborts the script. Matches
# `set -e` in bash. Individual sites can locally relax via try/catch
# or -ErrorAction SilentlyContinue.
$ErrorActionPreference = 'Stop'

# Repo root = parent of ci/ — every script lives one level down.
$RepoRoot = (Resolve-Path "$PSScriptRoot/..").Path

# Tagged log lines, matching the bash _lib.sh formatting.
#   log compose "starting postgres"  →  [compose] starting postgres
# Cyan tag, no color on the message so logs stay readable when
# piped through `tee` or redirected to a file.
function Write-Tag([string]$Tag = 'info', [string]$Message = '') {
    Write-Host "[$Tag] " -NoNewline -ForegroundColor Cyan
    Write-Host $Message
}
function Write-Warn([string]$Tag = 'warn', [string]$Message = '') {
    Write-Host "[$Tag] " -NoNewline -ForegroundColor Yellow
    Write-Host $Message
}
function Write-Fail([string]$Tag = 'fail', [string]$Message = '') {
    Write-Host "[$Tag] " -NoNewline -ForegroundColor Red
    Write-Host $Message
}

# Run a named step. If $Body throws (PowerShell exception) OR sets
# $LASTEXITCODE to non-zero (native command failure), abort with a
# clear "step X failed" message. Used so `./ci/all.ps1` can report
# which stage tripped without burying the error in the middle of
# 1000 lines of build output.
function Invoke-Step {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Body
    )
    Write-Tag -Tag 'step' -Message $Name
    $stepStart = Get-Date
    & $Body
    if ($LASTEXITCODE -ne 0) {
        throw "Step '$Name' failed with exit code $LASTEXITCODE"
    }
    $elapsed = (Get-Date) - $stepStart
    Write-Tag -Tag 'done' -Message ("{0} ({1:F1}s)" -f $Name, $elapsed.TotalSeconds)
}

# Discover .csproj files under src/. Used by check (format) and
# build steps. Excludes the obj/ output tree just in case something
# leaks a stray project file in there.
function Get-Projects {
    Get-ChildItem -Path (Join-Path $RepoRoot 'src') -Recurse -Filter '*.csproj' `
        -ErrorAction Stop |
        Where-Object { $_.FullName -notmatch '\\obj\\' } |
        ForEach-Object { $_.FullName }
}

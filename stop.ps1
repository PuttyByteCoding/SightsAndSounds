# Stop UI, API, and postgres. Safe to run multiple times.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\_lib.ps1"

Set-Location $RepoRoot

Stop-Pidfile -Name ui
Stop-Pidfile -Name api

Log compose "stopping postgres container"
& docker compose stop postgres

Log done "all stopped"

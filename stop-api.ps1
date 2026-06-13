# Stop just the API. Frees the build-output DLL locks that otherwise
# break `dotnet build` / `dotnet format` / `dotnet ef` (and the
# pre-commit hook) while the API is running. Safe to run when the API
# isn't running -- Stop-Pidfile is a no-op then. Leaves postgres + the
# UI alone; use stop.ps1 to stop everything.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\_lib.ps1"

Set-Location $RepoRoot

Stop-Pidfile -Name api
Log done "api stopped"

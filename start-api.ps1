# Start just the API in the background. Idempotent -- Start-Bg leaves an
# already-running API alone. Assumes postgres is already up (stopping the
# API never stops the DB); run start.ps1 for a full cold start. Logs go
# to .run/api.log, pid to .run/api.pid.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\_lib.ps1"

Set-Location $RepoRoot

Import-DotEnv
Start-Bg -Name api -Command 'dotnet run --project src/VideoOrganizer.API'

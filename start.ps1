# Start postgres (Docker), API (dotnet), and SvelteKit dev server (npm).
# The DB is left intact -- use start-fresh.ps1 if you want to wipe data.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\_lib.ps1"

Set-Location $RepoRoot

# Load .env so the API/ImportTool inherit POSTGRES_* (they build the
# connection string from these). docker compose reads .env on its own.
Import-DotEnv

Log compose "starting postgres + seq"
& docker compose up -d postgres seq
Wait-Postgres

Start-Bg -Name api -Command 'dotnet run --project src/VideoOrganizer.API'
Start-Bg -Name ui  -Command 'npm run dev' -WorkingDirectory (Join-Path $RepoRoot 'src\VideoOrganizer.SvelteUI')

Log done "API:  http://localhost:5098  (Swagger at /swagger)"
Log done "UI:   http://localhost:5173  (Vite proxies /api to :5098)"
Log done "Seq:  http://localhost:5341  (structured logs)"
Log done "Logs: Get-Content $RunDir\api.log -Wait  |  Get-Content $RunDir\ui.log -Wait"
Log done "Stop: .\stop.ps1"

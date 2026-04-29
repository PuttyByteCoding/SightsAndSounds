# Wipe the postgres volume, then start postgres + API + UI.
# Useful after schema changes that aren't covered by a migration, or just to
# start with a clean slate. EF Core re-applies all migrations on first boot.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\_lib.ps1"

Set-Location $RepoRoot

# Load .env so the API/ImportTool inherit POSTGRES_* (they build the
# connection string from these). docker compose reads .env on its own.
Import-DotEnv

# Stop anything currently running so the DB volume can be removed cleanly.
Log reset "stopping any existing services"
try { & "$PSScriptRoot\stop.ps1" } catch { }

Log reset "tearing down postgres + volume"
& docker compose down -v

Log compose "starting fresh postgres + seq"
& docker compose up -d postgres seq
Wait-Postgres

Start-Bg -Name api -Command 'dotnet run --project src/VideoOrganizer.API'
Start-Bg -Name ui  -Command 'npm run dev' -WorkingDirectory (Join-Path $RepoRoot 'src\VideoOrganizer.SvelteUI')

Log done "Database wiped. Migrations will run automatically on first API request."
Log done "API:  http://localhost:5098  (Swagger at /swagger)"
Log done "UI:   http://localhost:5173"
Log done "Seq:  http://localhost:5341  (structured logs)"
Log done "Stop: .\stop.ps1"

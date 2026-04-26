# Sights and Sounds

A self-hosted media library manager. Plays local video files, tracks rich metadata, supports user-defined tag groups + custom properties, and imports new files in bulk.

**Stack:** .NET 10 · ASP.NET Core Minimal APIs · SvelteKit (Svelte 5, adapter-static) · EF Core · PostgreSQL.
PostgreSQL runs in Docker; the API and SvelteKit dev server run on the host directly.

---

## Quick start

```bash
cp .env.example .env       # then edit .env (set POSTGRES_PASSWORD at minimum)
./start.sh                 # postgres + API + UI
```

On Windows, equivalent PowerShell scripts ship alongside the bash ones:

```powershell
Copy-Item .env.example .env   # then edit .env (set POSTGRES_PASSWORD at minimum)
.\start.ps1                   # postgres + API + UI
```

UI:        <http://localhost:5173>
API:       <http://localhost:5098>
Swagger:   <http://localhost:5098/swagger>

Stop everything with `./stop.sh` (or `.\stop.ps1` on Windows).

---

## Environment variables

All runtime configuration that is **per-machine** or **secret** lives in a gitignored `.env` file at the repo root. The same file feeds both `docker-compose` (the postgres container) and the API process (`start.sh` does `set -a; source .env; set +a` so the dotnet process inherits them).

| Variable | Required | Default | Used by |
|---|---|---|---|
| `POSTGRES_DB` | yes | — | postgres container · API |
| `POSTGRES_USER` | yes | — | same |
| `POSTGRES_PASSWORD` | yes | — | same |
| `POSTGRES_HOST` | no | `localhost` | API |
| `POSTGRES_PORT` | no | `5432` | API |
| `VideoStorage__Root` | no | (empty) | API — bootstrap-only, seeds the "Default" VideoSet on first run |
| `VideoStorage__ThumbnailsDirectory` | no | (empty) | API — where scrub-thumbnail sprites are cached |

`VideoStorage__*` use double underscores so ASP.NET Core's configuration provider binds them to nested keys (`VideoStorage:Root`, etc.). They override the empty defaults in `appsettings.json`.

The `Required(...)` helper in `Program.cs` throws a clear startup error if `POSTGRES_DB`, `POSTGRES_USER`, or `POSTGRES_PASSWORD` is missing — you'll know immediately rather than seeing a confusing "connection refused" later.

### What's NOT in `.env`

- Tunable defaults (`BackgroundWorkers.*`, `Logging.LogLevel.*`) live in `appsettings.json` because they're shared across machines.
- Per-developer log-level tweaks go in `appsettings.Development.json` (gitignored).

---

## Tag-group seed

Tag groups (Performer, Year, Flags, …) and user-defined properties (Venue, Show Date, etc.) are seeded **once** on first startup, when the `tag_groups` table is empty. After that, you manage everything via the `/tags` page in the UI; the seed file is ignored.

| File | Tracked | Purpose |
|---|---|---|
| `src/VideoOrganizer.API/config/tags.seed.example.json` | yes | Reference shape — an example concert/bootleg library demonstrating every seed-loader feature |
| `src/VideoOrganizer.API/config/tags.seed.json` | **no** (gitignored) | Your personal seed. Copy from `.example.json` and customize. |

The loader (`TagSeedService`) supports:

- **Tag groups** — `name`, `allowMultiple`, `displayAsCheckboxes`, `sortOrder`, `notes`
- **Tags** — `name`, `aliases[]`, `isFavorite`, `sortOrder`, `notes`
- **Properties** — `name`, `dataType` (`text` / `longText` / `number` / `date` / `boolean` / `url`), `scope` (`Tag` or `Video`), `tagGroup` (when `Tag`-scoped), `required`, `sortOrder`, `notes`

To re-apply the seed, wipe the database with `./start-fresh.sh` — EF Core re-creates the schema, the seed service repopulates from `tags.seed.json`.

---

## Startup scripts

Three bash scripts at the repo root, plus a shared library. They work in Git Bash on Windows and on macOS/Linux. PowerShell equivalents (`start.ps1`, `start-fresh.ps1`, `stop.ps1`, `_lib.ps1`) sit next to them and behave the same way for native Windows shells. State (PIDs + logs) lives in `.run/` (gitignored).

### `start.sh`
Standard "everything up, keep my data":
1. Sources `.env` so dotnet inherits the postgres + storage vars
2. `docker compose up -d postgres` and waits for `pg_isready`
3. Backgrounds `dotnet run --project src/VideoOrganizer.API` → `.run/api.log`
4. Backgrounds `npm run dev` for the SvelteKit dev server → `.run/ui.log`

Re-running `start.sh` while services are alive is safe — pidfile checks skip starting any service that's already running.

### `start-fresh.sh`
Same as `start.sh`, plus a **destructive** first step:
1. Calls `stop.sh`
2. `docker compose down -v` — wipes the postgres volume
3. Brings everything back up; EF migrations run automatically on first request

Use this after schema changes that you don't want to migrate, or just to get back to a clean state.

### `stop.sh`
Tree-kills the API and UI processes (Windows: `taskkill /T`; Unix: `pkill -P` then `kill`), then `docker compose stop postgres`. Idempotent.

### `_lib.sh`
Sourced by the three scripts. Holds shared helpers: `is_windows`, `kill_tree`, `start_bg`, `stop_pidfile`, `wait_postgres`, plus colored `log` / `warn`. Not meant to be run directly — the leading `_` is the convention.

### Watching logs

```bash
tail -f .run/api.log     # API output
tail -f .run/ui.log      # SvelteKit dev server output
```

---

## Manual commands (without the scripts)

```bash
# Build everything
dotnet build src/SightsAndSounds.slnx

# Add an EF migration
dotnet ef migrations add <Name> --project src/VideoOrganizer.Infrastructure --startup-project src/VideoOrganizer.API

# Apply migrations manually (also runs automatically on API startup)
dotnet ef database update --project src/VideoOrganizer.Infrastructure --startup-project src/VideoOrganizer.API
```

---

## Project layout

| Project | Role |
|---|---|
| `VideoOrganizer.Domain` | Domain models — `Video`, `TagGroup`, `Tag`, `VideoTag`, `PropertyDefinition`, etc. |
| `VideoOrganizer.Infrastructure` | EF Core `DbContext`, migrations, entity configurations |
| `VideoOrganizer.Shared` | DTOs, `VideoStorageOptions`, shared enums |
| `VideoOrganizer.Import` | Library: `DirectoryImportService`, `FfprobeVideoMetadataService` (consumed by the API's import endpoint) |
| `VideoOrganizer.API` | ASP.NET Core Minimal API host (all endpoints in `ApiEndpoints.cs`); also serves the SvelteKit build from `wwwroot/` |
| `VideoOrganizer.SvelteUI` | SvelteKit (Svelte 5 · Tailwind 4 · daisyUI 5). Builds into `../VideoOrganizer.API/wwwroot/` on publish. |

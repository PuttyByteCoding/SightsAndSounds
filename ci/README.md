# Local CI / CD

Local-only pipeline for SightsAndSounds. Nothing in this directory
talks to a cloud runner — it all executes on your machine.

## Layout

```
ci/
├── _lib.ps1            # PowerShell helpers (log / step runner)
├── check.{ps1,sh}      # fast — svelte-check + dotnet format + ef smoke
├── build.{ps1,sh}      # dotnet build (Release) + npm run build
├── test.{ps1,sh}       # dotnet test + npm test (auto-discovers tests)
├── deploy.{ps1,sh}     # check + build + stop + migrate + start
├── all.{ps1,sh}        # check + build + test
├── install-hooks.{ps1,sh}
└── README.md
.githooks/
└── pre-commit          # auto-runs ci/check.sh on `git commit`
```

There is intentionally NO `.github/workflows/` directory. The
pipeline lives entirely on your machine — pushing to GitHub
doesn't trigger anything in the cloud. If you ever decide you
want a hosted runner as a safety net, add one then; the local
scripts won't change.

Every task ships a `.ps1` and a `.sh`. Pick whichever shell you're
in — both are feature-equivalent. The bash side reuses helpers from
`../_lib.sh` (the same file `start.sh` / `stop.sh` use).

## One-time setup

Activate the in-repo pre-commit hook. Only needs to run once per
clone:

```powershell
./ci/install-hooks.ps1
```

or

```bash
./ci/install-hooks.sh
```

This sets `git config core.hooksPath .githooks` so the tracked
`.githooks/pre-commit` runs on every commit.

## Common tasks

| Goal | Command |
|---|---|
| Run a quick sanity check (10–20s) | `./ci/check.ps1` / `./ci/check.sh` |
| Full local CI (check + build + test) | `./ci/all.ps1` / `./ci/all.sh` |
| Restart with the latest code + migrations | `./ci/deploy.ps1` / `./ci/deploy.sh` |
| Build only | `./ci/build.ps1` / `./ci/build.sh` |
| Skip the pre-commit hook for one commit | `git commit --no-verify` |

`deploy` refuses to run with uncommitted changes. Pass `-Force`
(PowerShell) or `--force` (bash) to override. Pass `-SkipBuild` /
`--skip-build` to bypass the check+build stages when you've just
finished a clean manual cycle.

## Stages explained

### `check` (the pre-commit hook)

1. **`npm run check`** — svelte-check across every `.svelte` and
   `.ts` file. Catches type errors, undeclared props, accidental
   `any`, broken imports, etc.
2. **`dotnet format --verify-no-changes`** — formatting + style
   on every `.csproj` under `src/`. Exits non-zero if any file
   would be reformatted.
3. **`dotnet ef migrations script`** — generates the full migration
   SQL to a temp file, then discards it. Doesn't touch the live
   database; it's a compile-time smoke that catches a broken
   `Migration.cs`, an inconsistent model snapshot, or a missing
   reference before it lands on disk.

### `build`

1. **`dotnet build` (Release)** of `VideoOrganizer.API` and
   `VideoOrganizer.Import`. Building these two cascades to
   `Domain`, `Shared`, and `Infrastructure` via project references.
2. **`npm ci`** in `src/VideoOrganizer.SvelteUI/` — reproducible
   install from `package-lock.json`.
3. **`npm run build`** — SvelteKit static output under
   `src/VideoOrganizer.SvelteUI/build/`.

### `test`

1. Scans `src/**/*.csproj` for any project that references
   `Microsoft.NET.Test.Sdk`. Runs `dotnet test` on each.
2. Runs `npm test` if `package.json` defines a `test` script.

There are no test projects today — both stages skip with a warning
and exit 0. Once you add a test project (or define `npm test`), the
script picks it up automatically; no edit needed here.

### `deploy`

1. Reject if working tree is dirty (override with `--force`).
2. Run `check` and `build` (skip with `--skip-build`).
3. `./stop.sh` — stops API, UI, and the postgres + seq containers.
4. `docker compose up -d postgres` + `wait_postgres` — make sure
   postgres is back up so the migration step can connect.
5. `dotnet ef database update` — apply any pending migrations.
6. `./start.sh` — restart API + UI + seq.

## Why no cloud CI?

This pipeline is deliberately local-only. `git push` to GitHub
won't trigger anything on hosted runners because there's no
`.github/workflows/` directory.

If you ever change your mind:

  - **Cloud runner from GitHub** — drop a `.github/workflows/ci.yml`
    that calls `bash ci/check.sh && bash ci/build.sh && bash ci/test.sh`.
    Same stages, same scripts, just executed by a GitHub-hosted
    ubuntu runner on every push. Free for public repos; 2000 min/mo
    free for private then pennies per minute.
  - **Local containerized run** — install [`act`](https://github.com/nektos/act)
    and point it at any workflow YAML. Runs in Docker on your
    machine. Lets you sanity-check that a fresh ubuntu container
    also passes without paying for / waiting on a cloud runner.

For now neither exists. The `ci/*.sh` scripts and the pre-commit
hook are the only automation in play.

## OpenAPI spec & frontend types (#125)

`ci/openapi.json` is the committed snapshot of the API's OpenAPI
document. The SvelteKit client's `src/lib/api.generated.ts` is
generated from it, so the spec is the single source of truth and
`types.ts` can stop drifting from the backend.

A drift guard (`OpenApiDriftTests`) fails the test run when the live
API no longer matches `ci/openapi.json`. When that happens, regenerate
both artifacts and commit them:

```bash
# 1. Re-dump the spec (hermetic — uses the Testcontainers API fixture)
SAS_DUMP_OPENAPI=1 dotnet test tests/VideoOrganizer.Tests \
    --filter FullyQualifiedName~OpenApiSpecDump

# 2. Regenerate the TypeScript types from it
cd src/VideoOrganizer.SvelteUI && npm run gen:types

# 3. Commit ci/openapi.json and src/lib/api.generated.ts
```

`gen:types` runs `openapi-typescript` via `npx` (pinned) rather than
as a dependency — it peer-requires TypeScript 5 and the app is on 6.

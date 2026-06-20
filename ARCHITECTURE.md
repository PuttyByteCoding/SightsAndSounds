# Architecture

A self-hosted video library manager. .NET 10 minimal-API + PostgreSQL on the
backend, SvelteKit (Svelte 5) on the frontend, with ffmpeg-driven background
processing. This document captures the non-obvious design decisions so they
survive refactors — for how to *run* the project, see `README.md` and `ci/`.

## Components

```
SvelteKit SPA (5173)  ──/api──▶  .NET API (5098)  ──▶  PostgreSQL (5432)
  (Vite dev proxy)                     │
                                       ├─ ffmpeg / ffprobe (metadata, sprites)
                                       └─ Seq (5341, dev-only structured logs)
```

- **`src/VideoOrganizer.API`** — HTTP endpoints (minimal APIs) + the background
  workers. Entry point `Program.cs`; routes in `ApiEndpoints*.cs`.
- **`src/VideoOrganizer.Domain`** — entities + enums (anemic; logic lives in the API).
- **`src/VideoOrganizer.Infrastructure`** — EF Core `DbContext`, entity
  configurations, migrations.
- **`src/VideoOrganizer.Shared`** — DTOs, helpers, config options (the API/UI contract).
- **`src/VideoOrganizer.Import`** — directory scan + ffprobe metadata extraction.
- **`src/VideoOrganizer.SvelteUI`** — the frontend.

Dependency direction: `Domain ← Infrastructure/Shared ← API (+ Import)`. No cycles.

## The path-as-prefix model (important)

A `VideoSet` is a *source root* with an absolute `Path`. A `Video` belongs to a
set by **`Video.FilePath` having `VideoSet.Path` as a prefix** — there is **no
foreign key**. This is why several operations look the way they do:

- "Which set owns this video / is this file allowed?" = a `StartsWith` prefix
  check against enabled sets (see the `/stream` guard).
- **Re-root** (moving a source, e.g. Windows→Linux) must rewrite the set `Path`
  *and* every child `Video.FilePath` together, or the children orphan. It does
  this atomically in one transaction with a single `ExecuteUpdate` that swaps the
  base prefix (`newBase + FilePath.Substring(oldBase.Length)`), preserving the
  relative tail. A read-only `/re-root/preview` lets the user confirm the mapping
  first.
- Disabling a set hides its videos from browse but keeps them in the DB.

### Filtering happens in SQL

The browse filter, playlist generators, and related-videos endpoints translate
the three-way tag filter (Required = AND, Optional = OR, Excluded = NOT, plus
hidden-by-default suppression) into an EF predicate via `VideoFilterTranslator`
so the database does the work — they do **not** load every video under the
enabled roots into memory. The one filter term that can't be expressed in SQL,
**Folder** (case-insensitive directory equality), narrows the set with
everything else first and is then refined by the exact in-memory matcher over
that much smaller slice. Related-videos likewise ranks (shared-tag count, then
`IngestDate`) and applies its limit in SQL.

## Background workers

Three `BackgroundService`s, all **signal-driven** rather than timer-polled — they
sleep until woken, which avoids idle CPU and lets work start the instant it's
queued:

- **`ThumbnailWarmingService`** — pre-generates the scrub-preview sprite (`sprite.jpg`)
  + WebVTT for each video. Woken by `ThumbnailWarmingSignal` (finished import,
  "Scan now", "Retry failed", re-root, restore). Selects work by checking the
  **cache on disk** (`IsAlreadyWarmed`), not a DB flag — so missing sprites
  self-heal. Skips videos flagged `ThumbnailsFailed` (manual "Retry failed"
  clears it). Per-video timeout + kill-on-cancel bound each item.
- **`Md5BackfillService`** — hashes videos imported without an MD5, flags
  cross-video duplicates for review. Same signal/wake shape.
- **`ImportQueueService`** — a single-consumer `Channel` that serializes the
  "add to database" phase across import requests, so Import 1's videos finish
  saving before Import 2's start (predictable ordering for the downstream workers,
  which order by `IngestDate`).

A worker that finds its source files unreachable (wrong paths after a machine
move) skips them and sleeps; re-rooting then fires the signal so it retries.

### Sprite generation (ffmpeg `tile`)

`ThumbnailGenerator` builds the sprite in two fast ffmpeg passes — extract ~15
frames via **input seeks** (`-ss` before `-i`, ~constant cost regardless of
length), then `tile` the small JPEGs into one sheet. It deliberately does **not**
use `fps=1/interval` (that decodes the whole file and is multiples slower on long
4K videos) and deliberately does **not** depend on an image library (ImageSharp
v4 requires a paid license).

## Destructive operations are reversible / guarded

- **Backup → restore**: a JSON snapshot of every table. Restore first takes a
  **pre-restore safety snapshot**, then replaces all tables in one transaction;
  on failure the data is left untouched and the safety snapshot is reported. A
  restored snapshot carries the *other* machine's paths, so the user re-roots
  afterward (and restore signals the warmer to regenerate sprites).
- **File move**: moves the file and writes a `FileMoveLog`; **undo** (`/file-moves/{id}/revert`)
  puts it back and marks the log reverted.
- **Purge / delete**: only act on videos explicitly flagged (`MarkedForDeletion`
  or `PlaybackIssue`); deleting a source that would orphan videos requires
  `?force=true`.

## Security posture

Local-first tool. Path-traversal is the main defense: file-serving endpoints
resolve the full path and **403 unless it's under an enabled `VideoSet`**;
OS-level endpoints (open terminal / reveal / ffprobe-diagnostics) are gated to
**loopback requests only**. There is currently **no authentication** (see issue
#124) — anyone who can reach the port has full access.

## Testing

- **Unit** (`src/VideoOrganizer.UnitTests`, `tests/VideoOrganizer.Tests`): pure
  helpers (path/SQL/codec/dimension logic, enum converters, progress trackers).
- **Integration** (`tests/VideoOrganizer.Tests/Integration`): boot the real app
  with `WebApplicationFactory` against a throwaway **Postgres container**
  (Testcontainers); cover the risky paths — re-root, backup round-trip, tag
  merge, filter, move/undo, purge, the stream 403 guard, tag-group cascade,
  end-to-end import.
- **ffmpeg fixtures** are synthetic (generated `testsrc` clips) — real/private
  media never touches CI. A shared `TestFfmpeg` points Xabe at the system
  binaries so nothing is downloaded.
- CI (`.github/workflows/ci.yml`) runs the same `ci/all.sh` (check + build +
  test) on every PR to `dev`.

## Conventions worth knowing

- Endpoints return `{ error: "..." }` for 4xx; unhandled exceptions return a
  consistent `{ error }` 500 in production (developer page in dev).
- `ApiEndpoints` is a `partial class` split one file per domain
  (`ApiEndpoints.<Domain>.cs`); shared helpers + the ungrouped "videos core"
  stay in `ApiEndpoints.cs`.
- Enums serialize camelCase via `LenientEnumConverter` (case-insensitive,
  numeric-tolerant on read).
- The frontend's `src/lib/api.generated.ts` is generated from the API's OpenAPI
  document (`ci/openapi.json`) — that spec is the source of truth. A drift guard
  (`OpenApiDriftTests`) fails the build when an endpoint/DTO changes without
  regenerating; the regen workflow is in `ci/README.md`.
- `.env` (gitignored) is the single source of truth for `POSTGRES_*`; the API
  fails fast if they're missing.

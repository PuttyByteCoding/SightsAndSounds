# Import-tool browsing performance (research — issue #197)

Browsing folders in the Import tool is slow. This documents *why*, and lays out
options to make it faster, with a recommendation. No behavior is changed yet —
this is the research deliverable for #197.

## Where the time goes

`GET /api/import/browse` (`ApiEndpoints.Import.cs`) returns, for the requested
folder, its immediate child directories — each annotated with a recursive
**VideoCount** badge. Producing that badge is the slow part:

- For **every child folder** shown, `CachedVideoCount → CountVideoFilesRecursive`
  runs `Directory.EnumerateFiles(child, "*", SearchOption.AllDirectories)` over
  the child's **entire subtree** and counts video files
  (`ApiEndpoints.cs:247`). So one browse of a folder with *N* children walks the
  full file tree under all *N* — potentially the whole library.
- `DirectoryScanCache` memoizes these counts per folder, but it is **in-memory
  only** and is bypassed/invalidated in the common cases:
  - **cold start / server restart** → every count re-walked on first browse;
  - **`?refresh=true`** (the Sources "refresh" button) → `Clear()`s the whole
    cache, then re-walks;
  - **a folder never browsed before** → cold for that subtree.
- **Network mounts / spinning disks** amplify this dramatically — a recursive
  enumeration of thousands of files over SMB/NFS is seconds-to-minutes.

Secondary (smaller) costs per browse:

- `hasSubdirectories` is computed as `Directory.GetDirectories(d).Length > 0`
  for each child — allocates the full array when a single entry would do.
- `ImportedCount`: one DB query for the parent prefix, then in-memory
  `StartsWith` counting per child. Cheap; not a concern.

**Bottom line:** the folder *structure* (names + `hasSubs`) is cheap to produce;
the recursive *video counts* are what make browsing slow, and they're recomputed
by walking the disk far more often than the counts actually change.

## Options

### 1. Decouple structure from counts (lazy counts) — best perceived-latency win
Return the folder list immediately (names + `hasSubs`, no `VideoCount`), then
fill the count badges in afterward:
- a separate `GET /api/import/folder-count?path=…` the client calls per visible
  folder once the tree renders, **or**
- stream counts over the existing scan-progress channel the page already polls.

The tree appears instantly; badges populate progressively. Doesn't reduce total
work, but moves it off the critical path so browsing *feels* fast.
Effort: moderate (new endpoint + frontend wiring). Risk: low.

### 2. Persist the count index in the DB, maintained incrementally — best real fix
Store per-folder recursive video counts in a table and update them from the
events that actually change them (import, file move/undo, delete/purge). Browse
then reads counts from the DB — fast, and survives restart — with **no disk walk
on the hot path**. `?refresh=true` kicks a background reconcile walk.
Effort: higher (schema + maintenance hooks + reconcile job). Risk: medium
(must keep the index correct). Pairs naturally with #1.

### 3. Count direct children only (change the badge's meaning)
Make the badge "videos directly in this folder" (one non-recursive
`EnumerateFiles`) instead of a recursive total. Cheap, but changes what the
number means; users may rely on the subtree total. Could show "N here" with the
recursive total loaded lazily (#1).
Effort: low. Risk: UX/semantic change.

### 4. One subtree walk, bucketed by child (instead of N per-child walks)
Replace the *N* per-child recursive enumerations with a single
`EnumerateFiles(parent, AllDirectories)` grouped by top-level child. Same total
IO, fewer enumeration setups and syscalls. Modest win, no semantic change.
Effort: low. Risk: low.

### 5. Warm the cache in the background
On startup / source-add, kick a background walk to populate `DirectoryScanCache`
so interactive browses hit a warm cache. Doesn't speed the first walk but moves
it off the user's click. Pairs with #2 (or is subsumed by it).
Effort: low–moderate. Risk: low.

### 6. Cheap wins regardless
- `hasSubs`: `Directory.EnumerateDirectories(d).Any()` (early-exit) instead of
  `.GetDirectories(d).Length > 0` (full allocation).
- Use `EnumerationOptions { IgnoreInaccessible = true }` to avoid exceptions on
  permission-denied entries mid-walk.
Effort: tiny. Risk: none.

### 7. Parallelize per-child walks
`Task.WhenAll` the per-child counts. Helps on SSD/multi-spindle; can *hurt* a
single network mount (contention). Only worth it behind a measured flag.

## Recommendation

Two-phase:

1. **Now (cheap, high impact):** #1 (lazy counts) + #6 (cheap wins). The tree
   renders instantly and the slow part stops blocking it. Small, low-risk.
2. **Then (the durable fix):** #2 (persisted, incrementally-maintained count
   index) so counts come from the DB and the disk is only walked on an explicit
   background reconcile. Removes the disk walk from the hot path entirely.

#3/#4 are optional refinements; #7 only behind a benchmark.

## Status

**Option 1 (lazy counts) is implemented in this PR.** `/import/browse` now returns
the folder tree immediately with `VideoCount = null`; the client fetches each
folder's recursive count from the new `GET /import/folder-count` endpoint and
fills the badge in afterward (both the Import tool tree and the browse-page
Folders tree, the latter via per-node self-fetch in `FolderTreeNode`). The
scan-progress "Discovered N…" counter (#27) is driven by the folder-count calls,
and the per-folder scan cache (#4) / `?refresh=true` still apply.

Option 2 (persisted incremental count index) remains the recommended durable
follow-up and can land independently. #6's cheap wins are still on the table.

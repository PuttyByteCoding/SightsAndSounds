<script lang="ts">
  // Data Validation page. Four diagnostic tools surface "drift"
  // between what's in the DB and what's actually on disk:
  //
  //   1. Sources reachable — VideoSet.Path probed via TryDirectoryExists
  //      (already returned by /video-sets), shown with green / red badges.
  //   2. Files missing on disk — Video rows whose FilePath no longer
  //      resolves. Hidden for disabled sources by default.
  //   3. Files on disk not in DB — un-imported leftovers under each
  //      configured source.
  //   4. MD5 re-check — recomputes each video's hash and flags
  //      mismatches (corruption, truncation, unrelated edits since
  //      import). Iterates client-side so the user sees real-time
  //      progress and can pause / stop without losing partial work.
  //
  // All tools are lazy: they stay collapsed/empty until the user
  // hits "Run check" so a slow filesystem walk doesn't block the
  // initial render. Each section caches its own results so re-running
  // one doesn't blow away the others.
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import type {
    MissingVideoFile, ExtraDiskFile, VideoSet,
    Md5Candidate, Md5CheckResult
  } from '$lib/types';
  import {
    loadColumnWidths,
    saveColumnWidths,
    resizable,
  } from '$lib/tableUtils.svelte';

  // Column widths for the five diagnostic tables on this page. One
  // map per table, persisted under distinct keys so users can shape
  // each independently. Defaults mirror the prior inline `w-24` /
  // `w-32` Tailwind classes for fixed columns, plus reasonable
  // estimates for the previously content-sized text columns.
  const TABLE_DEFAULTS: Record<string, Record<string, number>> = {
    sources:    { name: 200, path: 480, enabled: 96, reachable: 128 },
    missing:    { source: 160, file: 480, size: 96 },
    extras:     { source: 160, file: 480, size: 96 },
    md5Match:   { source: 160, file: 480, size: 96, stored: 280, computed: 280 },
    md5Errors:  { source: 160, file: 480, error: 400 },
  };
  // Top-level reassignment pattern (see history/+page.svelte) — each
  // setW call replaces the per-table map AND the outer object so
  // Svelte's proxy registers the write at the column granularity
  // that drives the colgroup re-render.
  let tableW = $state<Record<string, Record<string, number>>>(
    Object.fromEntries(
      Object.entries(TABLE_DEFAULTS).map(([k, d]) =>
        [k, loadColumnWidths(`data-validation.${k}`, d)]
      )
    )
  );
  function setW(table: string, col: string, w: number) {
    const next = { ...(tableW[table] ?? {}), [col]: w };
    tableW = { ...tableW, [table]: next };
    saveColumnWidths(`data-validation.${table}`, next);
  }
  function getW(table: string, col: string, fallback: number): number {
    return tableW[table]?.[col] ?? fallback;
  }
  // Per-table explicit pixel widths (sum of declared columns). See
  // DataTableModal for why `width: max-content` doesn't honor the
  // colgroup — that fix lives here too for every diagnostic table.
  const totalW: Record<string, number> = $derived(
    Object.fromEntries(
      Object.entries(TABLE_DEFAULTS).map(([table, defs]) => [
        table,
        Object.entries(defs).reduce((s, [col, def]) => s + getW(table, col, def), 0),
      ])
    )
  );

  // --- Sources ----------------------------------------------------------
  let sources = $state<VideoSet[]>([]);
  let sourcesLoading = $state(false);
  let sourcesError = $state<string | null>(null);

  async function loadSources() {
    sourcesLoading = true;
    sourcesError = null;
    try {
      sources = await api.listVideoSets();
    } catch (e) {
      sourcesError = e instanceof Error ? e.message : String(e);
    } finally {
      sourcesLoading = false;
    }
  }
  onMount(loadSources);

  // --- Missing files ----------------------------------------------------
  let missing = $state<MissingVideoFile[] | null>(null);
  let missingLoading = $state(false);
  let missingError = $state<string | null>(null);
  // When false, files in disabled sources are excluded by the API.
  // The user can flip this to also see orphans under disabled sources
  // — useful when tracking down a missing file that happened to be
  // under a source they later disabled.
  let missingIncludeDisabled = $state(false);

  async function runMissing() {
    missingLoading = true;
    missingError = null;
    try {
      missing = await api.getMissingFiles(missingIncludeDisabled);
    } catch (e) {
      missingError = e instanceof Error ? e.message : String(e);
    } finally {
      missingLoading = false;
    }
  }

  // --- Extra files (on disk, not in DB) --------------------------------
  let extras = $state<ExtraDiskFile[] | null>(null);
  let extrasLoading = $state(false);
  let extrasError = $state<string | null>(null);
  // Optional source-scope. Empty = scan every source the user is
  // currently allowed to see (enabled by default; disabled too when
  // extrasIncludeDisabled is on).
  let extrasSourceId = $state<string>('');
  let extrasIncludeDisabled = $state(false);

  async function runExtras() {
    extrasLoading = true;
    extrasError = null;
    try {
      extras = await api.getExtraFiles(
        extrasSourceId || undefined,
        extrasIncludeDisabled
      );
    } catch (e) {
      extrasError = e instanceof Error ? e.message : String(e);
    } finally {
      extrasLoading = false;
    }
  }

  // --- MD5 re-check -----------------------------------------------------
  // Two-phase: fetch the candidate list once, then walk it firing
  // one POST per video. Per-file results stream in so the user
  // sees mismatches the moment they land instead of waiting for
  // the whole library to finish. Pause / Stop are polled between
  // iterations — the in-flight hash itself isn't interrupted, but
  // pausing is fine for a single 1-2GB file (worst case 30s).
  let md5IncludeDisabled = $state(false);
  let md5Candidates = $state<Md5Candidate[] | null>(null);
  let md5Done = $state(0);
  let md5Mismatches = $state<Md5CheckResult[]>([]);
  let md5Errors = $state<{ candidate: Md5Candidate; error: string }[]>([]);
  let md5Running = $state(false);
  let md5Paused = $state(false);
  let md5StopRequested = $state(false);
  let md5Error = $state<string | null>(null);
  // Currently-hashing file label for the progress UI — drops out
  // when the loop finishes so the modal reads as "done".
  let md5Current = $state<string | null>(null);

  async function md5WaitWhilePaused() {
    while (md5Paused && !md5StopRequested) {
      await new Promise(r => setTimeout(r, 100));
    }
  }

  async function runMd5Check() {
    if (md5Running) return;
    md5Running = true;
    md5Paused = false;
    md5StopRequested = false;
    md5Error = null;
    md5Done = 0;
    md5Mismatches = [];
    md5Errors = [];
    md5Current = null;
    try {
      md5Candidates = await api.getMd5Candidates(md5IncludeDisabled);
      const list = md5Candidates;
      for (const c of list) {
        await md5WaitWhilePaused();
        if (md5StopRequested) break;
        md5Current = c.fileName;
        try {
          const result = await api.validateMd5(c.videoId);
          if (result.error) {
            md5Errors = [...md5Errors, { candidate: c, error: result.error }];
          } else if (!result.match) {
            md5Mismatches = [...md5Mismatches, result];
          }
        } catch (e) {
          md5Errors = [...md5Errors, {
            candidate: c,
            error: e instanceof Error ? e.message : String(e)
          }];
        }
        md5Done++;
      }
    } catch (e) {
      md5Error = e instanceof Error ? e.message : String(e);
    } finally {
      md5Current = null;
      md5Running = false;
      md5Paused = false;
    }
  }

  function md5Pause() { md5Paused = true; }
  function md5Resume() { md5Paused = false; }
  function md5Stop() { md5StopRequested = true; md5Paused = false; }

  // Lookup back from a mismatch result to the candidate row so the
  // mismatch table can show source / filename / size without us
  // having to copy that data into Md5CheckResult.
  function md5CandidateFor(videoId: string): Md5Candidate | undefined {
    return md5Candidates?.find(c => c.videoId === videoId);
  }

  // --- Helpers ----------------------------------------------------------
  function formatBytes(bytes: number): string {
    if (!bytes || bytes <= 0) return '';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0;
    let n = bytes;
    while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
    return `${n.toFixed(n >= 10 || i === 0 ? 0 : 1)} ${units[i]}`;
  }

  // Aggregate sizes for the Missing / Extras tables — useful when
  // the user is deciding whether the drift is worth acting on.
  const missingTotalBytes = $derived(
    (missing ?? []).reduce((sum, m) => sum + (m.fileSize || 0), 0)
  );
  const extrasTotalBytes = $derived(
    (extras ?? []).reduce((sum, x) => sum + (x.fileSize || 0), 0)
  );
</script>

<svelte:head><title>Data Validation - Video Organizer</title></svelte:head>

<div class="max-w-6xl mx-auto space-y-8">
  <header>
    <h1 class="text-2xl font-semibold">Data Validation</h1>
    <p class="text-sm text-base-content/70 mt-1">
      Spot drift between the database and the filesystem. Each tool
      runs on demand — large libraries on slow disks may take several
      seconds for the missing / extras scans.
    </p>
  </header>

  <!-- ======================== Sources ======================== -->
  <section class="card bg-base-200 p-4 space-y-3">
    <div class="flex items-center justify-between gap-4">
      <div>
        <h2 class="text-lg font-semibold">Sources reachable</h2>
        <p class="text-sm text-base-content/60">
          Each configured source's path is probed once on load. A red
          "missing" badge means the API can't see the directory at
          all — usually a Docker mount problem or a typo.
        </p>
      </div>
      <button
        type="button"
        class="btn btn-sm btn-cancel"
        onclick={loadSources}
        disabled={sourcesLoading}
      >
        {#if sourcesLoading}<span class="loading loading-spinner loading-xs"></span>{/if}
        Re-check
      </button>
    </div>

    {#if sourcesError}
      <div class="alert alert-error text-sm">{sourcesError}</div>
    {/if}

    {#if sources.length === 0 && !sourcesLoading}
      <div class="text-base-content/60 italic">No sources configured. Add one on the Configuration page.</div>
    {:else if sources.length > 0}
      <div class="overflow-x-auto">
        <table class="table table-sm resizable-table" style="table-layout: fixed; width: {totalW.sources}px;">
          <colgroup>
            <col style="width: {getW('sources', 'name', 200)}px" />
            <col style="width: {getW('sources', 'path', 480)}px" />
            <col style="width: {getW('sources', 'enabled', 96)}px" />
            <col style="width: {getW('sources', 'reachable', 128)}px" />
          </colgroup>
        <thead>
          <tr>
            {#each [
              { key: 'name', label: 'Name', align: 'left', def: 200 },
              { key: 'path', label: 'Path', align: 'left', def: 480 },
              { key: 'enabled', label: 'Enabled', align: 'center', def: 96 },
              { key: 'reachable', label: 'Reachable', align: 'center', def: 128 },
            ] as col (col.key)}
              <th
                class="relative select-none p-0 {col.align === 'center' ? 'text-center' : 'text-left'}"
                style="width: {getW('sources', col.key, col.def)}px;"
              >
                <span class="block px-3 py-2 truncate">{col.label}</span>
                <button
                  type="button"
                  aria-label={`Resize ${col.label} (double-click to auto-fit)`}
                  class="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/40 active:bg-primary/60 z-10"
                  use:resizable={{
                    getWidth: () => getW('sources', col.key, 100),
                    setWidth: (w) => setW('sources', col.key, w),
                  }}
                ></button>
              </th>
            {/each}
          </tr>
        </thead>
        <tbody>
          {#each sources as s (s.id)}
            <tr>
              <td class="font-medium">
                {#if s.enabled}
                  {s.name}
                {:else}
                  <span class="line-through text-base-content/60">{s.name}</span>
                  <span class="text-xs italic text-base-content/50 ml-1">(Disabled)</span>
                {/if}
              </td>
              <td class="font-mono text-xs break-all">{s.path}</td>
              <td class="text-center">
                {#if s.enabled}
                  <span class="badge badge-success badge-sm">Enabled</span>
                {:else}
                  <span class="badge badge-ghost badge-sm">Disabled</span>
                {/if}
              </td>
              <td class="text-center">
                {#if s.pathExists === undefined}
                  <span class="badge badge-ghost badge-sm">unknown</span>
                {:else if s.pathExists}
                  <span class="badge badge-success badge-sm">reachable</span>
                {:else}
                  <span class="badge badge-error badge-sm" title="Path not found in container">missing</span>
                {/if}
              </td>
            </tr>
          {/each}
        </tbody>
        </table>
      </div>
    {/if}
  </section>

  <!-- ================== Files missing on disk ================ -->
  <section class="card bg-base-200 p-4 space-y-3">
    <div class="flex items-center justify-between gap-4">
      <div>
        <h2 class="text-lg font-semibold">Files in DB but missing on disk</h2>
        <p class="text-sm text-base-content/60">
          For every Video row, the API probes
          <code class="text-xs">File.Exists(FilePath)</code>. Rows with
          a missing file are listed below — likely candidates for
          purging or restoring from a backup. Clips share their
          parent's file so they're skipped.
        </p>
      </div>
      <div class="flex items-center gap-2 shrink-0">
        <label class="cursor-pointer label gap-2 py-0">
          <input
            type="checkbox"
            class="checkbox checkbox-sm"
            bind:checked={missingIncludeDisabled}
            disabled={missingLoading}
          />
          <span class="label-text text-sm">Include disabled sources</span>
        </label>
        <button
          type="button"
          class="btn btn-sm btn-soft btn-primary btn-cta"
          onclick={runMissing}
          disabled={missingLoading}
        >
          {#if missingLoading}<span class="loading loading-spinner loading-xs"></span>{/if}
          Run check
        </button>
      </div>
    </div>

    {#if missingError}
      <div class="alert alert-error text-sm">{missingError}</div>
    {/if}

    {#if missing !== null}
      {#if missing.length === 0}
        <div class="alert alert-success text-sm">
          Every video file is reachable on disk
          {missingIncludeDisabled ? '(including disabled sources).' : '(across enabled sources).'}
        </div>
      {:else}
        <div class="text-xs text-base-content/60 tabular-nums">
          {missing.length} missing · {formatBytes(missingTotalBytes)} total
        </div>
        <div class="overflow-x-auto">
          <table class="table table-sm resizable-table" style="table-layout: fixed; width: {totalW.missing}px;">
            <colgroup>
              <col style="width: {getW('missing', 'source', 160)}px" />
              <col style="width: {getW('missing', 'file', 480)}px" />
              <col style="width: {getW('missing', 'size', 96)}px" />
            </colgroup>
            <thead>
              <tr>
                {#each [
                  { key: 'source', label: 'Source', align: 'left', def: 160 },
                  { key: 'file', label: 'File', align: 'left', def: 480 },
                  { key: 'size', label: 'Size', align: 'right', def: 96 },
                ] as col (col.key)}
                  <th
                    class="relative select-none p-0 {col.align === 'right' ? 'text-right' : 'text-left'}"
                    style="width: {getW('missing', col.key, col.def)}px;"
                  >
                    <span class="block px-3 py-2 truncate">{col.label}</span>
                    <button
                      type="button"
                      aria-label={`Resize ${col.label} (double-click to auto-fit)`}
                      class="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/40 active:bg-primary/60 z-10"
                      use:resizable={{
                        getWidth: () => getW('missing', col.key, 100),
                        setWidth: (w) => setW('missing', col.key, w),
                      }}
                    ></button>
                  </th>
                {/each}
              </tr>
            </thead>
            <tbody>
              {#each missing as m (m.videoId)}
                <tr>
                  <td class="text-sm whitespace-nowrap">
                    {#if m.sourceName}
                      {#if m.sourceEnabled}
                        {m.sourceName}
                      {:else}
                        <span class="line-through text-base-content/60">{m.sourceName}</span>
                        <span class="text-xs italic text-base-content/50 ml-1">(Disabled)</span>
                      {/if}
                    {:else}
                      <span class="text-base-content/40 italic">no source</span>
                    {/if}
                  </td>
                  <td>
                    <div class="font-medium break-all">{m.fileName}</div>
                    <div class="text-xs text-base-content/60 break-all">{m.filePath}</div>
                  </td>
                  <td class="text-right tabular-nums text-sm">{formatBytes(m.fileSize)}</td>
                </tr>
              {/each}
            </tbody>
          </table>
        </div>
      {/if}
    {:else if !missingLoading}
      <div class="text-sm text-base-content/50 italic">
        Click "Run check" to scan the database for missing files.
      </div>
    {/if}
  </section>

  <!-- ================ Files on disk, not in DB =============== -->
  <section class="card bg-base-200 p-4 space-y-3">
    <div class="flex items-center justify-between gap-4">
      <div>
        <h2 class="text-lg font-semibold">Files on disk but not in DB</h2>
        <p class="text-sm text-base-content/60">
          Walks each source's directory tree and lists video files
          (.mp4 / .m4v) that don't have a matching row. These are
          un-imported leftovers — re-run the import tool to pick
          them up. Special-folder staging areas (_ToDelete,
          _PlaybackIssue) are skipped.
        </p>
      </div>
      <div class="flex items-center gap-2 shrink-0">
        <select
          class="select select-bordered select-sm"
          bind:value={extrasSourceId}
          disabled={extrasLoading}
        >
          <option value="">All sources</option>
          {#each sources as s (s.id)}
            <option value={s.id}>{s.name}{!s.enabled ? ' (disabled)' : ''}</option>
          {/each}
        </select>
        {#if !extrasSourceId}
          <label class="cursor-pointer label gap-2 py-0">
            <input
              type="checkbox"
              class="checkbox checkbox-sm"
              bind:checked={extrasIncludeDisabled}
              disabled={extrasLoading}
            />
            <span class="label-text text-sm">Include disabled</span>
          </label>
        {/if}
        <button
          type="button"
          class="btn btn-sm btn-soft btn-primary btn-cta"
          onclick={runExtras}
          disabled={extrasLoading}
        >
          {#if extrasLoading}<span class="loading loading-spinner loading-xs"></span>{/if}
          Run check
        </button>
      </div>
    </div>

    {#if extrasError}
      <div class="alert alert-error text-sm">{extrasError}</div>
    {/if}

    {#if extras !== null}
      {#if extras.length === 0}
        <div class="alert alert-success text-sm">
          No un-imported video files found
          {extrasSourceId ? 'in the selected source.' : 'in the scanned sources.'}
        </div>
      {:else}
        <div class="text-xs text-base-content/60 tabular-nums">
          {extras.length} extra file{extras.length === 1 ? '' : 's'} · {formatBytes(extrasTotalBytes)} total
        </div>
        <div class="overflow-x-auto">
          <table class="table table-sm resizable-table" style="table-layout: fixed; width: {totalW.extras}px;">
            <colgroup>
              <col style="width: {getW('extras', 'source', 160)}px" />
              <col style="width: {getW('extras', 'file', 480)}px" />
              <col style="width: {getW('extras', 'size', 96)}px" />
            </colgroup>
            <thead>
              <tr>
                {#each [
                  { key: 'source', label: 'Source', align: 'left', def: 160 },
                  { key: 'file', label: 'File', align: 'left', def: 480 },
                  { key: 'size', label: 'Size', align: 'right', def: 96 },
                ] as col (col.key)}
                  <th
                    class="relative select-none p-0 {col.align === 'right' ? 'text-right' : 'text-left'}"
                    style="width: {getW('extras', col.key, col.def)}px;"
                  >
                    <span class="block px-3 py-2 truncate">{col.label}</span>
                    <button
                      type="button"
                      aria-label={`Resize ${col.label} (double-click to auto-fit)`}
                      class="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/40 active:bg-primary/60 z-10"
                      use:resizable={{
                        getWidth: () => getW('extras', col.key, 100),
                        setWidth: (w) => setW('extras', col.key, w),
                      }}
                    ></button>
                  </th>
                {/each}
              </tr>
            </thead>
            <tbody>
              {#each extras as x (x.filePath)}
                {@const xSet = sources.find(s => s.id === x.sourceId)}
                <tr>
                  <td class="text-sm whitespace-nowrap">
                    {#if xSet && !xSet.enabled}
                      <span class="line-through text-base-content/60">{x.sourceName}</span>
                      <span class="text-xs italic text-base-content/50 ml-1">(Disabled)</span>
                    {:else}
                      {x.sourceName}
                    {/if}
                  </td>
                  <td>
                    <div class="font-medium break-all">{x.fileName}</div>
                    <div class="text-xs text-base-content/60 break-all">{x.filePath}</div>
                  </td>
                  <td class="text-right tabular-nums text-sm">{formatBytes(x.fileSize)}</td>
                </tr>
              {/each}
            </tbody>
          </table>
        </div>
      {/if}
    {:else if !extrasLoading}
      <div class="text-sm text-base-content/50 italic">
        Pick a source (or "All sources") and click "Run check" to scan disk for un-imported files.
      </div>
    {/if}
  </section>

  <!-- ===================== MD5 re-check ===================== -->
  <section class="card bg-base-200 p-4 space-y-3">
    <div class="flex items-center justify-between gap-4">
      <div>
        <h2 class="text-lg font-semibold">Re-validate videos via MD5</h2>
        <p class="text-sm text-base-content/60">
          Recomputes the MD5 of every video file with a stored hash
          and flags mismatches — catches corruption, truncation, and
          edits that happened after the initial import. Each file is
          streamed off disk one chunk at a time, so it's safe on
          multi-GB recordings, but expect roughly the same wall-clock
          time as a full read of the library. Pause and Stop work
          between files; the in-flight hash itself isn't interrupted.
        </p>
      </div>
      <div class="flex items-center gap-2 shrink-0">
        <label class="cursor-pointer label gap-2 py-0">
          <input
            type="checkbox"
            class="checkbox checkbox-sm"
            bind:checked={md5IncludeDisabled}
            disabled={md5Running}
          />
          <span class="label-text text-sm">Include disabled sources</span>
        </label>
        {#if !md5Running}
          <button
            type="button"
            class="btn btn-sm btn-soft btn-primary btn-cta"
            onclick={runMd5Check}
          >Run check</button>
        {:else if md5Paused}
          <button
            type="button"
            class="btn btn-sm btn-soft btn-primary"
            onclick={md5Resume}
          >Resume</button>
          <button
            type="button"
            class="btn btn-sm btn-soft btn-error border border-error/50"
            onclick={md5Stop}
            disabled={md5StopRequested}
          >Stop</button>
        {:else}
          <button
            type="button"
            class="btn btn-sm btn-cancel"
            onclick={md5Pause}
          >Pause</button>
          <button
            type="button"
            class="btn btn-sm btn-soft btn-error border border-error/50"
            onclick={md5Stop}
            disabled={md5StopRequested}
          >Stop</button>
        {/if}
      </div>
    </div>

    {#if md5Error}
      <div class="alert alert-error text-sm">{md5Error}</div>
    {/if}

    {#if md5Candidates !== null}
      {@const total = md5Candidates.length}
      {@const isComplete = !md5Running}
      <div class="space-y-2">
        <progress
          class="progress {md5Mismatches.length > 0 ? 'progress-error' : 'progress-primary'} w-full"
          value={md5Done}
          max={Math.max(total, 1)}
        ></progress>
        <div class="text-sm tabular-nums flex flex-wrap items-center gap-x-4 gap-y-1">
          <span>{md5Done} / {total}</span>
          {#if md5Mismatches.length > 0}
            <span class="text-error">{md5Mismatches.length} mismatch{md5Mismatches.length === 1 ? '' : 'es'}</span>
          {/if}
          {#if md5Errors.length > 0}
            <span class="text-warning">{md5Errors.length} error{md5Errors.length === 1 ? '' : 's'}</span>
          {/if}
          {#if md5Paused && !isComplete}
            <span class="badge badge-sm badge-warning">Paused</span>
          {/if}
          {#if isComplete && md5StopRequested}
            <span class="italic text-base-content/60">stopped early</span>
          {/if}
        </div>
        {#if md5Current && !isComplete}
          <div class="text-xs text-base-content/60 break-all truncate" title={md5Current}>
            Hashing: {md5Current}
          </div>
        {/if}
      </div>

      {#if isComplete && md5Mismatches.length === 0 && md5Errors.length === 0 && total > 0 && !md5StopRequested}
        <div class="alert alert-success text-sm">
          All {total} video file{total === 1 ? '' : 's'} match their stored MD5.
        </div>
      {/if}

      {#if md5Mismatches.length > 0}
        <div class="overflow-x-auto">
          <div class="text-xs uppercase tracking-wide text-base-content/60 mb-1">Mismatches</div>
          <table class="table table-sm resizable-table" style="table-layout: fixed; width: {totalW.md5Match}px;">
            <colgroup>
              <col style="width: {getW('md5Match', 'source', 160)}px" />
              <col style="width: {getW('md5Match', 'file', 480)}px" />
              <col style="width: {getW('md5Match', 'size', 96)}px" />
              <col style="width: {getW('md5Match', 'stored', 280)}px" />
              <col style="width: {getW('md5Match', 'computed', 280)}px" />
            </colgroup>
            <thead>
              <tr>
                {#each [
                  { key: 'source', label: 'Source', align: 'left', max: undefined, def: 160 },
                  { key: 'file', label: 'File', align: 'left', max: undefined, def: 480 },
                  { key: 'size', label: 'Size', align: 'right', max: 200, def: 96 },
                  { key: 'stored', label: 'Stored MD5', align: 'left', max: 400, def: 280 },
                  { key: 'computed', label: 'Computed MD5', align: 'left', max: 400, def: 280 },
                ] as col (col.key)}
                  <th
                    class="relative select-none p-0 {col.align === 'right' ? 'text-right' : 'text-left'}"
                    style="width: {getW('md5Match', col.key, col.def)}px;"
                  >
                    <span class="block px-3 py-2 truncate">{col.label}</span>
                    <button
                      type="button"
                      aria-label={`Resize ${col.label} (double-click to auto-fit)`}
                      class="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/40 active:bg-primary/60 z-10"
                      use:resizable={{
                        getWidth: () => getW('md5Match', col.key, 100),
                        setWidth: (w) => setW('md5Match', col.key, w),
                        maxWidth: col.max,
                      }}
                    ></button>
                  </th>
                {/each}
              </tr>
            </thead>
            <tbody>
              {#each md5Mismatches as r (r.videoId)}
                {@const c = md5CandidateFor(r.videoId)}
                <tr>
                  <td class="text-sm whitespace-nowrap">
                    {#if c?.sourceName}
                      {#if c.sourceEnabled}
                        {c.sourceName}
                      {:else}
                        <span class="line-through text-base-content/60">{c.sourceName}</span>
                        <span class="text-xs italic text-base-content/50 ml-1">(Disabled)</span>
                      {/if}
                    {:else}
                      <span class="text-base-content/40 italic">no source</span>
                    {/if}
                  </td>
                  <td>
                    <div class="font-medium break-all">{c?.fileName ?? '?'}</div>
                    <div class="text-xs text-base-content/60 break-all">{c?.filePath ?? ''}</div>
                  </td>
                  <td class="text-right tabular-nums text-sm">{formatBytes(r.fileSize)}</td>
                  <td class="font-mono text-xs break-all">{r.storedMd5}</td>
                  <td class="font-mono text-xs break-all text-error">{r.computedMd5}</td>
                </tr>
              {/each}
            </tbody>
          </table>
        </div>
      {/if}

      {#if md5Errors.length > 0}
        <div class="overflow-x-auto">
          <div class="text-xs uppercase tracking-wide text-base-content/60 mb-1 mt-3">Errors</div>
          <table class="table table-sm resizable-table" style="table-layout: fixed; width: {totalW.md5Errors}px;">
            <colgroup>
              <col style="width: {getW('md5Errors', 'source', 160)}px" />
              <col style="width: {getW('md5Errors', 'file', 480)}px" />
              <col style="width: {getW('md5Errors', 'error', 400)}px" />
            </colgroup>
            <thead>
              <tr>
                {#each [
                  { key: 'source', label: 'Source', align: 'left', def: 160 },
                  { key: 'file', label: 'File', align: 'left', def: 480 },
                  { key: 'error', label: 'Error', align: 'left', def: 400 },
                ] as col (col.key)}
                  <th
                    class="relative select-none p-0 text-left"
                    style="width: {getW('md5Errors', col.key, col.def)}px;"
                  >
                    <span class="block px-3 py-2 truncate">{col.label}</span>
                    <button
                      type="button"
                      aria-label={`Resize ${col.label} (double-click to auto-fit)`}
                      class="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/40 active:bg-primary/60 z-10"
                      use:resizable={{
                        getWidth: () => getW('md5Errors', col.key, 100),
                        setWidth: (w) => setW('md5Errors', col.key, w),
                      }}
                    ></button>
                  </th>
                {/each}
              </tr>
            </thead>
            <tbody>
              {#each md5Errors as e, i (i)}
                <tr>
                  <td class="text-sm whitespace-nowrap">
                    {#if e.candidate.sourceName}
                      {#if e.candidate.sourceEnabled}
                        {e.candidate.sourceName}
                      {:else}
                        <span class="line-through text-base-content/60">{e.candidate.sourceName}</span>
                        <span class="text-xs italic text-base-content/50 ml-1">(Disabled)</span>
                      {/if}
                    {:else}
                      —
                    {/if}
                  </td>
                  <td>
                    <div class="font-medium break-all">{e.candidate.fileName}</div>
                    <div class="text-xs text-base-content/60 break-all">{e.candidate.filePath}</div>
                  </td>
                  <td class="text-sm text-warning break-all">{e.error}</td>
                </tr>
              {/each}
            </tbody>
          </table>
        </div>
      {/if}
    {:else if !md5Running}
      <div class="text-sm text-base-content/50 italic">
        Click "Run check" to recompute every video's MD5 and compare
        against the stored hash. Skips clips, missing files, and
        videos that never had a hash computed.
      </div>
    {/if}
  </section>
</div>

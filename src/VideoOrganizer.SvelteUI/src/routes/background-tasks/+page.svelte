<script lang="ts">
  import { onMount, onDestroy } from 'svelte';
  import { api } from '$lib/api';
  import type {
    ImportFailedFileRow,
    ImportJobSummary,
    ImportQueueFileRow,
    WorkerFailedRow,
    WorkerQueueRow
  } from '$lib/types';
  import DataTableModal from '$lib/components/DataTableModal.svelte';

  // Polled from this page. A single 5s interval drives both cards — the
  // worker loops rescan at their own cadence, we just want to watch the
  // counters move.
  let thumbStatus = $state<{
    total: number;
    warmed: number;
    failed: number;
    pending: number;
    currentVideoId: string | null;
    currentFilePath: string | null;
    startedAt: string | null;
    nextScanAt: string | null;
    importDetectedAt: string | null;
  } | null>(null);
  let thumbError = $state<string | null>(null);

  let md5Status = $state<{
    total: number;
    hashed: number;
    pending: number;
    failed: number;
    currentFileName: string | null;
    currentFilePath: string | null;
    bytesProcessed: number;
    totalBytes: number;
    nextScanAt: string | null;
    importDetectedAt: string | null;
  } | null>(null);
  let md5Error = $state<string | null>(null);

  // Old inline-queue state (md5QueuePaths / thumbQueuePaths / loaders)
  // removed. The "Show Queue" buttons + DataTableModal at the bottom of
  // this file now own that responsibility — modalRows holds whatever the
  // currently-open modal is showing.

  // Skip current item — the worker sees the flag, cancels its current job,
  // marks the row as failed, and continues to the next one.
  let thumbSkipBusy = $state(false);
  let md5SkipBusy = $state(false);

  async function skipCurrentThumbnail() {
    thumbSkipBusy = true;
    try { await api.skipCurrentThumbnail(); await load(); }
    catch (e) { thumbError = e instanceof Error ? e.message : String(e); }
    finally { thumbSkipBusy = false; }
  }

  async function skipCurrentMd5() {
    md5SkipBusy = true;
    try { await api.skipCurrentMd5(); await load(); }
    catch (e) { md5Error = e instanceof Error ? e.message : String(e); }
    finally { md5SkipBusy = false; }
  }

  // Manual "wake up now" triggers for each worker — fires Signal() on the
  // server so an idle/sleeping worker picks up its next batch immediately.
  let thumbScanBusy = $state(false);
  let md5ScanBusy = $state(false);

  // Resets the *Failed flag on every matching row so the worker retries
  // them. Mirror of "Clear finished" on the Imports card.
  let thumbClearFailedBusy = $state(false);
  let md5ClearFailedBusy = $state(false);

  async function clearFailedThumbnails() {
    thumbClearFailedBusy = true;
    try { await api.clearFailedThumbnails(); await load(); }
    catch (e) { thumbError = e instanceof Error ? e.message : String(e); }
    finally { thumbClearFailedBusy = false; }
  }

  async function clearFailedMd5() {
    md5ClearFailedBusy = true;
    try { await api.clearFailedMd5(); await load(); }
    catch (e) { md5Error = e instanceof Error ? e.message : String(e); }
    finally { md5ClearFailedBusy = false; }
  }

  async function triggerThumbScan() {
    thumbScanBusy = true;
    try {
      await api.triggerThumbnailScan();
      await load();
    } catch (e) {
      thumbError = e instanceof Error ? e.message : String(e);
    } finally {
      thumbScanBusy = false;
    }
  }

  async function triggerMd5Scan() {
    md5ScanBusy = true;
    try {
      await api.triggerMd5BackfillScan();
      await load();
    } catch (e) {
      md5Error = e instanceof Error ? e.message : String(e);
    } finally {
      md5ScanBusy = false;
    }
  }

  // Named state for the status badge. The worker no longer auto-rescans on
  // a timer, so the only states are: just-woke-up grace ('import-detected'),
  // actively processing ('running'), or no work pending ('idle').
  function workerState(pending: number, _nextScanAt: string | null, importDetectedAt: string | null):
    'idle' | 'running' | 'import-detected' {
    if (importDetectedAt) return 'import-detected';
    if (pending === 0) return 'idle';
    return 'running';
  }

  // Poll every second so the per-file MD5 progress bar moves smoothly. These
  // endpoints are cheap — an index count + a SELECT over VideoSets.
  const POLL_INTERVAL_MS = 1000;
  let timer: ReturnType<typeof setInterval> | null = null;
  let tick: ReturnType<typeof setInterval> | null = null;
  let now = $state(Date.now());

  // Converts a server-provided ISO timestamp into seconds-from-now, clamped
  // to 0. Null when the worker is busy (not idling).
  function secondsUntil(iso: string | null): number | null {
    if (!iso) return null;
    const t = Date.parse(iso);
    if (Number.isNaN(t)) return null;
    return Math.max(0, Math.ceil((t - now) / 1000));
  }

  function formatCountdown(seconds: number): string {
    if (seconds < 60) return `${seconds}s`;
    const m = Math.floor(seconds / 60);
    const s = seconds - m * 60;
    return s === 0 ? `${m}m` : `${m}m ${s}s`;
  }

  const thumbSecondsToScan = $derived(secondsUntil(thumbStatus?.nextScanAt ?? null));
  const md5SecondsToScan = $derived(secondsUntil(md5Status?.nextScanAt ?? null));

  // Top-right summary across both workers. "Running" while either task is
  // actively processing; "Starting in Xs" during the brief post-import
  // grace; "Idle" otherwise. (No more periodic-rescan countdown.)
  const overallCountdownText = $derived.by<string>(() => {
    const thumbBusy = thumbStatus && thumbStatus.pending > 0 && thumbStatus.nextScanAt === null;
    const md5Busy = md5Status && md5Status.pending > 0 && md5Status.nextScanAt === null;
    if (thumbBusy || md5Busy) return 'Running';
    const candidates: number[] = [];
    if (thumbSecondsToScan !== null) candidates.push(thumbSecondsToScan);
    if (md5SecondsToScan !== null) candidates.push(md5SecondsToScan);
    if (candidates.length === 0) return 'Idle';
    return `Starting in ${formatCountdown(Math.min(...candidates))}`;
  });

  // Imports — fed by /api/import/jobs every poll. Independent of the
  // import-page's own polling: the user can land here from any page and see
  // anything that's still running (or has recently finished).
  let importJobs = $state<ImportJobSummary[]>([]);
  let importsError = $state<string | null>(null);
  let clearingImports = $state(false);

  const activeImports = $derived(importJobs.filter((j) => !j.isCompleted));
  const finishedImports = $derived(importJobs.filter((j) => j.isCompleted));
  // Two slices of "active" — queued (the queue worker hasn't pulled
  // them yet) and running (currently in the Add-to-database phase).
  // Drives the badge on the new Library-wide "Add to database" card.
  const queuedImports = $derived(activeImports.filter((j) => j.startedAt === null));
  const runningImports = $derived(activeImports.filter((j) => j.startedAt !== null));

  // Paused-flag state for the three workers. Polled alongside the rest;
  // toggled by the per-card pause/resume buttons.
  let pauseStatus = $state<{ importPaused: boolean; thumbnailsPaused: boolean; md5Paused: boolean }>({
    importPaused: false,
    thumbnailsPaused: false,
    md5Paused: false,
  });
  let pauseBusy = $state<{ import: boolean; thumb: boolean; md5: boolean }>({
    import: false, thumb: false, md5: false,
  });

  async function toggleImportPause() {
    pauseBusy.import = true;
    try {
      if (pauseStatus.importPaused) await api.resumeImports();
      else await api.pauseImports();
      await load();
    } finally { pauseBusy.import = false; }
  }
  async function toggleThumbnailsPause() {
    pauseBusy.thumb = true;
    try {
      if (pauseStatus.thumbnailsPaused) await api.resumeThumbnails();
      else await api.pauseThumbnails();
      await load();
    } finally { pauseBusy.thumb = false; }
  }
  async function toggleMd5Pause() {
    pauseBusy.md5 = true;
    try {
      if (pauseStatus.md5Paused) await api.resumeMd5();
      else await api.pauseMd5();
      await load();
    } finally { pauseBusy.md5 = false; }
  }

  // Aggregate counts that drive the "Show Failed (N)" / "Show Queue (N)"
  // button labels. `queue` = files still to do across active jobs (anything
  // not yet completed/failed/skipped); `failed` = total failed across every
  // job in memory.
  const importsFailedCount = $derived(
    importJobs.reduce((sum, j) => sum + j.failedCount, 0)
  );
  const importsQueueCount = $derived(
    activeImports.reduce(
      (sum, j) => sum + Math.max(0, j.totalFiles - j.completedCount - j.failedCount - j.skippedCount),
      0
    )
  );

  // Show Failed / Show Queue modal state.
  // Each section has two buttons that open the same DataTableModal with
  // different columns, search keys, and fetcher. Stored as discriminated
  // unions so we don't proliferate state vars.
  type ModalKind =
    | 'thumb-failed' | 'thumb-queue'
    | 'md5-failed'   | 'md5-queue'   | 'md5-dups'
    | 'imports-failed' | 'imports-queue';
  let openModal = $state<ModalKind | null>(null);
  let modalRows = $state<Array<Record<string, unknown>>>([]);
  let modalLoading = $state(false);
  let modalError = $state<string | null>(null);

  async function openTableModal(kind: ModalKind) {
    openModal = kind;
    modalError = null;
    await refreshModal();
  }

  async function refreshModal() {
    if (!openModal) return;
    modalLoading = true;
    modalError = null;
    try {
      switch (openModal) {
        case 'thumb-failed':
          modalRows = (await api.getFailedThumbnails()) as unknown as Record<string, unknown>[];
          break;
        case 'thumb-queue':
          modalRows = (await api.getThumbnailQueue()) as unknown as Record<string, unknown>[];
          break;
        case 'md5-failed':
          modalRows = (await api.getFailedMd5()) as unknown as Record<string, unknown>[];
          break;
        case 'md5-queue':
          modalRows = (await api.getMd5BackfillQueue()) as unknown as Record<string, unknown>[];
          break;
        case 'md5-dups':
          modalRows = (await api.getMd5Duplicates()) as unknown as Record<string, unknown>[];
          break;
        case 'imports-failed':
          modalRows = (await api.getFailedImportFiles()) as unknown as Record<string, unknown>[];
          break;
        case 'imports-queue':
          modalRows = (await api.getImportQueue()) as unknown as Record<string, unknown>[];
          break;
      }
    } catch (e) {
      modalError = e instanceof Error ? e.message : String(e);
    } finally {
      modalLoading = false;
    }
  }

  function closeModal() {
    openModal = null;
    modalRows = [];
    modalError = null;
  }

  // Column sets per modal kind. Reused across "failed" and "queue" — only
  // difference is whether an Error column is present.
  // Mirrors the Column interface in DataTableModal — keep them in sync.
  interface Column {
    key: string;
    label: string;
    mono?: boolean;
    wide?: boolean;
    align?: 'left' | 'right';
    defaultWidth?: number;
  }
  const failedColumns: Column[] = [
    { key: 'fileName', label: 'File name', mono: true },
    { key: 'filePath', label: 'File path', mono: true, wide: true },
    { key: 'fileSizeBytes', label: 'Size', align: 'right' },
    { key: 'error', label: 'Error', wide: true }
  ];
  const queueColumns: Column[] = [
    { key: 'fileName', label: 'File name', mono: true },
    { key: 'filePath', label: 'File path', mono: true, wide: true },
    { key: 'fileSizeBytes', label: 'Size', align: 'right' }
  ];
  const importQueueColumns: Column[] = [
    { key: 'fileName', label: 'File name', mono: true },
    { key: 'filePath', label: 'File path', mono: true, wide: true },
    { key: 'fileSizeBytes', label: 'Size', align: 'right' },
    { key: 'status', label: 'Status' }
  ];
  // MD5 dups: show the hash so the user can see which rows belong to the
  // same group (the SQL sort by md5 already clusters them).
  const md5DupsColumns: Column[] = [
    { key: 'fileName', label: 'File name', mono: true },
    { key: 'filePath', label: 'File path', mono: true, wide: true },
    { key: 'fileSizeBytes', label: 'Size', align: 'right' },
    { key: 'md5', label: 'MD5', mono: true },
    { key: 'groupSize', label: '# in group', align: 'right', defaultWidth: 80 }
  ];

  // Modal-config dispatch — keyed by ModalKind so the template stays terse.
  const modalConfig: Record<ModalKind, {
    title: string; columns: Column[]; searchKeys: string[]; emptyText: string;
  }> = {
    'thumb-failed':   { title: 'Create thumbnails — Failed', columns: failedColumns, searchKeys: ['fileName', 'filePath', 'error'], emptyText: 'No failed thumbnails.' },
    'thumb-queue':    { title: 'Create thumbnails — Queue',  columns: queueColumns,  searchKeys: ['fileName', 'filePath'],          emptyText: 'Queue is empty.' },
    'md5-failed':     { title: 'Calculate MD5 — Failed',        columns: failedColumns, searchKeys: ['fileName', 'filePath', 'error'], emptyText: 'No failed MD5 hashes.' },
    'md5-queue':      { title: 'Calculate MD5 — Queue',         columns: queueColumns,  searchKeys: ['fileName', 'filePath'],          emptyText: 'Queue is empty.' },
    'md5-dups':       { title: 'Calculate MD5 — Duplicates',    columns: md5DupsColumns, searchKeys: ['fileName', 'filePath', 'md5'],  emptyText: 'No duplicate MD5 hashes found.' },
    'imports-failed': { title: 'Imports — Failed',              columns: failedColumns, searchKeys: ['fileName', 'filePath', 'error'], emptyText: 'No failed imports in memory.' },
    'imports-queue':  { title: 'Imports — Queue',               columns: importQueueColumns, searchKeys: ['fileName', 'filePath', 'status'], emptyText: 'No active imports.' }
  };

  async function clearFinishedImports() {
    clearingImports = true;
    try {
      await api.clearCompletedImportJobs();
      importJobs = await api.listImportJobs();
    } catch (e) {
      importsError = e instanceof Error ? e.message : String(e);
    } finally {
      clearingImports = false;
    }
  }

  // Take only the last two segments of a path so the chip stays readable
  // on narrow screens. Tooltip still has the full path.
  function shortenPath(p: string): string {
    if (!p) return '';
    const norm = p.replace(/\\/g, '/').replace(/\/+$/, '');
    const parts = norm.split('/').filter((s) => s.length > 0);
    if (parts.length <= 2) return norm;
    return '…/' + parts.slice(-2).join('/');
  }

  function formatDuration(startedIso: string, endedIso: string | null): string {
    const start = Date.parse(startedIso);
    if (Number.isNaN(start)) return '';
    const end = endedIso ? Date.parse(endedIso) : now;
    const ms = Math.max(0, end - start);
    const s = Math.floor(ms / 1000);
    if (s < 60) return `${s}s`;
    const m = Math.floor(s / 60);
    const r = s - m * 60;
    return r === 0 ? `${m}m` : `${m}m ${r}s`;
  }

  async function load() {
    try {
      thumbStatus = await api.getThumbnailStatus();
      thumbError = null;
    } catch (e) {
      thumbError = e instanceof Error ? e.message : String(e);
    }
    try {
      md5Status = await api.getMd5BackfillStatus();
      md5Error = null;
    } catch (e) {
      md5Error = e instanceof Error ? e.message : String(e);
    }
    try {
      importJobs = await api.listImportJobs();
      importsError = null;
    } catch (e) {
      importsError = e instanceof Error ? e.message : String(e);
    }
    try {
      pauseStatus = await api.getWorkerPauseStatus();
    } catch {
      /* non-fatal — buttons stay rendered with stale state */
    }
  }

  // Progress treats failed rows as "done" — they're terminal states the
  // worker won't retry, so leaving them out of the bar makes a fully-caught-up
  // queue look stuck at e.g. 78%. Same logic applies to MD5 backfill.
  const thumbPercent = $derived(
    thumbStatus && thumbStatus.total > 0
      ? Math.round(((thumbStatus.warmed + thumbStatus.failed) / thumbStatus.total) * 100)
      : 0
  );

  const md5Percent = $derived(
    md5Status && md5Status.total > 0
      ? Math.round(((md5Status.hashed + md5Status.failed) / md5Status.total) * 100)
      : 0
  );

  const md5FilePercent = $derived(
    md5Status && md5Status.totalBytes > 0
      ? Math.min(100, Math.round((md5Status.bytesProcessed / md5Status.totalBytes) * 100))
      : 0
  );

  function formatBytes(bytes: number): string {
    if (!bytes || bytes <= 0) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0, n = bytes;
    while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
    return `${n.toFixed(i === 0 ? 0 : 2)} ${units[i]}`;
  }

  onMount(() => {
    load();
    timer = setInterval(load, POLL_INTERVAL_MS);
    // Separate 1-second ticker drives the countdown. Kept independent of the
    // data-fetch timer so the displayed seconds stay accurate even if the
    // fetch itself is slow.
    tick = setInterval(() => { now = Date.now(); }, 250);
  });

  onDestroy(() => {
    if (timer) clearInterval(timer);
    if (tick) clearInterval(tick);
  });
</script>

<svelte:head><title>Background Tasks</title></svelte:head>

<!-- Flex column with explicit `order-N` classes on each child so we can
     position cards (Library-wide above Imports) without physically
     moving the JSX blocks. The default `space-y-*` is replaced by
     `gap-8` since flex containers don't honor space-y. -->
<div class="flex flex-col gap-8">
  <div class="order-1 flex items-center justify-between gap-3 flex-wrap">
    <h1 class="text-2xl font-semibold">Background Tasks</h1>
    <div class="flex items-center gap-3">
      <span class="text-sm text-base-content/60 tabular-nums">
        {overallCountdownText}
      </span>
      <button type="button" class="btn btn-ghost btn-sm" onclick={load} title="Refresh now">↻ Refresh</button>
    </div>
  </div>

  <!-- ========= Library-wide outer card =========
       One outer card that mirrors the Imports section: header on top,
       <ul> of inner cards underneath. The three inner cards (Add to
       database / Create thumbnails / Calculate MD5) all use the same
       `rounded border` styling as per-import rows so the page reads as
       two parallel groups (Library-wide + Imports). -->
  <section class="order-2 card bg-base-200 p-4 space-y-3">
    <div class="flex items-center justify-between gap-3 flex-wrap">
      <div class="flex items-center gap-3">
        <h2 class="text-lg font-semibold">Library-wide</h2>
      </div>
    </div>

    <ul class="space-y-3">
      <!-- Add to database — aggregate view of the import queue. -->
      <li class="rounded-lg border border-base-300 bg-base-100 p-4 space-y-3 shadow-sm">
        <div class="flex items-baseline gap-3 flex-wrap mb-1">
          <h2 class="text-base font-semibold">Add to database</h2>
          {#if pauseStatus.importPaused}
            <span class="badge badge-sm badge-warning" title="Import queue is paused.">
              ⏸ paused
            </span>
          {:else if runningImports.length > 0 || queuedImports.length > 0}
            <span
              class="badge badge-sm {runningImports.length > 0 ? 'badge-success' : 'badge-warning'}"
              title={runningImports.length > 0
                ? 'Add-to-database worker is processing an import.'
                : 'Imports are queued waiting for the worker.'}
            >
              {#if runningImports.length > 0}
                <span class="loading loading-spinner loading-xs"></span> running
              {:else}
                queued
              {/if}
            </span>
            <span class="text-xs text-base-content/60 tabular-nums">
              {runningImports.length} running · {queuedImports.length} queued
            </span>
          {:else}
            <span class="badge badge-sm badge-ghost">idle</span>
          {/if}
          <div class="flex items-center gap-2 ml-auto">
            <button
              type="button"
              class="btn btn-xs btn-cancel"
              onclick={toggleImportPause}
              disabled={pauseBusy.import}
              title={pauseStatus.importPaused
                ? 'Resume the import queue worker.'
                : 'Pause the queue. Currently-importing job finishes, then no more pull until resumed.'}
            >
              {#if pauseBusy.import}<span class="loading loading-spinner loading-xs"></span>{/if}
              {#if pauseStatus.importPaused}
                <svg viewBox="0 0 24 24" class="w-3 h-3 fill-current"><polygon points="7,4 20,12 7,20" /></svg>
                Resume
              {:else}
                <svg viewBox="0 0 24 24" class="w-3 h-3 fill-current"><rect x="6" y="4" width="4" height="16" /><rect x="14" y="4" width="4" height="16" /></svg>
                Pause
              {/if}
            </button>
            <a href="/import" class="btn btn-xs">Open Import page</a>
          </div>
        </div>
        <div class="text-xs text-base-content/60 tabular-nums">
          Library has <span class="font-semibold">{thumbStatus?.total ?? 0}</span> videos.
        </div>
      </li>

      {@render thumbnailCard()}
      {@render md5Card()}
    </ul>

    <!-- Library-wide bottom action row. Three globally-scoped scans live
         here so they're easy to find regardless of which inner card you
         were last looking at. Styled as outlined primary buttons so they
         read clearly as actions, not chrome. -->
    <div class="flex flex-wrap items-center gap-2 pt-3 border-t border-base-300">
      <span class="text-sm font-medium text-base-content/70 mr-1">Library-wide actions:</span>
      <button
        type="button"
        class="btn btn-sm btn-soft btn-primary btn-cta"
        onclick={triggerThumbScan}
        disabled={thumbScanBusy || !thumbStatus || thumbStatus.total === 0}
        title="Scan the library now for any video missing a thumbnail."
      >
        {#if thumbScanBusy}<span class="loading loading-spinner loading-xs"></span>{/if}
        🔍 Scan Thumbnails (entire library)
      </button>
      <button
        type="button"
        class="btn btn-sm btn-soft btn-primary btn-cta"
        onclick={triggerMd5Scan}
        disabled={md5ScanBusy || !md5Status || md5Status.total === 0}
        title="Scan the library now for any video missing an MD5."
      >
        {#if md5ScanBusy}<span class="loading loading-spinner loading-xs"></span>{/if}
        🔍 Scan MD5 (entire library)
      </button>
      <!-- Disabled until at least two videos have an MD5 — finding
           "duplicates" requires comparable hashes. md5Status.hashed is
           computed server-side as (total - pending - failed). -->
      <button
        type="button"
        class="btn btn-sm btn-soft btn-primary btn-cta"
        onclick={() => openTableModal('md5-dups')}
        disabled={!md5Status || md5Status.hashed < 2}
        title={md5Status && md5Status.hashed >= 2
          ? 'Find videos that share an MD5 hash.'
          : `Need at least 2 videos with an MD5 to compare (currently ${md5Status?.hashed ?? 0}).`}
      >
        🔎 Check for dups by MD5
      </button>
    </div>
  </section>

  <!-- ========= Imports =========
       Per-import grouping panel. order-3 puts it below the single
       Library-wide outer card (order-2). The Create thumbnails /
       Calculate MD5 snippets live further down in the DOM but render
       inside the Library-wide card via {@render}. -->
  <section class="order-3 card bg-base-200 p-4 space-y-3">
    <div class="flex items-center justify-between gap-3 flex-wrap">
      <div class="flex items-center gap-3">
        <h2 class="text-lg font-semibold">Imports</h2>
        {#if activeImports.length > 0}
          <span class="badge badge-primary badge-sm font-bold tabular-nums">
            {activeImports.length} running
          </span>
        {:else if finishedImports.length > 0}
          <span class="badge badge-ghost badge-sm tabular-nums">idle</span>
        {/if}
      </div>
      <div class="flex items-center gap-2">
        <a href="/import" class="btn btn-sm btn-ghost">Open Import page</a>
        <button
          type="button"
          class="btn btn-sm btn-ghost"
          onclick={() => openTableModal('imports-queue')}
          disabled={importsQueueCount === 0}
          title={importsQueueCount > 0 ? 'View pending and importing files' : 'Nothing in flight'}
        >
          Show Queue ({importsQueueCount})
        </button>
        <button
          type="button"
          class="btn btn-sm btn-ghost"
          onclick={() => openTableModal('imports-failed')}
          disabled={importsFailedCount === 0}
          title={importsFailedCount > 0 ? 'View files that failed to import' : 'Nothing failed'}
        >
          Show Failed ({importsFailedCount})
        </button>
        {#if finishedImports.length > 0}
          <button
            type="button"
            class="btn btn-sm btn-ghost"
            onclick={clearFinishedImports}
            disabled={clearingImports}
            title="Remove completed/failed jobs from the list"
          >
            {#if clearingImports}<span class="loading loading-spinner loading-xs"></span>{/if}
            Clear finished
          </button>
        {/if}
      </div>
    </div>

    {#if importsError}<div class="alert alert-error text-sm">{importsError}</div>{/if}

    {#if importJobs.length === 0}
      <div class="text-sm text-base-content/60 italic">
        No import jobs in memory. Start one from the
        <a href="/import" class="link link-primary">Import page</a>.
      </div>
    {:else}
      <!-- Each panel groups three progress rows under one import name —
           Importing files, Create thumbnails for those files, Calculate
           MD5 for those files. Per-task counts come from the server-side
           join on Videos.ImportJobId. -->
      <ul class="space-y-3">
        {#each importJobs as job (job.jobId)}
          {@const importTotal = job.totalFiles}
          {@const importDone = job.completedCount + job.failedCount + job.skippedCount}
          {@const importPercent = importTotal > 0 ? Math.round((importDone / importTotal) * 100) : 0}
          {@const thumbsPercent = job.thumbnails.total > 0
            ? Math.round(((job.thumbnails.done + job.thumbnails.failed) / job.thumbnails.total) * 100)
            : 0}
          {@const md5Percent = job.md5.total > 0
            ? Math.round(((job.md5.done + job.md5.failed) / job.md5.total) * 100)
            : 0}
          {@const isQueued = !job.isCompleted && job.startedAt === null}
          <li
            class="rounded border border-base-300 p-3 space-y-3"
            class:bg-base-100={!job.isCompleted}
            class:bg-base-300={job.isCompleted}
          >
            <!-- Header: import name (title) + status + duration -->
            <div class="flex items-start justify-between gap-3 flex-wrap">
              <div class="flex items-center gap-2 min-w-0">
                {#if isQueued}
                  <span class="badge badge-ghost badge-sm">queued</span>
                {:else if !job.isCompleted}
                  <span class="loading loading-spinner loading-xs shrink-0"></span>
                  <span class="badge badge-primary badge-sm">running</span>
                {:else if job.error}
                  <span class="badge badge-error badge-sm">failed</span>
                {:else}
                  <span class="badge badge-sm bg-success/25 text-success border-success/30 font-bold">done</span>
                {/if}
                <div class="min-w-0">
                  <div class="text-base font-semibold truncate" title={job.directoryPath}>
                    {job.name || shortenPath(job.directoryPath)}
                  </div>
                  <div class="text-xs font-mono text-base-content/55 break-all">
                    {shortenPath(job.directoryPath)}
                  </div>
                </div>
              </div>
              <div class="flex items-center gap-3 text-xs text-base-content/70 tabular-nums">
                {#if isQueued}
                  <span title={`Enqueued ${new Date(job.enqueuedAt).toLocaleString()}`}>
                    queued for {formatDuration(job.enqueuedAt, null)}
                  </span>
                {:else}
                  <span title={`Started ${new Date(job.startedAt!).toLocaleString()}`}>
                    {formatDuration(job.startedAt!, job.completedAt)}
                  </span>
                {/if}
              </div>
            </div>

            <!-- Three progress rows: one per task type. Each row shows a
                 mini progress bar + counts. Disabled-looking when total=0
                 (e.g. import hasn't created any Video rows yet, or job
                 pre-dates the ImportJobId column). -->
            <div class="grid grid-cols-[110px_1fr_auto] items-center gap-x-3 gap-y-1 text-xs">
              <!-- Row 1: Add to database (the "import" phase — saves Video
                   rows from filesystem scan + ffprobe metadata). -->
              <span class="text-base-content/70">Add to database</span>
              <progress class="progress progress-primary h-2" value={importPercent} max="100"></progress>
              <span class="tabular-nums text-base-content/70 whitespace-nowrap">
                {importDone}/{importTotal}
              </span>

              <!-- Row 2: Create thumbnails -->
              <span class="text-base-content/70">Create thumbnails</span>
              <progress class="progress progress-info h-2" value={thumbsPercent} max="100" class:opacity-30={job.thumbnails.total === 0}></progress>
              <span class="tabular-nums text-base-content/70 whitespace-nowrap" class:opacity-50={job.thumbnails.total === 0}>
                {job.thumbnails.done}/{job.thumbnails.total}{job.thumbnails.failed > 0 ? ` · ${job.thumbnails.failed} failed` : ''}
              </span>

              <!-- Row 3: Calculate MD5 -->
              <span class="text-base-content/70">Calculate MD5</span>
              <progress class="progress progress-info h-2" value={md5Percent} max="100" class:opacity-30={job.md5.total === 0}></progress>
              <span class="tabular-nums text-base-content/70 whitespace-nowrap" class:opacity-50={job.md5.total === 0}>
                {job.md5.done}/{job.md5.total}{job.md5.failed > 0 ? ` · ${job.md5.failed} failed` : ''}
              </span>
            </div>

            <!-- "Now importing" line stays in document flow whenever
                 the job is active so the rest of the card doesn't
                 reflow each time the worker advances between files.
                 Empty placeholder fills the slot during inter-file
                 hand-off (currentFilePath transiently null). -->
            {#if !job.isCompleted}
              <div class="text-xs text-base-content/70 break-all" title={job.currentFilePath ?? ''}>
                <span class="text-base-content/50">Now importing:</span>
                {#if job.currentFilePath}
                  {job.currentFilePath}
                {:else}
                  <span class="italic text-base-content/40">No File Processing</span>
                {/if}
              </div>
            {/if}

            {#if job.error}
              <div class="text-xs text-error break-words">{job.error}</div>
            {/if}
          </li>
        {/each}
      </ul>
    {/if}
  </section>

  <!-- ========= Create thumbnails =========
       Defined as a snippet here so it can render inside the Library-wide
       outer card at the top of the page. The actual JSX stays at this
       DOM position to avoid a 250-line cut/paste; {@render thumbnailCard()}
       up top pulls it into the visual hierarchy. -->
  {#snippet thumbnailCard()}
  <li class="rounded-lg border border-base-300 bg-base-100 p-4 space-y-3 shadow-sm">
    <div class="flex items-baseline gap-3 flex-wrap mb-1">
      <h2 class="text-base font-semibold">Create thumbnails</h2>
      {#if pauseStatus.thumbnailsPaused}
        <span class="badge badge-sm badge-warning" title="Thumbnail worker is paused.">
          ⏸ paused
        </span>
      {:else if thumbStatus}
        {@const s = workerState(thumbStatus.pending, thumbStatus.nextScanAt, thumbStatus.importDetectedAt)}
        <span
          class="badge badge-sm {s === 'running' ? 'badge-success' : s === 'import-detected' ? 'badge-info' : 'badge-ghost'}"
          title={
            s === 'idle' ? 'Nothing pending. Worker waits for the next signal — finished import, "Scan Entire Library", or "Retry failed".' :
            s === 'running' ? 'Worker is actively processing videos right now.' :
            'Signal received. Worker is in a short grace window so bursty events batch into one scan.'
          }
        >
          {#if s === 'import-detected'}
            📥 Starting Thumbnail creation
          {:else if s === 'running'}
            <span class="loading loading-spinner loading-xs"></span> running
          {:else}
            idle
          {/if}
        </span>
        {#if s === 'import-detected' && thumbSecondsToScan !== null}
          <span class="text-sm text-info tabular-nums">
            starting in {formatCountdown(thumbSecondsToScan)}
          </span>
        {/if}
      {/if}
      <div class="ml-auto flex items-center gap-2">
        <button
          type="button"
          class="btn btn-xs btn-cancel"
          onclick={toggleThumbnailsPause}
          disabled={pauseBusy.thumb}
          title={pauseStatus.thumbnailsPaused
            ? 'Resume the thumbnail worker.'
            : 'Pause after the current item.'}
        >
          {#if pauseBusy.thumb}<span class="loading loading-spinner loading-xs"></span>{/if}
          {#if pauseStatus.thumbnailsPaused}
            <svg viewBox="0 0 24 24" class="w-3 h-3 fill-current"><polygon points="7,4 20,12 7,20" /></svg>
            Resume
          {:else}
            <svg viewBox="0 0 24 24" class="w-3 h-3 fill-current"><rect x="6" y="4" width="4" height="16" /><rect x="14" y="4" width="4" height="16" /></svg>
            Pause
          {/if}
        </button>
        <button
          type="button"
          class="btn btn-xs btn-ghost"
          onclick={() => openTableModal('thumb-queue')}
          disabled={!thumbStatus || thumbStatus.pending === 0}
          title={thumbStatus && thumbStatus.pending > 0
            ? 'View videos waiting to be warmed.'
            : 'Queue is empty.'}
        >
          Show Queue ({thumbStatus?.pending ?? 0})
        </button>
        <button
          type="button"
          class="btn btn-xs btn-ghost"
          onclick={() => openTableModal('thumb-failed')}
          disabled={!thumbStatus || thumbStatus.failed === 0}
          title={thumbStatus && thumbStatus.failed > 0
            ? 'View thumbnails that failed to generate, with the captured error.'
            : 'Nothing currently flagged as failed.'}
        >
          Show Failed ({thumbStatus?.failed ?? 0})
        </button>
        <!-- Always rendered — disabled when no rows are flagged — so the
             button is discoverable even when failed=0. -->
        <button
          type="button"
          class="btn btn-xs btn-ghost"
          onclick={clearFailedThumbnails}
          disabled={thumbClearFailedBusy || !thumbStatus || thumbStatus.failed === 0}
          title={thumbStatus && thumbStatus.failed > 0
            ? 'Reset the ThumbnailsFailed flag on all videos so the worker retries them.'
            : 'Nothing currently flagged as failed.'}
        >
          {#if thumbClearFailedBusy}<span class="loading loading-spinner loading-xs"></span>{/if}
          Retry failed ({thumbStatus?.failed ?? 0})
        </button>
        <!-- "Scan Entire Library" moved to the Library-wide outer card's
             bottom action row so all three global scans live together. -->
      </div>
    </div>
    {#if thumbError}
      <div class="alert alert-error text-sm mb-2">{thumbError}</div>
    {/if}

    {#if thumbStatus === null}
      <div class="flex items-center gap-2 text-base-content/70 text-sm">
        <span class="loading loading-spinner loading-sm"></span> Loading...
      </div>
    {:else if thumbStatus.total === 0}
      <div class="text-base-content/60 italic text-sm">No videos in enabled sets.</div>
    {:else}
      <div class="flex items-baseline gap-3 mb-2 flex-wrap">
        <div class="text-lg font-semibold tabular-nums">
          {thumbStatus.warmed} / {thumbStatus.total}
        </div>
        <div class="text-sm text-base-content/70">{thumbPercent}% ready</div>
        {#if thumbStatus.pending > 0}
          <div class="text-sm text-warning">{thumbStatus.pending} pending</div>
        {:else}
          <div class="text-sm text-success">All caught up</div>
        {/if}
        {#if thumbStatus.failed > 0}
          <div class="text-sm text-error">{thumbStatus.failed} failed</div>
        {/if}
      </div>
      <progress
        class="progress progress-primary w-full"
        value={thumbStatus.warmed + thumbStatus.failed}
        max={thumbStatus.total}
      ></progress>

      <!-- "Now processing" row is always rendered so the card height
           is stable across worker idle/active transitions. When the
           worker has nothing in flight, the spinner + Skip button
           reserve their footprint via `invisible` and the file slot
           shows the "No File Processing" placeholder. -->
      <div class="mt-4 p-3 bg-base-200 rounded space-y-2">
        <div class="flex items-center gap-2">
          {#if thumbStatus.currentFilePath}
            <span class="loading loading-spinner loading-xs"></span>
            <span class="text-sm font-medium truncate flex-1" title={thumbStatus.currentFilePath}>
              {thumbStatus.currentFilePath}
            </span>
            <button
              type="button"
              class="btn btn-xs btn-soft btn-error border border-error/50"
              onclick={skipCurrentThumbnail}
              disabled={thumbSkipBusy}
              title="Mark this video as ThumbnailsFailed and move on"
            >
              {#if thumbSkipBusy}<span class="loading loading-spinner loading-xs"></span>{/if}
              Skip
            </button>
          {:else}
            <span class="loading loading-spinner loading-xs invisible" aria-hidden="true"></span>
            <span class="text-sm italic text-base-content/50 flex-1">No File Processing</span>
            <span class="btn btn-xs btn-soft btn-error border border-error/50 invisible" aria-hidden="true">Skip</span>
          {/if}
        </div>
      </div>

      <!-- Queue preview moved to the "Show Queue" modal at the top of this
           card. Same data, but with FileName + FileSize columns + search. -->
    {/if}
  </li>
  {/snippet}

  <!-- ========= Calculate MD5 =========
       Same snippet pattern as Create thumbnails above. -->
  {#snippet md5Card()}
  <li class="rounded-lg border border-base-300 bg-base-100 p-4 space-y-3 shadow-sm">
    <div class="flex items-baseline gap-3 flex-wrap mb-1">
      <h2 class="text-base font-semibold">Calculate MD5</h2>
      {#if pauseStatus.md5Paused}
        <span class="badge badge-sm badge-warning" title="MD5 worker is paused.">
          ⏸ paused
        </span>
      {:else if md5Status}
        {@const s = workerState(md5Status.pending, md5Status.nextScanAt, md5Status.importDetectedAt)}
        <span
          class="badge badge-sm {s === 'running' ? 'badge-success' : s === 'import-detected' ? 'badge-info' : 'badge-ghost'}"
          title={
            s === 'idle' ? 'Nothing pending. Worker waits for the next signal — finished import, "Scan Entire Library", or "Retry failed".' :
            s === 'running' ? 'Worker is actively hashing a batch of up to 100 videos.' :
            'Signal received. Worker is in a short grace window so bursty events batch into one scan.'
          }
        >
          {#if s === 'import-detected'}
            📥 Starting MD5 calculation
          {:else if s === 'running'}
            <span class="loading loading-spinner loading-xs"></span> running
          {:else}
            idle
          {/if}
        </span>
        {#if s === 'import-detected' && md5SecondsToScan !== null}
          <span class="text-sm text-info tabular-nums">
            starting in {formatCountdown(md5SecondsToScan)}
          </span>
        {/if}
      {/if}
      <div class="ml-auto flex items-center gap-2">
        <button
          type="button"
          class="btn btn-xs btn-cancel"
          onclick={toggleMd5Pause}
          disabled={pauseBusy.md5}
          title={pauseStatus.md5Paused
            ? 'Resume the MD5 worker.'
            : 'Pause after the current item.'}
        >
          {#if pauseBusy.md5}<span class="loading loading-spinner loading-xs"></span>{/if}
          {#if pauseStatus.md5Paused}
            <svg viewBox="0 0 24 24" class="w-3 h-3 fill-current"><polygon points="7,4 20,12 7,20" /></svg>
            Resume
          {:else}
            <svg viewBox="0 0 24 24" class="w-3 h-3 fill-current"><rect x="6" y="4" width="4" height="16" /><rect x="14" y="4" width="4" height="16" /></svg>
            Pause
          {/if}
        </button>
        <button
          type="button"
          class="btn btn-xs btn-ghost"
          onclick={() => openTableModal('md5-queue')}
          disabled={!md5Status || md5Status.pending === 0}
          title={md5Status && md5Status.pending > 0
            ? 'View videos waiting to be hashed.'
            : 'Queue is empty.'}
        >
          Show Queue ({md5Status?.pending ?? 0})
        </button>
        <button
          type="button"
          class="btn btn-xs btn-ghost"
          onclick={() => openTableModal('md5-failed')}
          disabled={!md5Status || md5Status.failed === 0}
          title={md5Status && md5Status.failed > 0
            ? 'View videos whose MD5 failed, with the captured error.'
            : 'Nothing currently flagged as failed.'}
        >
          Show Failed ({md5Status?.failed ?? 0})
        </button>
        <button
          type="button"
          class="btn btn-xs btn-ghost"
          onclick={clearFailedMd5}
          disabled={md5ClearFailedBusy || !md5Status || md5Status.failed === 0}
          title={md5Status && md5Status.failed > 0
            ? 'Reset the Md5Failed flag on all videos so the worker retries them.'
            : 'Nothing currently flagged as failed.'}
        >
          {#if md5ClearFailedBusy}<span class="loading loading-spinner loading-xs"></span>{/if}
          Retry failed ({md5Status?.failed ?? 0})
        </button>
        <!-- "Check for MD5 dups" + "Scan Entire Library" moved to the
             Library-wide outer card's bottom action row. -->
      </div>
    </div>
    {#if md5Error}
      <div class="alert alert-error text-sm mb-2">{md5Error}</div>
    {/if}

    {#if md5Status === null}
      <div class="flex items-center gap-2 text-base-content/70 text-sm">
        <span class="loading loading-spinner loading-sm"></span> Loading...
      </div>
    {:else if md5Status.total === 0}
      <div class="text-base-content/60 italic text-sm">No videos yet.</div>
    {:else}
      <div class="flex items-baseline gap-3 mb-2 flex-wrap">
        <div class="text-lg font-semibold tabular-nums">
          {md5Status.hashed} / {md5Status.total}
        </div>
        <div class="text-sm text-base-content/70">{md5Percent}% hashed</div>
        {#if md5Status.pending > 0}
          <div class="text-sm text-warning">{md5Status.pending} pending</div>
        {:else}
          <div class="text-sm text-success">All caught up</div>
        {/if}
        {#if md5Status.failed > 0}
          <div class="text-sm text-error">{md5Status.failed} failed</div>
        {/if}
      </div>
      <progress
        class="progress progress-primary w-full"
        value={md5Status.hashed + md5Status.failed}
        max={md5Status.total}
      ></progress>

      <!-- "Now processing" row is always rendered so the card height
           is stable across worker idle/active transitions. When the
           worker has nothing in flight, the spinner + Skip button
           reserve their footprint via `invisible` and the file slot
           shows the "No File Processing" placeholder. The byte
           progress sub-row is conditional — it only ever appears
           while a file is in flight, so its appearance/disappearance
           reflects real work, not idle reflow. -->
      <div class="mt-4 p-3 bg-base-200 rounded space-y-2">
        <div class="flex items-center gap-2">
          {#if md5Status.currentFileName}
            <span class="loading loading-spinner loading-xs"></span>
            <span class="text-sm font-medium truncate flex-1" title={md5Status.currentFilePath ?? ''}>
              {md5Status.currentFileName}
            </span>
            <button
              type="button"
              class="btn btn-xs btn-soft btn-error border border-error/50"
              onclick={skipCurrentMd5}
              disabled={md5SkipBusy}
              title="Mark this video as Md5Failed and move on"
            >
              {#if md5SkipBusy}<span class="loading loading-spinner loading-xs"></span>{/if}
              Skip
            </button>
          {:else}
            <span class="loading loading-spinner loading-xs invisible" aria-hidden="true"></span>
            <span class="text-sm italic text-base-content/50 flex-1">No File Processing</span>
            <span class="btn btn-xs btn-soft btn-error border border-error/50 invisible" aria-hidden="true">Skip</span>
          {/if}
        </div>
        {#if md5Status.currentFileName}
          {#if md5Status.totalBytes > 0}
            <progress
              class="progress progress-accent w-full"
              value={md5Status.bytesProcessed}
              max={md5Status.totalBytes}
            ></progress>
            <div class="flex items-center justify-between text-xs text-base-content/70 tabular-nums">
              <span>{formatBytes(md5Status.bytesProcessed)} / {formatBytes(md5Status.totalBytes)}</span>
              <span>{md5FilePercent}%</span>
            </div>
          {:else}
            <div class="text-xs text-base-content/70">Starting...</div>
          {/if}
        {/if}
      </div>

      <!-- Queue preview moved to the "Show Queue" modal at the top of this
           card. Same data, but with FileName + FileSize columns + search. -->
    {/if}
  </li>
  {/snippet}

  <!-- Shared modal: drives all six "Show Failed" / "Show Queue" buttons.
       The same component instance handles every kind via modalConfig
       lookup so we don't render six near-duplicates. -->
  {#if openModal}
    {@const cfg = modalConfig[openModal]}
    <DataTableModal
      title={cfg.title}
      columns={cfg.columns}
      rows={modalRows}
      searchKeys={cfg.searchKeys}
      emptyText={cfg.emptyText}
      loading={modalLoading}
      error={modalError}
      onRefresh={refreshModal}
      onClose={closeModal}
    />
  {/if}
</div>

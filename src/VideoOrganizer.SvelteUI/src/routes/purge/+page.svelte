<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import type { Video } from '$lib/types';
  import {
    applySortClick,
    ariaSort,
    compareBySortStack,
    resizable,
    sortDir,
    sortPosition,
    loadColumnWidths,
    saveColumnWidths,
    type SortEntry,
  } from '$lib/tableUtils.svelte';

  let videos = $state<Video[]>([]);
  let loading = $state(false);
  let errorMessage = $state<string | null>(null);

  // Sort + column-width state shared across this table.
  type PurgeCol = 'file' | 'size';
  let sortStack = $state<SortEntry<PurgeCol>[]>([]);
  function onSortClick(col: PurgeCol, e: MouseEvent) {
    sortStack = applySortClick(sortStack, col, e.shiftKey);
  }

  const PURGE_WIDTHS_KEY = 'purge.list';
  let widths = $state<Record<string, number>>(loadColumnWidths(PURGE_WIDTHS_KEY, {
    preview: 176,
    file: 480,
    size: 96,
    actions: 224,
  }));
  // Reassign rather than mutate — Svelte 5's deep $state proxy *can*
  // track property writes, but only if every read sits in a directly
  // tracked spot in the template. Wrapping reads in a helper function
  // (getWidth) breaks that, so a write via `widths[col] = w` doesn't
  // re-trigger the colgroup. Reassigning the whole object always
  // invalidates dependents.
  function setWidth(col: string, w: number) {
    widths = { ...widths, [col]: w };
    saveColumnWidths(PURGE_WIDTHS_KEY, widths);
  }
  function getWidth(col: string, fallback: number): number {
    return widths[col] ?? fallback;
  }
  // Explicit pixel width for the table. See DataTableModal for why
  // `width: max-content` doesn't work: browsers compute it from cell
  // min-content, and an unbreakable wide cell pins its column open.
  const totalWidth = $derived(
    getWidth('preview', 176)
    + getWidth('file', 480)
    + getWidth('size', 96)
    + getWidth('actions', 224)
  );

  // Split into two sections by structural type:
  //   parentVideos — rows backed by a real file on disk; purging
  //                  deletes that file from the filesystem
  //   clipVideos   — rows whose file is shared with a parent;
  //                  purging only removes the DB row, never touches
  //                  the file (the parent still owns it)
  // Showing them in separate sections makes the consequence of Purge
  // visible at a glance — the user can see at a glance that the
  // "Clips" section will never reach the disk.
  const parentVideos = $derived(videos.filter(v => !v.parentVideoId));
  const clipVideos = $derived(videos.filter(v => !!v.parentVideoId));

  // Multi-column sort applied per section. The same sortStack drives
  // both tables so the user toggles a header once and both lists
  // reorder consistently.
  function sortList(list: Video[]): Video[] {
    if (sortStack.length === 0) return list;
    const cmp = compareBySortStack<Video, PurgeCol>(
      {
        file: (v) => v.fileName,
        size: (v) => v.fileSize,
      },
      sortStack
    );
    return [...list].sort(cmp);
  }
  const sortedParents = $derived(sortList(parentVideos));
  const sortedClips = $derived(sortList(clipVideos));

  // Busy map keyed by video id while its own Purge is in flight, so we can
  // show per-row spinners without freezing the whole list.
  let busyIds = $state<Set<string>>(new Set());
  let purgeAllBusy = $state(false);
  let restoreAllBusy = $state(false);

  // Results from the most recent Purge All, so the user can see the count
  // and any failures without digging into logs.
  let lastResult = $state<{ purged: number; failed: Array<{ fileName: string; error: string }> } | null>(null);

  // --- Bulk-operation progress modal ---------------------------------
  // Single modal drives both bulk ops: Purge All and Restore All. The
  // shape distinguishes "indeterminate" (server-side bulk endpoint
  // can't report per-file progress mid-flight) from "determinate"
  // (client-side per-file loop, increments done after each await).
  // The modal stays open after completion when there were failures so
  // the user can read what went wrong; otherwise auto-closes.
  type ProgressOp = 'purge' | 'restore';
  let progressModal = $state<{
    op: ProgressOp;
    done: number;
    total: number;
    currentFile: string | null;
    failed: Array<{ fileName: string; error: string }>;
    isComplete: boolean;
    // True after the user clicked Stop (loop terminated early).
    // Drives the "stopped early" hint on the summary so it's
    // distinguishable from "ran to completion".
    stopped: boolean;
  } | null>(null);

  // Pause / Stop state for the bulk loops below. Both bulk
  // operations run client-side per-file so pause/stop are real:
  // the loop polls these between iterations. Pause halts at the
  // next iteration boundary; Stop terminates the loop entirely.
  let paused = $state(false);
  let stopRequested = $state(false);

  function pauseLoop()  { paused = true; }
  function resumeLoop() { paused = false; }
  function stopLoop()   { stopRequested = true; paused = false; /* don't deadlock the wait loop */ }

  // Polled between iterations by the bulk loops. Resolves
  // immediately when not paused; otherwise busy-waits in 100ms
  // ticks until either resumed or stopped. Fine grain: a 100ms
  // delay is invisible to a human pressing Resume/Stop.
  async function waitWhilePaused() {
    while (paused && !stopRequested) {
      await new Promise(r => setTimeout(r, 100));
    }
  }

  function closeProgressModal() {
    progressModal = null;
    paused = false;
    stopRequested = false;
  }

  // Restore-All confirmation. Replaces window.confirm() so the
  // dialog matches the page's daisyUI style instead of falling out
  // of the design system as a system-native prompt. Scoped to a
  // section ('parents' or 'clips') so the button on each section
  // only restores its own list.
  type SectionKey = 'parents' | 'clips';
  let restoreConfirm = $state<{ section: SectionKey; list: Video[] } | null>(null);
  function openRestoreConfirm(section: SectionKey, list: Video[]) {
    if (list.length === 0 || restoreAllBusy || purgeAllBusy) return;
    restoreConfirm = { section, list };
  }
  function cancelRestoreConfirm() {
    restoreConfirm = null;
  }

  // Purge confirmation — same daisyUI-styled modal pattern as the
  // restore one. Replaces the system window.confirm() prompts so a
  // destructive action on this page never falls out of the design
  // system into a browser-chrome dialog. Two shapes:
  //   { kind: 'one' }  — single-row Purge button
  //   { kind: 'all' }  — section-level Purge All button
  // Both flow through onConfirmPurge() which dispatches by kind.
  type PurgeConfirm =
    | { kind: 'one'; video: Video }
    | { kind: 'all'; section: SectionKey; list: Video[] };
  let purgeConfirm = $state<PurgeConfirm | null>(null);
  function openPurgeOneConfirm(v: Video) {
    if (busyIds.has(v.id)) return;
    purgeConfirm = { kind: 'one', video: v };
  }
  function openPurgeAllConfirm(section: SectionKey) {
    const list = section === 'parents' ? parentVideos : clipVideos;
    if (list.length === 0 || purgeAllBusy || restoreAllBusy) return;
    purgeConfirm = { kind: 'all', section, list };
  }
  function cancelPurgeConfirm() {
    purgeConfirm = null;
  }
  async function onConfirmPurge() {
    const c = purgeConfirm;
    if (!c) return;
    purgeConfirm = null;
    if (c.kind === 'one') {
      await purgeOne(c.video);
    } else {
      await runPurgeAll(c.section);
    }
  }

  async function load() {
    loading = true;
    errorMessage = null;
    try {
      videos = await api.getMarkedForDeletion();
    } catch (e) {
      errorMessage = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
  }

  onMount(load);

  function formatBytes(bytes: number): string {
    if (!bytes || bytes <= 0) return '';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0;
    let n = bytes;
    while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
    return `${n.toFixed(n >= 10 || i === 0 ? 0 : 1)} ${units[i]}`;
  }

  const parentBytes = $derived(parentVideos.reduce((sum, v) => sum + (v.fileSize || 0), 0));
  // Clip rows carry the parent's fileSize for display purposes, but
  // purging a clip doesn't reclaim that disk space — the parent
  // still owns the file. Keep the total for context but don't claim
  // it in any "you'll free up X" copy.
  const clipsTotalBytes = $derived(clipVideos.reduce((sum, v) => sum + (v.fileSize || 0), 0));

  // Single-row purge worker. Confirmation is handled by the styled
  // modal in the template (see openPurgeOneConfirm + onConfirmPurge);
  // this just performs the API call once the user has confirmed.
  async function purgeOne(v: Video) {
    if (busyIds.has(v.id)) return;
    busyIds.add(v.id);
    busyIds = new Set(busyIds);
    try {
      await api.purgeVideo(v.id);
      videos = videos.filter(x => x.id !== v.id);
    } catch (e) {
      errorMessage = `Failed to purge ${v.fileName}: ${e instanceof Error ? e.message : String(e)}`;
    } finally {
      busyIds.delete(v.id);
      busyIds = new Set(busyIds);
    }
  }

  async function restore(v: Video) {
    if (busyIds.has(v.id)) return;
    busyIds.add(v.id);
    busyIds = new Set(busyIds);
    try {
      await api.unmarkForDeletion(v.id);
      videos = videos.filter(x => x.id !== v.id);
    } catch (e) {
      errorMessage = `Failed to restore ${v.fileName}: ${e instanceof Error ? e.message : String(e)}`;
    } finally {
      busyIds.delete(v.id);
      busyIds = new Set(busyIds);
    }
  }

  // Bulk purge worker. Confirmation is handled by the styled modal
  // in the template (see openPurgeAllConfirm + onConfirmPurge); this
  // just runs the loop once the user has confirmed.
  async function runPurgeAll(section: SectionKey) {
    const list = section === 'parents' ? parentVideos : clipVideos;
    if (list.length === 0 || purgeAllBusy) return;
    purgeAllBusy = true;
    errorMessage = null;
    lastResult = null;
    // Client-side per-file loop (replaces the prior server-side
    // bulk endpoint call) so the user can Pause / Stop mid-run.
    // Parents are sorted to the front because the server cascade-
    // deletes clip rows when their parent is purged — sending a
    // clip after its parent has already been cascaded would 404,
    // which we'd then have to filter out. Sorting keeps the loop's
    // success/failure counts honest. (Within a single-section purge
    // this is mostly a no-op since each section is homogeneous, but
    // the order is stable when both sections happen to overlap.)
    const sorted = [...list].sort(
      (a, b) => Number(!!a.parentVideoId) - Number(!!b.parentVideoId)
    );
    paused = false;
    stopRequested = false;
    progressModal = {
      op: 'purge',
      done: 0,
      total: sorted.length,
      currentFile: null,
      failed: [],
      isComplete: false,
      stopped: false
    };
    for (const v of sorted) {
      await waitWhilePaused();
      if (stopRequested) break;
      progressModal = { ...progressModal, currentFile: v.fileName };
      try {
        await api.purgeVideo(v.id);
        videos = videos.filter(x => x.id !== v.id);
        progressModal = { ...progressModal, done: progressModal.done + 1 };
      } catch (e) {
        progressModal = {
          ...progressModal,
          done: progressModal.done + 1,
          failed: [
            ...progressModal.failed,
            { fileName: v.fileName, error: e instanceof Error ? e.message : String(e) }
          ]
        };
      }
    }
    progressModal = {
      ...progressModal,
      isComplete: true,
      currentFile: null,
      stopped: stopRequested
    };
    lastResult = {
      purged: progressModal.done - progressModal.failed.length,
      failed: progressModal.failed
    };
    purgeAllBusy = false;
    // Modal stays open until the user dismisses it via Esc or the
    // Close button.
  }

  // Invoked by the styled confirmation modal's "Restore" button.
  // The Restore-All button on each section just opens the
  // confirmation; this is where the actual loop runs.
  async function confirmRestoreAll() {
    if (!restoreConfirm || restoreAllBusy) return;
    const list = [...restoreConfirm.list];
    restoreConfirm = null;
    if (list.length === 0) return;
    restoreAllBusy = true;
    errorMessage = null;
    // No bulk-restore endpoint exists; loop client-side. Snapshot
    // the list before mutating `videos` so we keep iterating after
    // each removal. Determinate progress: increment `done` after
    // every await so the bar tracks real per-file completion.
    // Pause/Stop are polled between iterations.
    paused = false;
    stopRequested = false;
    progressModal = {
      op: 'restore',
      done: 0,
      total: list.length,
      currentFile: null,
      failed: [],
      isComplete: false,
      stopped: false
    };
    for (const v of list) {
      await waitWhilePaused();
      if (stopRequested) break;
      progressModal = { ...progressModal, currentFile: v.fileName };
      try {
        await api.unmarkForDeletion(v.id);
        videos = videos.filter(x => x.id !== v.id);
        progressModal = { ...progressModal, done: progressModal.done + 1 };
      } catch (e) {
        progressModal = {
          ...progressModal,
          done: progressModal.done + 1,
          failed: [
            ...progressModal.failed,
            { fileName: v.fileName, error: e instanceof Error ? e.message : String(e) }
          ]
        };
      }
    }
    progressModal = {
      ...progressModal,
      isComplete: true,
      currentFile: null,
      stopped: stopRequested
    };
    restoreAllBusy = false;
    // Modal stays open after completion — user dismisses via Esc
    // or the Close button (so success summaries / failure lists
    // remain readable).
  }

  // Escape dismisses any open modal:
  //   - confirm prompts (purge / restore) → cancel without acting
  //   - progress modal → only after the operation completes (so a
  //     stray key during a long run can't abort it)
  // Window-level so the key works regardless of where focus is.
  function onWindowKeyDown(e: KeyboardEvent) {
    if (e.key !== 'Escape') return;
    if (purgeConfirm) {
      e.preventDefault();
      purgeConfirm = null;
      return;
    }
    if (restoreConfirm) {
      e.preventDefault();
      restoreConfirm = null;
      return;
    }
    if (progressModal?.isComplete) {
      e.preventDefault();
      progressModal = null;
    }
  }
</script>

<svelte:window onkeydown={onWindowKeyDown} />

<!-- Reused for both Files and Clips sections so they share table
     styling, sort headers, and column-resize. Takes the row list
     plus a flag that drives section-aware copy on the action
     buttons (e.g. "Purge" tooltip notes that clips don't delete the
     file). -->
{#snippet purgeTable(rows: Video[], isClipsSection: boolean)}
  <div class="overflow-x-auto border border-base-300 rounded">
    <!-- Drop min-width:100% — with table-layout:fixed and explicit
         col widths summing < container, the browser redistributes the
         leftover space across columns, which made col 1 appear to not
         shrink (the freed space was being given right back to it).
         Plain `width: max-content` makes the table = exactly sum-of-cols
         so each drag affects only the column being dragged. -->
    <table class="table table-sm resizable-table" style="table-layout: fixed; width: {totalWidth}px;">
      <colgroup>
        <col style="width: {getWidth('preview', 176)}px" />
        <col style="width: {getWidth('file', 480)}px" />
        <col style="width: {getWidth('size', 96)}px" />
        <col style="width: {getWidth('actions', 224)}px" />
      </colgroup>
      <thead>
        <tr>
          <!-- Preview: not sortable, but resizable so users can shrink
               the thumbnail column when they want a denser list. -->
          <th class="relative select-none" style="width: {getWidth('preview', 176)}px;">
            Preview
            <button
              type="button"
              aria-label="Resize Preview (double-click to auto-fit)"
              class="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/40 active:bg-primary/60 z-10"
              use:resizable={{
                getWidth: () => getWidth('preview', 176),
                setWidth: (w) => setWidth('preview', w),
              }}
            ></button>
          </th>
          {#each [
            { key: 'file' as const, label: 'File', align: 'left' as const, def: 480 },
            { key: 'size' as const, label: 'Size', align: 'right' as const, def: 96 },
          ] as col (col.key)}
            <th
              class="relative select-none p-0 {col.align === 'right' ? 'text-right' : ''}"
              style="width: {getWidth(col.key, col.def)}px;"
              aria-sort={ariaSort(sortStack, col.key)}
            >
              <button
                type="button"
                class="w-full px-3 py-2 hover:bg-base-200 cursor-pointer flex items-center gap-1 {col.align === 'right' ? 'justify-end' : ''}"
                onclick={(e) => onSortClick(col.key, e)}
                title="Click to sort. Shift-click for multi-column sort. Double-click the right edge to auto-fit."
              >
                <span class="overflow-hidden text-ellipsis {col.align === 'left' ? 'flex-1 text-left' : ''}">{col.label}</span>
                {#if sortDir(sortStack, col.key)}
                  <span class="text-[10px] tabular-nums text-base-content/60">
                    {sortDir(sortStack, col.key) === 'asc' ? '▲' : '▼'}{sortPosition(sortStack, col.key) > 1 ? sortPosition(sortStack, col.key) : ''}
                  </span>
                {/if}
              </button>
              <button
                type="button"
                aria-label={`Resize ${col.label} (double-click to auto-fit)`}
                class="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/40 active:bg-primary/60 z-10"
                use:resizable={{
                  getWidth: () => getWidth(col.key, col.def),
                  setWidth: (w) => setWidth(col.key, w),
                }}
              ></button>
            </th>
          {/each}
          <th class="relative select-none text-right" style="width: {getWidth('actions', 224)}px;">
            Actions
            <button
              type="button"
              aria-label="Resize Actions (double-click to auto-fit)"
              class="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/40 active:bg-primary/60 z-10"
              use:resizable={{
                getWidth: () => getWidth('actions', 224),
                setWidth: (w) => setWidth('actions', w),
              }}
            ></button>
          </th>
        </tr>
      </thead>
      <tbody>
        {#each rows as v (v.id)}
          {@const busy = busyIds.has(v.id)}
          <tr>
            <!-- w-full max-w-40 so the thumbnail tracks the column's
                 actual rendered width (it caps at 160px, the natural
                 preview size, but shrinks when the user drags the
                 Preview column smaller). overflow-hidden on the cell
                 prevents long content from visually defeating the
                 resize. -->
            <td class="align-top overflow-hidden">
              <div class="w-full max-w-40 aspect-video bg-base-300 rounded overflow-hidden">
                <img
                  src={api.posterUrl(v.id)}
                  loading="lazy"
                  alt=""
                  class="w-full h-full object-cover"
                  onerror={(e) => ((e.currentTarget as HTMLImageElement).style.visibility = 'hidden')}
                />
              </div>
            </td>
            <td class="align-top">
              <div class="font-medium break-all">{v.fileName}</div>
              <div class="text-xs text-base-content/60 break-all">{v.filePath}</div>
              {#if isClipsSection && v.clipStartSeconds !== null && v.clipEndSeconds !== null}
                <!-- Surface the clip's range so the user can identify
                     which slice of the parent it represents without
                     opening the player. -->
                <div class="text-xs text-base-content/50 tabular-nums mt-0.5">
                  {v.clipStartSeconds.toFixed(1)}s – {v.clipEndSeconds.toFixed(1)}s
                </div>
              {/if}
              {#if v.tags.length > 0}
                <div class="mt-1 flex flex-wrap gap-1">
                  {#each v.tags as t (t.id)}
                    <span class="badge badge-accent badge-xs" title="{t.tagGroupName}">{t.name}</span>
                  {/each}
                </div>
              {/if}
            </td>
            <td class="align-top text-right tabular-nums text-sm whitespace-nowrap">
              {formatBytes(v.fileSize)}
            </td>
            <td class="align-top text-right whitespace-nowrap">
              {#if !isClipsSection}
                <a
                  class="btn btn-xs btn-soft btn-primary btn-cta"
                  href={api.streamUrl(v.id)}
                  download={v.fileName}
                  title="Download the video file before purging"
                >
                  Download
                </a>
              {/if}
              <button
                type="button"
                class="btn btn-xs btn-cancel ml-1"
                onclick={() => restore(v)}
                disabled={busy}
                title={isClipsSection
                  ? 'Clear the marked-for-deletion flag on this clip row'
                  : 'Unmark for deletion and move the file back to its original location'}
              >
                Restore
              </button>
              <button
                type="button"
                class="btn btn-xs btn-soft btn-error border border-error/50 ml-1"
                onclick={() => openPurgeOneConfirm(v)}
                disabled={busy}
                title={isClipsSection
                  ? 'Remove this clip row from the database. The parent file is not touched.'
                  : 'Delete the file from disk and remove its database record. Permanent.'}
              >
                {#if busy}<span class="loading loading-spinner loading-xs"></span>{/if}
                Purge
              </button>
            </td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
{/snippet}

<div class="max-w-6xl mx-auto">
  <div class="flex items-start justify-between gap-4 mb-4">
    <div>
      <h1 class="text-2xl font-semibold">Purge Deleted</h1>
      <p class="text-sm text-base-content/70 mt-1">
        Videos and clips marked for deletion. Each section purges
        independently — files delete from disk; clips only remove
        their database row, never the underlying file.
      </p>
    </div>
    <div class="flex gap-2 shrink-0">
      <button type="button" class="btn btn-sm" onclick={load} disabled={loading}>
        {#if loading}<span class="loading loading-spinner loading-xs"></span>{/if}
        Refresh
      </button>
    </div>
  </div>

  {#if errorMessage}
    <div class="alert alert-error mb-4 text-sm">{errorMessage}</div>
  {/if}

  {#if lastResult}
    <div class="alert {lastResult.failed.length === 0 ? 'alert-success' : 'alert-warning'} mb-4 text-sm">
      <div>
        <div>Purged {lastResult.purged} item{lastResult.purged === 1 ? '' : 's'}.</div>
        {#if lastResult.failed.length > 0}
          <ul class="mt-2 list-disc ml-5 space-y-0.5">
            {#each lastResult.failed as f, i (i)}
              <li class="break-all"><strong>{f.fileName}:</strong> {f.error}</li>
            {/each}
          </ul>
        {/if}
      </div>
    </div>
  {/if}

  {#if !loading && videos.length === 0}
    <div class="text-center py-16 text-base-content/60">
      Nothing marked for deletion.
    </div>
  {:else}
    <!-- ============================ Files (parents) ============================ -->
    {#if parentVideos.length > 0}
      <section class="mb-8">
        <div class="flex items-baseline justify-between gap-4 mb-2">
          <div>
            <h2 class="text-lg font-semibold">Files</h2>
            <p class="text-xs text-base-content/60 tabular-nums">
              {parentVideos.length} file{parentVideos.length === 1 ? '' : 's'}
              · {formatBytes(parentBytes)} total
              · purging deletes the file from disk
            </p>
          </div>
          <div class="flex gap-2 shrink-0">
            <!-- Restore All sits to the LEFT of Purge All so the safer
                 action is the closer-to-finger default — Purge is on
                 the outside in error-tinted styling, Restore in the
                 cancel/neutral palette. -->
            <button
              type="button"
              class="btn btn-sm btn-cancel"
              onclick={() => openRestoreConfirm('parents', parentVideos)}
              disabled={restoreAllBusy || purgeAllBusy}
              title="Move every staged file back to its original location and clear the marked-for-deletion flag"
            >
              {#if restoreAllBusy}<span class="loading loading-spinner loading-xs"></span>{/if}
              Restore All ({parentVideos.length})
            </button>
            <button
              type="button"
              class="btn btn-sm btn-soft btn-error border border-error/50"
              onclick={() => openPurgeAllConfirm('parents')}
              disabled={purgeAllBusy || restoreAllBusy}
            >
              {#if purgeAllBusy}<span class="loading loading-spinner loading-xs"></span>{/if}
              Purge All ({parentVideos.length})
            </button>
          </div>
        </div>
        {@render purgeTable(sortedParents, false)}
      </section>
    {/if}

    <!-- ============================ Clips ============================ -->
    {#if clipVideos.length > 0}
      <section>
        <div class="flex items-baseline justify-between gap-4 mb-2">
          <div>
            <h2 class="text-lg font-semibold">Clips</h2>
            <p class="text-xs text-base-content/60 tabular-nums">
              {clipVideos.length} clip{clipVideos.length === 1 ? '' : 's'}
              · purging removes only the database row — the parent
              file stays on disk
            </p>
          </div>
          <div class="flex gap-2 shrink-0">
            <button
              type="button"
              class="btn btn-sm btn-cancel"
              onclick={() => openRestoreConfirm('clips', clipVideos)}
              disabled={restoreAllBusy || purgeAllBusy}
              title="Clear the marked-for-deletion flag on every clip below"
            >
              {#if restoreAllBusy}<span class="loading loading-spinner loading-xs"></span>{/if}
              Restore All ({clipVideos.length})
            </button>
            <button
              type="button"
              class="btn btn-sm btn-soft btn-error border border-error/50"
              onclick={() => openPurgeAllConfirm('clips')}
              disabled={purgeAllBusy || restoreAllBusy}
              title="Remove every clip row below. Parent video files are never touched."
            >
              {#if purgeAllBusy}<span class="loading loading-spinner loading-xs"></span>{/if}
              Purge All ({clipVideos.length})
            </button>
          </div>
        </div>
        {@render purgeTable(sortedClips, true)}
      </section>
    {/if}
  {/if}
</div>

<!-- Restore All confirmation. Custom daisyUI modal matching the
     page's design system — the previous window.confirm() prompt
     dropped out of the styling and felt like a browser dialog.
     Now scoped to a section so the copy reflects "files back to
     disk" vs "clear flag on clip rows". -->
{#if restoreConfirm !== null}
  {@const isParents = restoreConfirm.section === 'parents'}
  {@const count = restoreConfirm.list.length}
  <div class="modal modal-open" role="dialog" aria-modal="true" aria-labelledby="restore-confirm-title">
    <div class="modal-box">
      <h3 id="restore-confirm-title" class="font-bold text-lg">
        Restore {isParents ? 'all files' : 'all clips'}?
      </h3>
      <p class="mt-3 text-sm text-base-content/80">
        {#if isParents}
          Move <span class="font-semibold tabular-nums">{count}</span>
          marked-for-deletion file{count === 1 ? '' : 's'} back to their
          original location{count === 1 ? '' : 's'} and clear the deletion flag?
        {:else}
          Clear the marked-for-deletion flag on
          <span class="font-semibold tabular-nums">{count}</span>
          clip row{count === 1 ? '' : 's'}? No files are moved — clips
          share their parent's file.
        {/if}
      </p>
      <p class="mt-2 text-xs text-base-content/60">
        Each {isParents ? 'file' : 'clip'} is restored individually — progress will appear in a follow-up dialog.
      </p>
      <div class="modal-action">
        <button
          type="button"
          class="btn btn-sm btn-cancel"
          onclick={cancelRestoreConfirm}
        >Cancel</button>
        <button
          type="button"
          class="btn btn-sm btn-soft btn-primary btn-cta"
          onclick={confirmRestoreAll}
        >Restore All</button>
      </div>
    </div>
    <!-- Backdrop click cancels — same affordance as the Cancel
         button. Rendered as a <button> so screen readers and the
         keyboard pick it up natively, no svelte-ignore needed. -->
    <button
      type="button"
      class="modal-backdrop"
      aria-label="Cancel restore all"
      onclick={cancelRestoreConfirm}
    ></button>
  </div>
{/if}

<!-- Purge confirmation. Same daisyUI-styled pattern as the Restore
     modal above — replaces the system window.confirm() prompts so a
     destructive action on this page never falls out of the design
     system. Handles both the per-row Purge button (kind='one') and
     the per-section Purge All button (kind='all'); the copy and the
     primary button label adapt by kind + section. -->
{#if purgeConfirm !== null}
  {@const c = purgeConfirm}
  {@const isClipsCtx = c.kind === 'one'
    ? !!c.video.parentVideoId
    : c.section === 'clips'}
  <div class="modal modal-open" role="dialog" aria-modal="true" aria-labelledby="purge-confirm-title">
    <div class="modal-box">
      <h3 id="purge-confirm-title" class="font-bold text-lg">
        {#if c.kind === 'one'}
          {isClipsCtx ? 'Purge this clip?' : 'Purge this file?'}
        {:else}
          {isClipsCtx ? 'Purge all clips?' : 'Purge all files?'}
        {/if}
      </h3>

      {#if c.kind === 'one'}
        <!-- Single-row purge: show the file name + path so the user
             can verify they're about to destroy the right thing. -->
        <div class="mt-3 text-sm">
          <div class="font-medium break-all">{c.video.fileName}</div>
          <div class="text-xs text-base-content/60 break-all">{c.video.filePath}</div>
          {#if isClipsCtx && c.video.clipStartSeconds !== null && c.video.clipEndSeconds !== null}
            <div class="text-xs text-base-content/50 tabular-nums mt-0.5">
              {c.video.clipStartSeconds.toFixed(1)}s – {c.video.clipEndSeconds.toFixed(1)}s
            </div>
          {/if}
        </div>
        <p class="mt-3 text-sm text-base-content/80">
          {#if isClipsCtx}
            Remove this clip's database row? The parent video file is
            <span class="font-semibold">not</span> touched — only the
            clip metadata goes away.
          {:else}
            Permanently delete this file from disk
            <span class="font-semibold">and</span> remove its database
            record?
          {/if}
        </p>
      {:else}
        <!-- Bulk purge: show count and (for files) total size, then
             a one-line consequence statement. -->
        <p class="mt-3 text-sm text-base-content/80">
          {#if isClipsCtx}
            Remove
            <span class="font-semibold tabular-nums">{c.list.length}</span>
            clip row{c.list.length === 1 ? '' : 's'} from the database?
            Parent video files are
            <span class="font-semibold">not</span> touched.
          {:else}
            Permanently delete
            <span class="font-semibold tabular-nums">{c.list.length}</span>
            file{c.list.length === 1 ? '' : 's'} from disk
            <span class="font-semibold">and</span> remove
            {c.list.length === 1 ? 'its' : 'their'} database
            record{c.list.length === 1 ? '' : 's'}?
          {/if}
        </p>
        {#if !isClipsCtx}
          <p class="mt-1 text-xs text-base-content/60">
            Total size on disk: <span class="tabular-nums">{formatBytes(parentBytes)}</span>
          </p>
        {/if}
        <p class="mt-2 text-xs text-base-content/60">
          Each {isClipsCtx ? 'clip' : 'file'} is purged individually — progress will appear in a follow-up dialog.
        </p>
      {/if}

      {#if !isClipsCtx}
        <p class="mt-3 text-sm text-error font-semibold">This cannot be undone.</p>
      {/if}

      <!-- Initial focus lands on Cancel rather than the destructive
           Purge button — that way an accidental Enter on the modal
           dismisses, not destroys. The user has to deliberately Tab
           or click to confirm. -->
      <div class="modal-action">
        <!-- svelte-ignore a11y_autofocus -->
        <button
          type="button"
          class="btn btn-sm btn-cancel"
          onclick={cancelPurgeConfirm}
          autofocus
        >Cancel</button>
        <button
          type="button"
          class="btn btn-sm btn-soft btn-error border border-error/50"
          onclick={onConfirmPurge}
        >
          {c.kind === 'one' ? 'Purge' : `Purge All (${c.list.length})`}
        </button>
      </div>
    </div>
    <!-- Backdrop click cancels — same affordance as the Cancel
         button. Destructive primary button is intentionally NOT the
         backdrop default (a stray click outside the modal should
         dismiss, not destroy). -->
    <button
      type="button"
      class="modal-backdrop"
      aria-label="Cancel purge"
      onclick={cancelPurgeConfirm}
    ></button>
  </div>
{/if}

<!-- Bulk-operation progress modal. Both Purge All and Restore All
     run as client-side per-file loops so we get determinate
     progress and Pause / Stop semantics that actually do something.
     Stays open until dismissed via Esc or Close so success
     summaries (and any failure list) remain readable. Pause and
     Stop are non-closing actions: they affect the loop, not the
     modal's visibility. -->
{#if progressModal !== null}
  {@const m = progressModal}
  <div class="modal modal-open" role="dialog" aria-modal="true" aria-labelledby="purge-progress-title">
    <div class="modal-box">
      <h3 id="purge-progress-title" class="font-bold text-lg">
        {m.op === 'purge' ? 'Purging videos' : 'Restoring videos'}
        {#if paused && !m.isComplete}
          <span class="badge badge-sm badge-warning ml-2 align-middle">Paused</span>
        {/if}
      </h3>
      <div class="mt-4 space-y-3">
        <progress
          class="progress progress-primary w-full"
          value={m.done}
          max={m.total}
        ></progress>
        <div class="text-sm tabular-nums">
          {m.done} / {m.total}
          {#if m.failed.length > 0}
            · <span class="text-error">{m.failed.length} failed</span>
          {/if}
          {#if m.isComplete && m.stopped}
            · <span class="italic text-base-content/60">stopped early</span>
          {/if}
        </div>
        {#if m.currentFile && !m.isComplete}
          <div class="text-xs text-base-content/60 break-all truncate" title={m.currentFile}>
            {m.currentFile}
          </div>
        {/if}
        {#if m.isComplete && m.failed.length > 0}
          <div class="alert alert-warning text-sm mt-2">
            <div>
              <div class="font-semibold mb-1">{m.failed.length} {m.op === 'purge' ? 'purge' : 'restore'} failure{m.failed.length === 1 ? '' : 's'}:</div>
              <ul class="list-disc ml-5 space-y-0.5 max-h-40 overflow-auto">
                {#each m.failed as f, i (i)}
                  <li class="break-all"><strong>{f.fileName}:</strong> {f.error}</li>
                {/each}
              </ul>
            </div>
          </div>
        {/if}
      </div>
      <!-- Action row. While running: Pause/Resume + Stop (both
           non-closing — they affect the loop only). Once complete:
           Close. The Close button is intentionally only available
           after isComplete so the user can't dismiss mid-run and
           lose track of what's still happening. -->
      <div class="modal-action">
        {#if !m.isComplete}
          {#if paused}
            <button type="button" class="btn btn-sm btn-soft btn-primary" onclick={resumeLoop}>Resume</button>
          {:else}
            <button type="button" class="btn btn-sm btn-cancel" onclick={pauseLoop}>Pause</button>
          {/if}
          <button
            type="button"
            class="btn btn-sm btn-soft btn-error border border-error/50"
            onclick={stopLoop}
            disabled={stopRequested}
            title="Stop after the current file finishes; whatever has been processed so far stays processed"
          >Stop</button>
        {:else}
          <button type="button" class="btn btn-sm" onclick={closeProgressModal}>Close</button>
        {/if}
      </div>
    </div>
    <!-- Backdrop is non-interactive — Pause / Stop / Close drive the
         dismissal; clicking outside the modal during a long operation
         shouldn't accidentally close it. -->
    <div class="modal-backdrop"></div>
  </div>
{/if}

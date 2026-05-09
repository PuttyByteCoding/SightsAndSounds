<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import type { Video, FfprobeResult } from '$lib/types';
  import { runtimeStore } from '$lib/runtimeStore.svelte';
  import FfprobeResultModal from '$lib/components/FfprobeResultModal.svelte';
  import RemoteHostBanner from '$lib/components/RemoteHostBanner.svelte';
  import {
    applySortClick,
    compareBySortStack,
    sortDir,
    sortPosition,
    loadColumnWidths,
    saveColumnWidths,
    startColumnResize,
    type SortEntry,
  } from '$lib/tableUtils.svelte';

  // Triage page for videos flagged with PlaybackIssue. Mirrors /purge —
  // same column layout, sort + resize, busy map, bulk-progress modal,
  // styled confirmation modal — with these differences:
  //   · no Purge button (playback issues aren't terminal, the user
  //     has to decide whether to restore or escalate to mark-for-
  //     deletion before purging happens via the /purge page)
  //   · per-row "Delete" → mark-for-deletion (decision: file is bad)
  //   · per-row "Show in Folder" + "Diagnose" rendered when the
  //     browser is local, hidden when remote (endpoints would 403)
  //   · bulk: Restore All only (no bulk delete, since the
  //     per-row decision is intentional)

  let videos = $state<Video[]>([]);
  let loading = $state(false);
  let errorMessage = $state<string | null>(null);

  type IssuesCol = 'file' | 'size';
  let sortStack = $state<SortEntry<IssuesCol>[]>([]);
  function onSortClick(col: IssuesCol, e: MouseEvent) {
    sortStack = applySortClick(sortStack, col, e.shiftKey);
  }

  const WIDTHS_KEY = 'playbackIssues.list';
  let widths = $state<Record<string, number>>(loadColumnWidths(WIDTHS_KEY, {
    preview: 176,
    file: 480,
    size: 96,
    actions: 360,
  }));
  function setWidth(col: string, w: number) {
    widths = { ...widths, [col]: w };
    saveColumnWidths(WIDTHS_KEY, widths);
  }
  function getWidth(col: string, fallback: number): number {
    return widths[col] ?? fallback;
  }

  const sortedVideos = $derived.by(() => {
    if (sortStack.length === 0) return videos;
    const cmp = compareBySortStack<Video, IssuesCol>(
      {
        file: (v) => v.fileName,
        size: (v) => v.fileSize,
      },
      sortStack
    );
    return [...videos].sort(cmp);
  });

  // Per-row in-flight tracker → drives spinners on individual rows
  // without freezing the rest of the table.
  let busyIds = $state<Set<string>>(new Set());
  let restoreAllBusy = $state(false);
  let purgeAllBusy = $state(false);

  // --- Bulk-operation progress modal (same shape as /purge) ----------
  // Both Restore All and Purge All run as client-side per-file
  // loops so we get determinate progress + Pause / Stop semantics.
  // The loop polls `paused` between iterations and breaks on
  // `stopRequested`; the user dismisses via Esc or Close after
  // isComplete (so success summaries / failure lists stay readable).
  type ProgressOp = 'restore' | 'purge';
  let progressModal = $state<{
    op: ProgressOp;
    done: number;
    total: number;
    currentFile: string | null;
    failed: Array<{ fileName: string; error: string }>;
    isComplete: boolean;
    stopped: boolean;
  } | null>(null);

  // Pause / Stop state shared by both bulk loops below.
  let paused = $state(false);
  let stopRequested = $state(false);

  function pauseLoop()  { paused = true; }
  function resumeLoop() { paused = false; }
  function stopLoop()   { stopRequested = true; paused = false; }

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

  // --- Restore All confirmation -------------------------------------
  let restoreConfirmOpen = $state(false);
  function openRestoreConfirm() {
    if (videos.length === 0 || restoreAllBusy) return;
    restoreConfirmOpen = true;
  }
  function cancelRestoreConfirm() { restoreConfirmOpen = false; }

  // --- Purge All confirmation --------------------------------------
  // Bulk-deletes every playback-issue file from disk + DB in one
  // server call. More destructive than the per-row Delete (which
  // just queues for purge). Modal styled to match the rest of the
  // page; backdrop click cancels.
  let purgeConfirmOpen = $state(false);
  function openPurgeConfirm() {
    if (videos.length === 0 || purgeAllBusy || restoreAllBusy) return;
    purgeConfirmOpen = true;
  }
  function cancelPurgeConfirm() { purgeConfirmOpen = false; }

  // --- Per-row Delete (escalate to mark-for-deletion) confirmation -
  // Replaces window.confirm() with a page-styled daisyUI modal so the
  // dialog matches the rest of the design system. Holds the pending
  // video; null = closed. confirmDelete consumes the pending video
  // and runs the actual API call.
  let pendingDelete = $state<Video | null>(null);
  function openDeleteConfirm(v: Video) {
    if (busyIds.has(v.id)) return;
    pendingDelete = v;
  }
  function cancelDeleteConfirm() { pendingDelete = null; }

  // --- ffprobe diagnostic modal -------------------------------------
  // The modal markup + filter / pretty-print logic lives in the
  // shared FfprobeResultModal component. We only own the state.
  let ffprobeResult = $state<FfprobeResult | null>(null);

  async function load() {
    loading = true;
    errorMessage = null;
    try {
      videos = await api.getPlaybackIssues();
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

  const totalBytes = $derived(videos.reduce((sum, v) => sum + (v.fileSize || 0), 0));

  async function restore(v: Video) {
    if (busyIds.has(v.id)) return;
    busyIds.add(v.id);
    busyIds = new Set(busyIds);
    try {
      await api.unmarkPlaybackIssue(v.id);
      videos = videos.filter(x => x.id !== v.id);
    } catch (e) {
      errorMessage = `Failed to restore ${v.fileName}: ${e instanceof Error ? e.message : String(e)}`;
    } finally {
      busyIds.delete(v.id);
      busyIds = new Set(busyIds);
    }
  }

  // Escalate: user has reviewed the playback issue and decided the
  // file isn't worth keeping. Move to _ToDelete; actual purge still
  // happens on the /purge page so this stays a two-step destructive
  // action rather than a one-click landmine. Invoked by the styled
  // confirmation modal's Delete button — the row's Delete button on
  // the page just opens the modal via openDeleteConfirm.
  async function confirmDelete() {
    const v = pendingDelete;
    if (!v) return;
    pendingDelete = null;
    if (busyIds.has(v.id)) return;
    busyIds.add(v.id);
    busyIds = new Set(busyIds);
    try {
      // The mark-for-deletion endpoint sets MarkedForDeletion=true.
      // It also moves the file out of _PlaybackIssue/ into _ToDelete/
      // (server-side path math), so the row drops off this page.
      await api.markForDeletion(v.id);
      videos = videos.filter(x => x.id !== v.id);
    } catch (e) {
      errorMessage = `Failed to mark for deletion: ${e instanceof Error ? e.message : String(e)}`;
    } finally {
      busyIds.delete(v.id);
      busyIds = new Set(busyIds);
    }
  }

  async function revealRow(v: Video) {
    if (busyIds.has(v.id)) return;
    busyIds.add(v.id);
    busyIds = new Set(busyIds);
    try {
      await api.revealVideo(v.id);
    } catch (e) {
      errorMessage = `Failed to open folder for ${v.fileName}: ${e instanceof Error ? e.message : String(e)}`;
    } finally {
      busyIds.delete(v.id);
      busyIds = new Set(busyIds);
    }
  }

  async function diagnoseRow(v: Video) {
    if (busyIds.has(v.id)) return;
    busyIds.add(v.id);
    busyIds = new Set(busyIds);
    try {
      ffprobeResult = await api.ffprobeVideo(v.id);
    } catch (e) {
      errorMessage = `ffprobe failed for ${v.fileName}: ${e instanceof Error ? e.message : String(e)}`;
    } finally {
      busyIds.delete(v.id);
      busyIds = new Set(busyIds);
    }
  }

  async function confirmRestoreAll() {
    if (videos.length === 0 || restoreAllBusy) return;
    restoreConfirmOpen = false;
    restoreAllBusy = true;
    errorMessage = null;
    const list = [...videos];
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
        await api.unmarkPlaybackIssue(v.id);
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
  }

  async function confirmPurgeAll() {
    if (videos.length === 0 || purgeAllBusy) return;
    purgeConfirmOpen = false;
    purgeAllBusy = true;
    errorMessage = null;
    // Client-side per-file loop (same pattern as /purge#purgeAll).
    // The /api/videos/{id}/purge endpoint accepts PlaybackIssue=true
    // rows now, so we can hit it directly without staging through
    // mark-for-deletion first. Parents-first sort matches /purge.
    const list = [...videos].sort(
      (a, b) => Number(!!a.parentVideoId) - Number(!!b.parentVideoId)
    );
    paused = false;
    stopRequested = false;
    progressModal = {
      op: 'purge',
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
    purgeAllBusy = false;
  }

  // Escape closes the progress modal once the operation finishes.
  // Window-level so it works regardless of focus, guarded on
  // isComplete so we never abort an in-flight run on a stray key.
  function onWindowKeyDown(e: KeyboardEvent) {
    if (e.key === 'Escape' && progressModal?.isComplete) {
      e.preventDefault();
      progressModal = null;
    }
  }
</script>

<svelte:window onkeydown={onWindowKeyDown} />

<svelte:head><title>Playback Issues - Video Organizer</title></svelte:head>

<div class="max-w-6xl mx-auto">
  <!-- Local-only buttons (Show in Folder, Diagnose) on each row only
       render when the browser is on the API's host machine. The
       banner explains why they're missing if the user is remote. -->
  <RemoteHostBanner />
  <div class="flex items-start justify-between gap-4 mb-4">
    <div>
      <h1 class="text-2xl font-semibold">Playback Issues</h1>
      <p class="text-sm text-base-content/70 mt-1">
        Videos you've flagged as not playing cleanly in the browser. Files live under
        <code class="text-xs">&lt;set&gt;/_PlaybackIssue/</code> until you Restore them
        (move back, clear the flag) or escalate to Delete (move to the purge queue).
      </p>
    </div>
    <div class="flex gap-2 shrink-0">
      <button type="button" class="btn btn-sm" onclick={load} disabled={loading}>
        {#if loading}<span class="loading loading-spinner loading-xs"></span>{/if}
        Refresh
      </button>
      <!-- Restore (safe) on the inside, Purge (destructive) on the
           outside — same arrangement as /purge so the dangerous
           action is visually offset. -->
      <button
        type="button"
        class="btn btn-sm btn-cancel"
        onclick={openRestoreConfirm}
        disabled={restoreAllBusy || purgeAllBusy || videos.length === 0}
        title="Move every staged file back to its original location and clear the playback-issue flag"
      >
        {#if restoreAllBusy}<span class="loading loading-spinner loading-xs"></span>{/if}
        Restore All ({videos.length})
      </button>
      <button
        type="button"
        class="btn btn-sm btn-soft btn-error border border-error/50"
        onclick={openPurgeConfirm}
        disabled={purgeAllBusy || restoreAllBusy || videos.length === 0}
        title="Permanently delete every flagged file from disk and remove the database row — bypasses the To Delete queue"
      >
        {#if purgeAllBusy}<span class="loading loading-spinner loading-xs"></span>{/if}
        Purge All ({videos.length})
      </button>
    </div>
  </div>

  {#if errorMessage}
    <div class="alert alert-error mb-4 text-sm flex items-start gap-2">
      <span class="flex-1">{errorMessage}</span>
      <button class="btn btn-xs btn-ghost" onclick={() => (errorMessage = null)}>Dismiss</button>
    </div>
  {/if}

  {#if !loading && videos.length === 0}
    <div class="text-center py-16 text-base-content/60">
      No videos flagged with a playback issue.
    </div>
  {:else if videos.length > 0}
    <div class="text-xs text-base-content/60 mb-2 tabular-nums">
      {videos.length} video{videos.length === 1 ? '' : 's'} · {formatBytes(totalBytes)} total
    </div>
    <div class="overflow-x-auto border border-base-300 rounded">
      <table class="table table-sm" style="table-layout: fixed; width: max-content;">
        <colgroup>
          <col style="width: {getWidth('preview', 176)}px" />
          <col style="width: {getWidth('file', 480)}px" />
          <col style="width: {getWidth('size', 96)}px" />
          <col style="width: {getWidth('actions', 360)}px" />
        </colgroup>
        <thead>
          <tr>
            <th class="relative select-none">
              Preview
              <button
                type="button"
                aria-label="Resize Preview"
                class="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/40 active:bg-primary/60 z-10"
                onmousedown={(e) => startColumnResize(e, getWidth('preview', 176), (w) => setWidth('preview', w))}
              ></button>
            </th>
            {#each [
              { key: 'file' as const, label: 'File', align: 'left' as const, def: 480 },
              { key: 'size' as const, label: 'Size', align: 'right' as const, def: 96 },
            ] as col (col.key)}
              <th class="relative select-none p-0 {col.align === 'right' ? 'text-right' : ''}">
                <button
                  type="button"
                  class="w-full px-3 py-2 hover:bg-base-200 cursor-pointer flex items-center gap-1 {col.align === 'right' ? 'justify-end' : ''}"
                  onclick={(e) => onSortClick(col.key, e)}
                  title="Click to sort. Shift-click for multi-column sort."
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
                  aria-label={`Resize ${col.label}`}
                  class="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/40 active:bg-primary/60 z-10"
                  onmousedown={(e) => startColumnResize(e, getWidth(col.key, col.def), (w) => setWidth(col.key, w))}
                ></button>
              </th>
            {/each}
            <th class="relative select-none text-right">
              Actions
              <button
                type="button"
                aria-label="Resize Actions"
                class="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/40 active:bg-primary/60 z-10"
                onmousedown={(e) => startColumnResize(e, getWidth('actions', 360), (w) => setWidth('actions', w))}
              ></button>
            </th>
          </tr>
        </thead>
        <tbody>
          {#each sortedVideos as v (v.id)}
            {@const busy = busyIds.has(v.id)}
            <tr>
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
                <a
                  class="btn btn-xs btn-soft btn-primary btn-cta"
                  href={api.streamUrl(v.id)}
                  download={v.fileName}
                  title="Download the file before deciding what to do with it"
                >
                  Download
                </a>
                {#if runtimeStore.isLocal}
                  <button
                    type="button"
                    class="btn btn-xs btn-ghost ml-1"
                    onclick={() => revealRow(v)}
                    disabled={busy}
                    title="Reveal in the host file manager"
                  >Show in Folder</button>
                  <button
                    type="button"
                    class="btn btn-xs btn-ghost ml-1"
                    onclick={() => diagnoseRow(v)}
                    disabled={busy}
                    title="Run ffprobe and show codec / format / stream details"
                  >Diagnose</button>
                {/if}
                <button
                  type="button"
                  class="btn btn-xs btn-cancel ml-1"
                  onclick={() => restore(v)}
                  disabled={busy}
                  title="Clear the playback-issue flag and move the file back to its original location"
                >Restore</button>
                <button
                  type="button"
                  class="btn btn-xs btn-soft btn-error border border-error/50 ml-1"
                  onclick={() => openDeleteConfirm(v)}
                  disabled={busy}
                  title="Escalate: move the file to the purge queue instead of restoring it"
                >
                  {#if busy}<span class="loading loading-spinner loading-xs"></span>{/if}
                  Delete
                </button>
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  {/if}
</div>

<!-- Per-row Delete (escalate to mark-for-deletion) confirmation.
     Replaces a window.confirm() prompt so it matches the page's
     styling. Same skeleton as the Restore All dialog: cancel on the
     left, destructive primary action on the right, backdrop click
     also cancels. -->
{#if pendingDelete !== null}
  {@const v = pendingDelete}
  <div class="modal modal-open" role="dialog" aria-modal="true" aria-labelledby="delete-confirm-title">
    <div class="modal-box">
      <h3 id="delete-confirm-title" class="font-bold text-lg text-error">Move to deletion queue?</h3>
      <p class="mt-3 text-sm text-base-content/80 break-all">
        <span class="font-semibold">{v.fileName}</span>
      </p>
      <p class="mt-3 text-sm text-base-content/80">
        The file will be moved to <code class="text-xs bg-base-200 px-1 rounded">&lt;set&gt;/_ToDelete/</code>
        and listed on the Purge page. It is <em>not</em> deleted from disk yet — you can restore it from
        Purge, or run Purge All there to remove it permanently.
      </p>
      <div class="modal-action">
        <button type="button" class="btn btn-sm btn-cancel" onclick={cancelDeleteConfirm}>Cancel</button>
        <button type="button" class="btn btn-sm btn-soft btn-error border border-error/50" onclick={confirmDelete}>Move to Delete</button>
      </div>
    </div>
    <button
      type="button"
      class="modal-backdrop"
      aria-label="Cancel delete"
      onclick={cancelDeleteConfirm}
    ></button>
  </div>
{/if}

<!-- Purge All confirmation. Bulk-deletes every playback-issue
     file from disk + DB in one server call — destructive, bypasses
     the staging step that the per-row Delete uses. Styled with
     error-tinted heading + button so it visually matches the gravity
     of the action. -->
{#if purgeConfirmOpen}
  <div class="modal modal-open" role="dialog" aria-modal="true" aria-labelledby="purge-confirm-title">
    <div class="modal-box">
      <h3 id="purge-confirm-title" class="font-bold text-lg text-error">Purge all videos?</h3>
      <p class="mt-3 text-sm text-base-content/80">
        Permanently delete <span class="font-semibold tabular-nums">{videos.length}</span>
        playback-issue file{videos.length === 1 ? '' : 's'} from disk
        <em>and</em> remove the database row{videos.length === 1 ? '' : 's'}.
      </p>
      <p class="mt-2 text-xs text-base-content/60">
        Total size: <span class="tabular-nums">{formatBytes(totalBytes)}</span>. This cannot be undone.
        Use Restore All instead if you want to put the files back where they came from.
      </p>
      <div class="modal-action">
        <button type="button" class="btn btn-sm btn-cancel" onclick={cancelPurgeConfirm}>Cancel</button>
        <button type="button" class="btn btn-sm btn-soft btn-error border border-error/50" onclick={confirmPurgeAll}>Purge All</button>
      </div>
    </div>
    <button
      type="button"
      class="modal-backdrop"
      aria-label="Cancel purge all"
      onclick={cancelPurgeConfirm}
    ></button>
  </div>
{/if}

<!-- Restore All confirmation — same daisyUI pattern as /purge so the
     two triage pages feel like a matched pair. -->
{#if restoreConfirmOpen}
  <div class="modal modal-open" role="dialog" aria-modal="true" aria-labelledby="restore-confirm-title">
    <div class="modal-box">
      <h3 id="restore-confirm-title" class="font-bold text-lg">Restore all videos?</h3>
      <p class="mt-3 text-sm text-base-content/80">
        Move <span class="font-semibold tabular-nums">{videos.length}</span>
        playback-issue file{videos.length === 1 ? '' : 's'} back to their original locations
        and clear the flag?
      </p>
      <p class="mt-2 text-xs text-base-content/60">
        Each file is restored individually — progress will appear in a follow-up dialog.
      </p>
      <div class="modal-action">
        <button type="button" class="btn btn-sm btn-cancel" onclick={cancelRestoreConfirm}>Cancel</button>
        <button type="button" class="btn btn-sm btn-soft btn-primary btn-cta" onclick={confirmRestoreAll}>Restore All</button>
      </div>
    </div>
    <button
      type="button"
      class="modal-backdrop"
      aria-label="Cancel restore all"
      onclick={cancelRestoreConfirm}
    ></button>
  </div>
{/if}

<!-- Bulk Restore progress modal — determinate per-file progress
     since we loop client-side. Stays open after completion if any
     files failed so the user can read the per-file errors. -->
{#if progressModal !== null}
  {@const m = progressModal}
  <div class="modal modal-open" role="dialog" aria-modal="true" aria-labelledby="bulk-progress-title">
    <div class="modal-box">
      <h3 id="bulk-progress-title" class="font-bold text-lg">
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
      <!-- Pause / Stop while running (non-closing); Close once
           complete. Same arrangement as /purge so the two pages
           feel like a matched pair. -->
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
    <div class="modal-backdrop"></div>
  </div>
{/if}

<!-- Shared ffprobe diagnostic modal — same component the player
     overlay uses, so users get identical output (and the same line
     filter) regardless of where they invoked it. -->
<FfprobeResultModal bind:result={ffprobeResult} />

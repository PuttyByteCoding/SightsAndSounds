<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import type { Video } from '$lib/types';
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

  // Multi-column sort applied on top of the marked-for-deletion list.
  const sortedVideos = $derived.by(() => {
    if (sortStack.length === 0) return videos;
    const cmp = compareBySortStack<Video, PurgeCol>(
      {
        file: (v) => v.fileName,
        size: (v) => v.fileSize,
      },
      sortStack
    );
    return [...videos].sort(cmp);
  });

  // Busy map keyed by video id while its own Purge is in flight, so we can
  // show per-row spinners without freezing the whole list.
  let busyIds = $state<Set<string>>(new Set());
  let purgeAllBusy = $state(false);

  // Results from the most recent Purge All, so the user can see the count
  // and any failures without digging into logs.
  let lastResult = $state<{ purged: number; failed: Array<{ fileName: string; error: string }> } | null>(null);

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

  const totalBytes = $derived(videos.reduce((sum, v) => sum + (v.fileSize || 0), 0));

  async function purgeOne(v: Video) {
    if (busyIds.has(v.id)) return;
    const confirmMsg = `Permanently delete this file and remove it from the database?\n\n${v.fileName}\n${v.filePath}\n\nThis cannot be undone.`;
    if (!window.confirm(confirmMsg)) return;
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

  async function purgeAll() {
    if (videos.length === 0 || purgeAllBusy) return;
    const confirmMsg =
      `Permanently delete ${videos.length} file${videos.length === 1 ? '' : 's'} from disk and remove them from the database?\n\nTotal size: ${formatBytes(totalBytes)}\n\nThis cannot be undone.`;
    if (!window.confirm(confirmMsg)) return;
    purgeAllBusy = true;
    errorMessage = null;
    lastResult = null;
    try {
      const result = await api.purgeAllMarkedForDeletion();
      lastResult = {
        purged: result.purged,
        failed: result.failed.map(f => ({ fileName: f.fileName, error: f.error }))
      };
      await load();
    } catch (e) {
      errorMessage = e instanceof Error ? e.message : String(e);
    } finally {
      purgeAllBusy = false;
    }
  }
</script>

<div class="max-w-6xl mx-auto">
  <div class="flex items-start justify-between gap-4 mb-4">
    <div>
      <h1 class="text-2xl font-semibold">Purge Deleted</h1>
      <p class="text-sm text-base-content/70 mt-1">
        Videos marked for deletion. Purging removes the file from disk <em>and</em> the database record — this is permanent.
      </p>
    </div>
    <div class="flex gap-2 shrink-0">
      <button type="button" class="btn btn-sm" onclick={load} disabled={loading}>
        {#if loading}<span class="loading loading-spinner loading-xs"></span>{/if}
        Refresh
      </button>
      <button
        type="button"
        class="btn btn-sm btn-soft btn-error border border-error/50"
        onclick={purgeAll}
        disabled={purgeAllBusy || videos.length === 0}
      >
        {#if purgeAllBusy}<span class="loading loading-spinner loading-xs"></span>{/if}
        Purge All ({videos.length})
      </button>
    </div>
  </div>

  {#if errorMessage}
    <div class="alert alert-error mb-4 text-sm">{errorMessage}</div>
  {/if}

  {#if lastResult}
    <div class="alert {lastResult.failed.length === 0 ? 'alert-success' : 'alert-warning'} mb-4 text-sm">
      <div>
        <div>Purged {lastResult.purged} file{lastResult.purged === 1 ? '' : 's'}.</div>
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
  {:else if videos.length > 0}
    <div class="text-xs text-base-content/60 mb-2 tabular-nums">
      {videos.length} video{videos.length === 1 ? '' : 's'} · {formatBytes(totalBytes)} total
    </div>
    <div class="overflow-x-auto border border-base-300 rounded">
      <!-- Drop min-width:100% — with table-layout:fixed and explicit
           col widths summing < container, the browser redistributes the
           leftover space across columns, which made col 1 appear to not
           shrink (the freed space was being given right back to it).
           Plain `width: max-content` makes the table = exactly sum-of-cols
           so each drag affects only the column being dragged. -->
      <table class="table table-sm" style="table-layout: fixed; width: max-content;">
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
                onmousedown={(e) => startColumnResize(e, getWidth('actions', 224), (w) => setWidth('actions', w))}
              ></button>
            </th>
          </tr>
        </thead>
        <tbody>
          {#each sortedVideos as v (v.id)}
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
                  title="Download the video file before purging"
                >
                  Download
                </a>
                <button
                  type="button"
                  class="btn btn-xs btn-cancel ml-1"
                  onclick={() => restore(v)}
                  disabled={busy}
                  title="Unmark for deletion and move the file back to its original location"
                >
                  Restore
                </button>
                <button
                  type="button"
                  class="btn btn-xs btn-soft btn-error border border-error/50 ml-1"
                  onclick={() => purgeOne(v)}
                  disabled={busy}
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
  {/if}
</div>

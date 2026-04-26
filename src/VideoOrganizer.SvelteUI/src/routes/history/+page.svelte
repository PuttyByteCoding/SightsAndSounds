<script lang="ts">
  // Watch history view driven by Video.WatchCount — the API bumps this
  // counter after 10s of playback (see VideoPlayer's watch beacon). Shown
  // as a sortable table; clicking a row navigates to /browse to play.
  // "Shuffle evenly" kicks off an even-distribution playlist that
  // prioritises under-watched content.
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { api, ApiError } from '$lib/api';
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
  let loading = $state(true);
  let loadError = $state<string | null>(null);
  let shuffleBusy = $state(false);

  // Multi-column sort state. Default to plays desc — primary use case is
  // "what have I been watching most".
  type SortCol = 'plays' | 'title' | 'flags';
  let sortStack = $state<SortEntry<SortCol>[]>([{ col: 'plays', dir: 'desc' }]);
  let searchInput = $state('');

  // Column widths persisted to localStorage. Defaults match the previous
  // hard-coded `w-24 / w-40` Tailwind values.
  const WIDTHS_KEY = 'history.videos';
  let widths = $state<Record<string, number>>(loadColumnWidths(WIDTHS_KEY, {
    plays: 96,
    title: 480,
    flags: 160,
  }));
  // Reassign rather than mutate — see note in /purge: helper-wrapped
  // reads break $state's per-property dependency tracking.
  function setWidth(col: string, w: number) {
    widths = { ...widths, [col]: w };
    saveColumnWidths(WIDTHS_KEY, widths);
  }
  function getWidth(col: string, fallback: number): number {
    return widths[col] ?? fallback;
  }

  async function load() {
    loading = true;
    loadError = null;
    try {
      videos = await api.listVideosByTags({});
    } catch (e) {
      loadError = e instanceof ApiError || e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
  }

  // Flags column: a single comparable string of "Y/N" letters per flag,
  // so sorting by Flags clusters videos with similar flag combinations.
  function flagsComparable(v: Video): string {
    return [
      v.isClip ? 'C' : 'c',
      v.needsReview ? 'R' : 'r',
      v.wontPlay ? 'W' : 'w',
      v.markedForDeletion ? 'D' : 'd',
    ].join('');
  }

  const visible = $derived.by(() => {
    const q = searchInput.trim().toLowerCase();
    let out = videos;
    if (q) {
      out = out.filter((v) => v.fileName.toLowerCase().includes(q));
    }
    if (sortStack.length === 0) return out;
    const cmp = compareBySortStack<Video, SortCol>(
      {
        plays: (v) => v.watchCount,
        title: (v) => displayTitle(v),
        flags: (v) => flagsComparable(v),
      },
      sortStack
    );
    return [...out].sort(cmp);
  });

  function onSortClick(col: SortCol, e: MouseEvent) {
    sortStack = applySortClick(sortStack, col, e.shiftKey);
  }

  const stats = $derived.by(() => {
    if (videos.length === 0) return { played: 0, total: 0, sumPlays: 0 };
    let played = 0, sum = 0;
    for (const v of videos) {
      if (v.watchCount > 0) played++;
      sum += v.watchCount;
    }
    return { played, total: videos.length, sumPlays: sum };
  });

  function displayTitle(v: Video): string {
    return v.fileName;
  }

  function onOpen(v: Video) {
    goto(`/browse?id=${encodeURIComponent(v.id)}`);
  }

  async function shuffleEvenly() {
    shuffleBusy = true;
    try {
      // /browse reads ?mode=even and kicks off createEvenPlaylist on mount.
      await goto('/browse?mode=even');
    } finally {
      shuffleBusy = false;
    }
  }

  onMount(load);
</script>

<svelte:head><title>History</title></svelte:head>

<div class="space-y-3">
  <div class="flex items-center gap-3 flex-wrap sticky top-0 z-20 bg-base-100 py-2">
    <h1 class="text-2xl font-semibold">History</h1>
    <div class="text-sm text-base-content/60 tabular-nums">
      {stats.played} / {stats.total} watched · {stats.sumPlays} total plays
    </div>
    <button
      type="button"
      class="btn btn-sm btn-soft btn-primary btn-cta ml-auto"
      onclick={shuffleEvenly}
      disabled={shuffleBusy || videos.length === 0}
      title="Start a playlist that prioritizes under-watched videos"
    >
      {#if shuffleBusy}<span class="loading loading-spinner loading-xs"></span>{/if}
      Shuffle evenly
    </button>
    <button type="button" class="btn btn-sm btn-ghost" onclick={load} title="Reload">↻</button>
  </div>

  <div class="flex items-center gap-3 flex-wrap">
    <input
      class="input input-bordered flex-1 min-w-[16rem]"
      placeholder="Search title or filename..."
      bind:value={searchInput}
      autocomplete="off"
    />
    <span class="text-xs text-base-content/50 hidden md:inline">
      Click headers to sort. Shift-click for multi-column.
    </span>
  </div>

  {#if loadError}<div class="alert alert-error text-sm">{loadError}</div>{/if}

  {#if loading}
    <div class="flex items-center gap-2 text-base-content/70">
      <span class="loading loading-spinner"></span> Loading…
    </div>
  {:else if visible.length === 0}
    <div class="text-base-content/60 italic p-4">
      {videos.length === 0 ? 'No videos yet.' : 'No matches.'}
    </div>
  {:else}
    <div class="overflow-x-auto">
      <!-- table-layout:fixed lets the colgroup widths actually apply so
           the user can drag them. No min-width:100% — with sum-of-cols
           < container, that triggers redistribution and the leftmost
           column appears not to shrink. -->
      <table class="table table-zebra" style="table-layout: fixed; width: max-content;">
        <colgroup>
          <col style="width: {getWidth('plays', 96)}px" />
          <col style="width: {getWidth('title', 480)}px" />
          <col style="width: {getWidth('flags', 160)}px" />
        </colgroup>
        <thead>
          <tr>
            {#each [
              { key: 'plays', label: 'Plays', align: 'right' },
              { key: 'title', label: 'Title', align: 'left' },
              { key: 'flags', label: 'Flags', align: 'left' },
            ] as col (col.key)}
              {@const dir = sortDir(sortStack, col.key as SortCol)}
              {@const pos = sortPosition(sortStack, col.key as SortCol)}
              <th class="relative select-none p-0 {col.align === 'right' ? 'text-right' : ''}">
                <button
                  type="button"
                  class="w-full px-3 py-2 hover:bg-base-200 cursor-pointer flex items-center gap-1 {col.align === 'right' ? 'justify-end' : ''}"
                  onclick={(e) => onSortClick(col.key as SortCol, e)}
                  title="Click to sort. Shift-click for multi-column sort."
                >
                  <span class="overflow-hidden text-ellipsis">{col.label}</span>
                  {#if dir}
                    <span class="text-[10px] tabular-nums text-base-content/60">
                      {dir === 'asc' ? '▲' : '▼'}{pos > 1 ? pos : ''}
                    </span>
                  {/if}
                </button>
                <button
                  type="button"
                  aria-label={`Resize ${col.label}`}
                  class="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/40 active:bg-primary/60 z-10"
                  onmousedown={(e) => startColumnResize(e, getWidth(col.key, 100), (w) => setWidth(col.key, w))}
                ></button>
              </th>
            {/each}
          </tr>
        </thead>
        <tbody>
          {#each visible as v (v.id)}
            <tr>
              <td class="text-right tabular-nums">
                {#if v.watchCount === 0}
                  <span class="text-base-content/40">0</span>
                {:else}
                  <span class="badge badge-neutral badge-sm tabular-nums">{v.watchCount}</span>
                {/if}
              </td>
              <td>
                <button
                  type="button"
                  class="text-left hover:underline truncate max-w-full"
                  onclick={() => onOpen(v)}
                  title={v.fileName}
                >
                  {displayTitle(v)}
                </button>
              </td>
              <td class="text-sm text-base-content/70 space-x-1">
                {#if v.isClip}<span class="badge badge-warning badge-sm">Clip</span>{/if}
                {#if v.needsReview}<span class="badge badge-info badge-sm">Review</span>{/if}
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  {/if}
</div>

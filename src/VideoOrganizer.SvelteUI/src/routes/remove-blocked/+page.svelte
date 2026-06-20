<script lang="ts">
  // Remove Blocked Sections page (issue #70). Builds a new "<name>_trimmed" file
  // for each selected video with its "Hide" blocks cut out (stream-copy concat,
  // no re-encode), then — if the toggle is on — marks the original for deletion
  // via the normal purge flow. Queue of videos-with-hidden-sections along the
  // bottom; selected video's player + its hide timeline in the centre.
  import { onMount, onDestroy } from 'svelte';
  import { api } from '$lib/api';
  import { handleVideoKey } from '$lib/videoKeyboard';
  import { playbackSettings } from '$lib/playbackSettings.svelte';
  import type { BlockRemovalQueueItem, BlockRemovalProgress, VideoBlock } from '$lib/types';

  let queue = $state<BlockRemovalQueueItem[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);

  let selectedId = $state<string | null>(null);
  let selectedIds = $state<Set<string>>(new Set());
  let deleteOriginals = $state(true);

  let progress = $state<BlockRemovalProgress | null>(null);
  let busy = $state(false);
  let poll: ReturnType<typeof setInterval> | null = null;
  let pendingDeletes: string[] = [];

  const selected = $derived(queue.find((q) => q.videoId === selectedId) ?? null);
  const exporting = $derived(!!progress?.active);
  const pct = $derived(
    progress && progress.total > 0 ? Math.round((progress.done / progress.total) * 100) : 0
  );
  const selectedCount = $derived(selectedIds.size);

  // Player keyboard shortcuts (#70 follow-up): drive the focused video, or the
  // selected video's player otherwise, with the app's seek scheme.
  let selectedVideoEl = $state<HTMLVideoElement | null>(null);
  function onPlaybackKey(e: KeyboardEvent) {
    const ae = document.activeElement;
    if (ae && ['BUTTON', 'A', 'INPUT', 'SELECT', 'TEXTAREA'].includes(ae.tagName)) return;
    const el = ae instanceof HTMLVideoElement ? ae : selectedVideoEl;
    handleVideoKey(e, el, playbackSettings);
  }

  function fmt(t: number): string {
    if (!isFinite(t) || t < 0) t = 0;
    const m = Math.floor(t / 60);
    const s = Math.floor(t % 60);
    return `${m}:${s.toString().padStart(2, '0')}`;
  }

  // Merge a video's hide blocks (whole-second offsets) so overlapping bands
  // don't double-count toward the removed total.
  function mergedHides(blocks: VideoBlock[], dur: number): Array<[number, number]> {
    const ivs = blocks
      .map((b) => [Math.max(0, b.offsetInSeconds), Math.min(dur, b.offsetInSeconds + b.lengthInSeconds)] as [number, number])
      .filter(([s, e]) => e > s)
      .sort((a, b) => a[0] - b[0]);
    const out: Array<[number, number]> = [];
    for (const [s, e] of ivs) {
      if (out.length && s <= out[out.length - 1][1]) out[out.length - 1][1] = Math.max(out[out.length - 1][1], e);
      else out.push([s, e]);
    }
    return out;
  }
  function removedSeconds(item: BlockRemovalQueueItem): number {
    return mergedHides(item.hideBlocks, item.durationSeconds).reduce((n, [s, e]) => n + (e - s), 0);
  }
  function keptSeconds(item: BlockRemovalQueueItem): number {
    return Math.max(0, item.durationSeconds - removedSeconds(item));
  }

  async function load() {
    loading = true;
    error = null;
    try {
      queue = await api.getBlockRemovalQueue();
      if (!queue.find((q) => q.videoId === selectedId)) selectedId = queue[0]?.videoId ?? null;
      const present = new Set(queue.map((q) => q.videoId));
      selectedIds = new Set([...selectedIds].filter((id) => present.has(id)));
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
  }

  onMount(async () => {
    await load();
    try {
      const p = await api.getBlockRemovalProgress();
      if (p.active) {
        progress = p;
        startPolling();
      }
    } catch {
      /* none running */
    }
  });
  onDestroy(() => stopPolling());

  function toggle(id: string) {
    const next = new Set(selectedIds);
    next.has(id) ? next.delete(id) : next.add(id);
    selectedIds = next;
  }
  function selectAll() {
    selectedIds = new Set(queue.map((q) => q.videoId));
  }
  function clearSelection() {
    selectedIds = new Set();
  }

  function startPolling() {
    stopPolling();
    poll = setInterval(async () => {
      try {
        const p = await api.getBlockRemovalProgress();
        progress = p;
        if (!p.active) {
          stopPolling();
          await finishRun(p);
        }
      } catch {
        stopPolling();
      }
    }, 800);
  }
  function stopPolling() {
    if (poll) {
      clearInterval(poll);
      poll = null;
    }
  }

  async function finishRun(p: BlockRemovalProgress) {
    // Delete the originals we trimmed, once the run succeeded.
    if (p.phase === 'done' && p.errors.length === 0 && pendingDeletes.length > 0) {
      for (const id of pendingDeletes) {
        try {
          await api.markForDeletion(id);
        } catch {
          /* user can delete from the library */
        }
      }
    }
    pendingDeletes = [];
    await load();
  }

  async function startRun() {
    if (busy || selectedCount === 0 || exporting) return;
    busy = true;
    error = null;
    const ids = [...selectedIds];
    pendingDeletes = deleteOriginals ? ids : [];
    try {
      progress = await api.startBlockRemoval(ids);
      startPolling();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
      pendingDeletes = [];
    } finally {
      busy = false;
    }
  }

  async function stopRun() {
    try {
      progress = await api.stopBlockRemoval();
    } catch {
      /* poll reconciles */
    }
  }
</script>

<svelte:window onkeydowncapture={onPlaybackKey} />

<div class="flex flex-col h-[calc(100vh-3.5rem)]">
  <div class="flex items-center gap-3 flex-wrap p-3 border-b border-base-300">
    <h1 class="text-lg font-semibold">Remove Blocked Sections</h1>
    <span class="text-sm text-base-content/60">
      {queue.length} file{queue.length === 1 ? '' : 's'} with hidden sections
    </span>
    <div class="flex-1"></div>
    <label class="label cursor-pointer gap-1 text-xs">
      <input type="checkbox" class="checkbox checkbox-xs checkbox-error" bind:checked={deleteOriginals} disabled={exporting} />
      Delete originals after trimming
    </label>
    <button class="btn btn-sm btn-ghost" onclick={selectAll} disabled={exporting || queue.length === 0}>Select all</button>
    <button class="btn btn-sm btn-ghost" onclick={clearSelection} disabled={exporting || selectedCount === 0}>Clear</button>
    {#if exporting}
      <button class="btn btn-sm btn-warning" onclick={stopRun}>Stop</button>
    {:else}
      <button class="btn btn-sm btn-primary" onclick={startRun} disabled={busy || selectedCount === 0}>
        {#if busy}<span class="loading loading-spinner loading-xs"></span>{/if}
        Trim {selectedCount} video{selectedCount === 1 ? '' : 's'}
      </button>
    {/if}
  </div>

  {#if progress && (progress.active || progress.phase !== 'idle')}
    <div class="px-3 py-2 border-b border-base-300 bg-base-200/40">
      <div class="flex items-center gap-2 text-sm">
        {#if progress.active}<span class="loading loading-spinner loading-xs"></span>{/if}
        <span>
          {progress.active ? 'Trimming' : progress.phase === 'error' ? 'Finished with errors' : 'Done'} —
          {progress.done}/{progress.total}
        </span>
        {#if progress.current}<span class="text-base-content/60 truncate">{progress.current}</span>{/if}
      </div>
      <progress class="progress progress-primary w-full h-1.5 mt-1" value={pct} max="100"></progress>
      {#if progress.errors.length > 0}
        <ul class="text-xs text-error mt-1 list-disc pl-5">
          {#each progress.errors as e (e)}<li>{e}</li>{/each}
        </ul>
      {/if}
    </div>
  {/if}

  {#if loading}
    <div class="p-6 text-center text-base-content/60"><span class="loading loading-spinner"></span> Loading…</div>
  {:else if error}
    <div class="p-6 text-error">{error}</div>
  {:else if queue.length === 0}
    <div class="p-6 text-center text-base-content/60">
      No videos with hidden sections. Mark "hide" blocks on a video in the player, then trim them here.
    </div>
  {:else}
    <div class="flex-1 flex min-h-0">
      <div class="flex-1 flex flex-col min-w-0 p-3 gap-2">
        {#if selected}
          <div class="flex items-center gap-2 flex-wrap">
            <label class="label cursor-pointer gap-2 p-0">
              <input
                type="checkbox"
                class="checkbox checkbox-sm"
                checked={selectedIds.has(selected.videoId)}
                onchange={() => toggle(selected!.videoId)}
                disabled={exporting}
              />
              <span class="font-medium truncate">{selected.fileName}</span>
            </label>
            <span class="text-xs text-base-content/50">
              {fmt(selected.durationSeconds)} → keeps ≈ {fmt(keptSeconds(selected))}
              ({selected.hideBlocks.length} hidden section{selected.hideBlocks.length === 1 ? '' : 's'})
            </span>
          </div>
          <!-- svelte-ignore a11y_media_has_caption -->
          <video class="w-full max-h-[55vh] bg-black rounded" src={api.streamUrl(selected.videoId)} controls preload="metadata" bind:this={selectedVideoEl}></video>
          <!-- Hide timeline: red bands over the full duration. -->
          <div class="relative h-3 bg-base-300 rounded overflow-hidden">
            {#each selected.hideBlocks as b, i (i)}
              <div
                class="absolute inset-y-0 bg-error/70"
                style="left: {(b.offsetInSeconds / selected.durationSeconds) * 100}%; width: {(b.lengthInSeconds / selected.durationSeconds) * 100}%"
                title="Hidden {fmt(b.offsetInSeconds)}–{fmt(b.offsetInSeconds + b.lengthInSeconds)}"
              ></div>
            {/each}
          </div>
        {/if}
      </div>

      <!-- Right: the selected video's hidden ranges. -->
      <div class="w-72 shrink-0 border-l border-base-300 overflow-auto p-2">
        {#if selected}
          <div class="text-xs uppercase tracking-wide text-base-content/60 mb-1">Hidden sections</div>
          <ul class="flex flex-col gap-1">
            {#each selected.hideBlocks as b, i (i)}
              <li class="text-xs flex items-center gap-2">
                <span class="inline-block w-2 h-2 rounded-full bg-error/70"></span>
                <span class="font-mono">{fmt(b.offsetInSeconds)}–{fmt(b.offsetInSeconds + b.lengthInSeconds)}</span>
                <span class="text-base-content/50">({b.lengthInSeconds}s)</span>
              </li>
            {/each}
          </ul>
        {/if}
      </div>
    </div>

    <!-- Queue strip -->
    <div class="border-t border-base-300 p-2 overflow-x-auto">
      <div class="flex gap-2">
        {#each queue as item (item.videoId)}
          <div
            class="shrink-0 w-44 rounded border-2 p-1 {item.videoId === selectedId
              ? 'border-primary'
              : 'border-transparent hover:border-base-300'}"
          >
            <label class="flex items-center gap-1 mb-1 cursor-pointer">
              <input
                type="checkbox"
                class="checkbox checkbox-xs"
                checked={selectedIds.has(item.videoId)}
                onchange={() => toggle(item.videoId)}
                disabled={exporting}
              />
              <span class="text-xs truncate">{item.fileName}</span>
            </label>
            <button class="block w-full text-left" onclick={() => (selectedId = item.videoId)}>
              <!-- svelte-ignore a11y_media_has_caption -->
              <video class="w-full h-20 object-cover bg-black rounded" src={api.streamUrl(item.videoId)} preload="metadata" muted></video>
              <div class="relative h-1.5 bg-base-300 rounded overflow-hidden mt-1">
                {#each item.hideBlocks as b, i (i)}
                  <div
                    class="absolute inset-y-0 bg-error/70"
                    style="left: {(b.offsetInSeconds / item.durationSeconds) * 100}%; width: {(b.lengthInSeconds / item.durationSeconds) * 100}%"
                  ></div>
                {/each}
              </div>
              <div class="text-[0.7rem] text-base-content/50 mt-0.5">
                {item.hideBlocks.length} hidden · keeps ≈ {fmt(keptSeconds(item))}
              </div>
            </button>
          </div>
        {/each}
      </div>
    </div>
  {/if}
</div>

<script lang="ts">
  // Export Clips page (issue #69). Clips defined as a [start,end] region over a
  // parent file get stream-copied into their own standalone files. No filter
  // tree: a queue of files-with-clips along the bottom, the selected parent's
  // player in the centre, and that parent's clips as small players on the right
  // with per-clip export toggles. Stream-copy is fast and lossless but snaps to
  // keyframes, so each clip can preview the actual (snapped) in-point.
  import { onMount, onDestroy } from 'svelte';
  import { api } from '$lib/api';
  import { handleVideoKey } from '$lib/videoKeyboard';
  import { playbackSettings } from '$lib/playbackSettings.svelte';
  import type { ClipExportQueueItem, ClipExportProgress, KeyframeCut } from '$lib/types';

  let queue = $state<ClipExportQueueItem[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);

  let selectedParentId = $state<string | null>(null);
  let selectedClipIds = $state<Set<string>>(new Set());
  let deleteParentIds = $state<Set<string>>(new Set());
  let keyframeByClip = $state<Record<string, KeyframeCut>>({});
  // Editable output name per clip (#173), defaulting to "<parent-stem>_clip".
  let clipNames = $state<Record<string, string>>({});

  function stripExt(name: string): string {
    const i = name.lastIndexOf('.');
    return i > 0 ? name.slice(0, i) : name;
  }
  function defaultClipName(parentFileName: string): string {
    return `${stripExt(parentFileName)}_clip`;
  }

  let progress = $state<ClipExportProgress | null>(null);
  let busy = $state(false);
  let poll: ReturnType<typeof setInterval> | null = null;
  // Parents to mark-for-deletion once the run finishes successfully.
  let pendingDeletes: string[] = [];

  const selectedParent = $derived(queue.find((q) => q.parentId === selectedParentId) ?? null);
  const totalClips = $derived(queue.reduce((n, q) => n + q.clips.length, 0));
  const exporting = $derived(!!progress?.active);
  const pct = $derived(
    progress && progress.total > 0 ? Math.round((progress.done / progress.total) * 100) : 0
  );

  function fmt(t: number): string {
    if (!isFinite(t) || t < 0) t = 0;
    const m = Math.floor(t / 60);
    const s = Math.floor(t % 60);
    return `${m}:${s.toString().padStart(2, '0')}`;
  }

  async function load() {
    loading = true;
    error = null;
    try {
      queue = await api.getClipExportQueue();
      if (!queue.find((q) => q.parentId === selectedParentId)) {
        selectedParentId = queue[0]?.parentId ?? null;
      }
      const present = new Set(queue.flatMap((q) => q.clips.map((c) => c.id)));
      selectedClipIds = new Set([...selectedClipIds].filter((id) => present.has(id)));
      // Seed a default output name for any clip we haven't named yet.
      const names = { ...clipNames };
      for (const q of queue)
        for (const c of q.clips)
          if (!(c.id in names)) names[c.id] = defaultClipName(q.parentFileName);
      clipNames = names;
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
  }

  onMount(async () => {
    await load();
    try {
      const p = await api.getClipExportProgress();
      if (p.active) {
        progress = p;
        startPolling();
      }
    } catch {
      /* no run in flight */
    }
  });
  onDestroy(() => stopPolling());

  function toggleClip(id: string) {
    const next = new Set(selectedClipIds);
    next.has(id) ? next.delete(id) : next.add(id);
    selectedClipIds = next;
  }
  function selectAllThisVideo() {
    if (!selectedParent) return;
    const next = new Set(selectedClipIds);
    for (const c of selectedParent.clips) next.add(c.id);
    selectedClipIds = next;
  }
  function selectAllVideos() {
    selectedClipIds = new Set(queue.flatMap((q) => q.clips.map((c) => c.id)));
  }
  function clearSelection() {
    selectedClipIds = new Set();
  }
  function toggleDeleteParent(pid: string) {
    const next = new Set(deleteParentIds);
    next.has(pid) ? next.delete(pid) : next.add(pid);
    deleteParentIds = next;
  }

  async function previewKeyframe(clipId: string) {
    try {
      keyframeByClip = { ...keyframeByClip, [clipId]: await api.getClipKeyframeCut(clipId) };
    } catch {
      /* leave it; preview is best-effort */
    }
  }

  function startPolling() {
    stopPolling();
    poll = setInterval(async () => {
      try {
        const p = await api.getClipExportProgress();
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

  // After a successful run, delete the parents the user opted to remove (only
  // those whose clips were all exported), then refresh the queue.
  async function finishRun(p: ClipExportProgress) {
    if (p.phase === 'done' && p.errors.length === 0 && pendingDeletes.length > 0) {
      for (const pid of pendingDeletes) {
        try {
          await api.markForDeletion(pid);
        } catch {
          /* leave it; user can delete from the library */
        }
      }
    }
    pendingDeletes = [];
    await load();
  }

  async function startExport() {
    if (busy || selectedClipIds.size === 0 || exporting) return;
    busy = true;
    error = null;
    const ids = [...selectedClipIds];
    const clips = ids.map((id) => ({ clipId: id, name: clipNames[id]?.trim() || undefined }));
    // Only delete a parent if every one of its clips is in this export.
    pendingDeletes = queue
      .filter((q) => deleteParentIds.has(q.parentId) && q.clips.every((c) => selectedClipIds.has(c.id)))
      .map((q) => q.parentId);
    try {
      progress = await api.startClipExport(clips);
      startPolling();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
      pendingDeletes = [];
    } finally {
      busy = false;
    }
  }

  async function stopExport() {
    try {
      progress = await api.stopClipExport();
    } catch {
      /* poll reconciles */
    }
  }

  const selectedCount = $derived(selectedClipIds.size);

  // Player keyboard shortcuts (#69 follow-up): the app's numpad / Shift+digit /
  // plain-digit / space seek scheme drives the focused clip player, or the
  // selected parent's player otherwise — so videos are navigable here too.
  let parentVideoEl = $state<HTMLVideoElement | null>(null);
  function onPlaybackKey(e: KeyboardEvent) {
    const ae = document.activeElement;
    // Don't hijack keys while a control (button/checkbox/etc.) is focused.
    if (ae && ['BUTTON', 'A', 'INPUT', 'SELECT', 'TEXTAREA'].includes(ae.tagName)) return;
    const el = ae instanceof HTMLVideoElement ? ae : parentVideoEl;
    handleVideoKey(e, el, playbackSettings);
  }
</script>

<svelte:window onkeydowncapture={onPlaybackKey} />

<div class="flex flex-col h-[calc(100vh-3.5rem)]">
  <!-- Header + run controls -->
  <div class="flex items-center gap-3 flex-wrap p-3 border-b border-base-300">
    <h1 class="text-lg font-semibold">Export Clips</h1>
    <span class="text-sm text-base-content/60">
      {totalClips} clip{totalClips === 1 ? '' : 's'} across {queue.length} file{queue.length === 1 ? '' : 's'}
    </span>
    <div class="flex-1"></div>
    <button class="btn btn-sm btn-ghost" onclick={selectAllVideos} disabled={exporting || totalClips === 0}>
      Select all (all videos)
    </button>
    <button class="btn btn-sm btn-ghost" onclick={clearSelection} disabled={exporting || selectedCount === 0}>
      Clear
    </button>
    {#if exporting}
      <button class="btn btn-sm btn-warning" onclick={stopExport}>Stop</button>
    {:else}
      <button class="btn btn-sm btn-primary" onclick={startExport} disabled={busy || selectedCount === 0}>
        {#if busy}<span class="loading loading-spinner loading-xs"></span>{/if}
        Export {selectedCount} clip{selectedCount === 1 ? '' : 's'}
      </button>
    {/if}
  </div>

  <!-- Status / progress -->
  {#if progress && (progress.active || progress.phase !== 'idle')}
    <div class="px-3 py-2 border-b border-base-300 bg-base-200/40">
      <div class="flex items-center gap-2 text-sm">
        {#if progress.active}<span class="loading loading-spinner loading-xs"></span>{/if}
        <span>
          {progress.active ? 'Exporting' : progress.phase === 'error' ? 'Finished with errors' : 'Done'} —
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
    <div class="p-6 text-center text-base-content/60">
      <span class="loading loading-spinner"></span> Loading clips…
    </div>
  {:else if error}
    <div class="p-6 text-error">{error}</div>
  {:else if queue.length === 0}
    <div class="p-6 text-center text-base-content/60">
      No clips defined yet. Mark clips on a video in the player, then come back here to export them.
    </div>
  {:else}
    <!-- Main: parent player (centre) + that parent's clips (right) -->
    <div class="flex-1 flex min-h-0">
      <div class="flex-1 flex flex-col min-w-0 p-3 gap-2">
        {#if selectedParent}
          <div class="flex items-center gap-2 flex-wrap">
            <span class="font-medium truncate">{selectedParent.parentFileName}</span>
            <span class="text-xs text-base-content/50">({fmt(selectedParent.parentDurationSeconds)})</span>
            <div class="flex-1"></div>
            <button class="btn btn-xs btn-ghost" onclick={selectAllThisVideo} disabled={exporting}>
              Select all this video
            </button>
            <label class="label cursor-pointer gap-1 text-xs">
              <input
                type="checkbox"
                class="checkbox checkbox-xs checkbox-error"
                checked={deleteParentIds.has(selectedParent.parentId)}
                onchange={() => toggleDeleteParent(selectedParent!.parentId)}
                disabled={exporting}
              />
              Delete original after export
            </label>
          </div>
          <!-- svelte-ignore a11y_media_has_caption -->
          <video
            class="w-full max-h-[55vh] bg-black rounded"
            src={api.streamUrl(selectedParent.parentId)}
            controls
            preload="metadata"
            bind:this={parentVideoEl}
          ></video>
        {/if}
      </div>

      <!-- Right: the selected parent's clips as small players + export toggles -->
      <div class="w-80 shrink-0 border-l border-base-300 overflow-auto p-2 flex flex-col gap-3">
        {#if selectedParent}
          {#each selectedParent.clips as clip (clip.id)}
            {@const kf = keyframeByClip[clip.id]}
            <div class="card bg-base-200 p-2 gap-1">
              <label class="label cursor-pointer justify-start gap-2 p-0">
                <input
                  type="checkbox"
                  class="checkbox checkbox-sm"
                  checked={selectedClipIds.has(clip.id)}
                  onchange={() => toggleClip(clip.id)}
                  disabled={exporting}
                />
                <span class="text-xs font-medium truncate">{clip.fileName}</span>
              </label>
              <!-- Editable output name (#173). Defaults to "<parent>_clip"; the
                   parent's extension is kept automatically. -->
              <label class="flex items-center gap-1 text-[0.7rem] text-base-content/60">
                <span class="shrink-0">Name</span>
                <input
                  type="text"
                  class="input input-xs input-bordered flex-1 font-mono"
                  bind:value={clipNames[clip.id]}
                  placeholder={selectedParent ? defaultClipName(selectedParent.parentFileName) : 'clip'}
                  disabled={exporting}
                />
              </label>
              <!-- svelte-ignore a11y_media_has_caption -->
              <video
                class="w-full bg-black rounded"
                src={`${api.streamUrl(selectedParent.parentId)}#t=${clip.clipStartSeconds},${clip.clipEndSeconds}`}
                controls
                preload="metadata"
              ></video>
              <div class="flex items-center gap-2 text-[0.7rem] text-base-content/60">
                <span>{fmt(clip.clipStartSeconds)}–{fmt(clip.clipEndSeconds)}</span>
                <div class="flex-1"></div>
                <button class="btn btn-ghost btn-xs" onclick={() => previewKeyframe(clip.id)}>
                  Preview cut
                </button>
              </div>
              {#if kf}
                <div class="text-[0.7rem] text-base-content/60">
                  {#if kf.snappedStartSeconds < kf.requestedStartSeconds - 0.05}
                    Export starts at <span class="font-mono">{fmt(kf.snappedStartSeconds)}</span>
                    (nearest keyframe before {fmt(kf.requestedStartSeconds)}).
                  {:else}
                    Cut is keyframe-aligned at <span class="font-mono">{fmt(kf.snappedStartSeconds)}</span>.
                  {/if}
                </div>
              {/if}
            </div>
          {/each}
        {/if}
      </div>
    </div>

    <!-- Queue: every file with clips, as a thumbnail strip along the bottom -->
    <div class="border-t border-base-300 p-2 overflow-x-auto">
      <div class="flex gap-2">
        {#each queue as item (item.parentId)}
          <button
            class="shrink-0 w-40 rounded border-2 p-1 text-left {item.parentId === selectedParentId
              ? 'border-primary'
              : 'border-transparent hover:border-base-300'}"
            onclick={() => (selectedParentId = item.parentId)}
          >
            <!-- svelte-ignore a11y_media_has_caption -->
            <video
              class="w-full h-20 object-cover bg-black rounded"
              src={api.streamUrl(item.parentId)}
              preload="metadata"
              muted
            ></video>
            <div class="text-xs truncate mt-1">{item.parentFileName}</div>
            <div class="text-[0.7rem] text-base-content/50">
              {item.clips.length} clip{item.clips.length === 1 ? '' : 's'}
              · {item.clips.filter((c) => selectedClipIds.has(c.id)).length} selected
            </div>
          </button>
        {/each}
      </div>
    </div>
  {/if}
</div>

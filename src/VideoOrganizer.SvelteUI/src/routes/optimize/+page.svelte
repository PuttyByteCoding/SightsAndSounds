<script lang="ts">
  // Optimize for Streaming page (issue #166). Large MP4s buffer before playback
  // when their 'moov' atom is at the end of the file; this remuxes them in place
  // with faststart (moov first), losslessly, so they start instantly. Already-
  // faststart and non-MP4 files are skipped automatically.
  import { onMount, onDestroy } from 'svelte';
  import { api } from '$lib/api';
  import type { StreamingOptimizeProgress, SearchResult } from '$lib/types';

  type Picked = { id: string; title: string };

  let query = $state('');
  let results = $state<SearchResult[]>([]);
  let searching = $state(false);
  let picked = $state<Picked[]>([]);

  let progress = $state<StreamingOptimizeProgress | null>(null);
  let busy = $state(false);
  let error = $state<string | null>(null);
  let poll: ReturnType<typeof setInterval> | null = null;

  const running = $derived(!!progress?.active);
  const pct = $derived(progress && progress.total > 0 ? Math.round((progress.done / progress.total) * 100) : 0);
  const pickedIds = $derived(new Set(picked.map(p => p.id)));

  let searchTimer: ReturnType<typeof setTimeout> | null = null;
  function onQueryInput() {
    if (searchTimer) clearTimeout(searchTimer);
    searchTimer = setTimeout(runSearch, 250);
  }
  async function runSearch() {
    const q = query.trim();
    if (q.length === 0) { results = []; return; }
    searching = true;
    try { results = (await api.search({ q, limit: 20 })).results; }
    catch (e) { error = e instanceof Error ? e.message : String(e); }
    finally { searching = false; }
  }

  function add(r: SearchResult) {
    if (pickedIds.has(r.id)) return;
    picked = [...picked, { id: r.id, title: r.title }];
  }
  function remove(id: string) { picked = picked.filter(p => p.id !== id); }

  onMount(async () => {
    try {
      const p = await api.getOptimizeStreamingProgress();
      if (p.active) { progress = p; startPolling(); }
    } catch { /* none running */ }
  });
  onDestroy(() => stopPolling());

  function startPolling() {
    stopPolling();
    poll = setInterval(async () => {
      try {
        const p = await api.getOptimizeStreamingProgress();
        progress = p;
        if (!p.active) { stopPolling(); if (p.phase === 'done') picked = []; }
      } catch { stopPolling(); }
    }, 1000);
  }
  function stopPolling() { if (poll) { clearInterval(poll); poll = null; } }

  async function optimize() {
    if (busy || running || picked.length === 0) return;
    busy = true; error = null;
    try {
      progress = await api.startOptimizeStreaming(picked.map(p => p.id));
      startPolling();
    } catch (e) { error = e instanceof Error ? e.message : String(e); }
    finally { busy = false; }
  }
  async function stop() {
    try { progress = await api.stopOptimizeStreaming(); } catch { /* poll reconciles */ }
  }
</script>

<div class="p-4 max-w-5xl mx-auto flex flex-col gap-4">
  <h1 class="text-2xl font-semibold">Optimize for Streaming</h1>
  <p class="text-sm text-base-content/60">
    Large MP4s sometimes buffer for a while before they start playing — that's
    the "moov" atom sitting at the end of the file. This remuxes them <strong>in
    place</strong> with faststart (no re-encode, lossless, seconds even for big
    files) so they start instantly. Files that are already optimized, or aren't
    MP4, are skipped.
  </p>

  {#if error}
    <div class="alert alert-error text-sm">
      <span class="flex-1">{error}</span>
      <button class="btn btn-xs btn-ghost" onclick={() => (error = null)}>Dismiss</button>
    </div>
  {/if}

  {#if progress && (progress.active || progress.phase !== 'idle')}
    <div class="alert {progress.phase === 'error' ? 'alert-warning' : 'alert-info'} text-sm flex-col items-start gap-2">
      <div class="flex items-center gap-2 w-full">
        {#if progress.active}<span class="loading loading-spinner loading-xs"></span>{/if}
        <span>
          {progress.active ? 'Optimizing' : progress.phase === 'error' ? 'Finished with errors' : 'Done'} —
          {progress.done}/{progress.total}
          (optimized {progress.optimized}, skipped {progress.skipped})
        </span>
        {#if progress.current}<span class="text-base-content/60 truncate">{progress.current}</span>{/if}
        <span class="flex-1"></span><span>{pct}%</span>
      </div>
      <progress class="progress progress-primary w-full h-1.5" value={pct} max="100"></progress>
      {#if progress.errors.length > 0}
        <ul class="list-disc ml-5">{#each progress.errors as e (e)}<li class="break-all">{e}</li>{/each}</ul>
      {/if}
    </div>
  {/if}

  <div class="grid gap-4 md:grid-cols-2">
    <div class="flex flex-col gap-2">
      <input class="input input-bordered input-sm w-full" placeholder="Search videos to add…" bind:value={query} oninput={onQueryInput} />
      <div class="flex flex-col gap-1 max-h-96 overflow-auto">
        {#if searching}<div class="text-xs text-base-content/50">Searching…</div>{/if}
        {#each results as r (r.id)}
          <button class="flex items-center gap-2 text-left p-1 rounded hover:bg-base-200 disabled:opacity-40" onclick={() => add(r)} disabled={pickedIds.has(r.id)}>
            <img src={api.posterUrl(r.id)} alt="" class="w-16 h-10 object-cover bg-black rounded shrink-0" loading="lazy" />
            <span class="min-w-0"><span class="block text-xs truncate">{r.title}</span><span class="block text-[0.7rem] text-base-content/50 truncate">{r.subtitle}</span></span>
          </button>
        {/each}
      </div>
    </div>

    <div class="flex flex-col gap-2">
      <div class="text-sm font-medium">To optimize ({picked.length})</div>
      {#if picked.length === 0}<div class="text-xs text-base-content/50 italic">Add one or more videos.</div>{/if}
      <ul class="flex flex-col gap-1">
        {#each picked as p (p.id)}
          <li class="flex items-center gap-2 p-1 rounded bg-base-200">
            <img src={api.posterUrl(p.id)} alt="" class="w-16 h-10 object-cover bg-black rounded shrink-0" loading="lazy" />
            <span class="min-w-0 flex-1"><span class="block text-xs truncate">{p.title}</span></span>
            <button class="btn btn-ghost btn-xs" onclick={() => remove(p.id)} disabled={running} title="Remove">✕</button>
          </li>
        {/each}
      </ul>
      <div class="flex gap-2 mt-2">
        {#if running}
          <button class="btn btn-sm btn-warning" onclick={stop}>Stop</button>
        {:else}
          <button class="btn btn-sm btn-primary" onclick={optimize} disabled={busy || picked.length === 0}>
            {#if busy}<span class="loading loading-spinner loading-xs"></span>{/if}
            Optimize {picked.length} video{picked.length === 1 ? '' : 's'}
          </button>
        {/if}
      </div>
    </div>
  </div>
</div>

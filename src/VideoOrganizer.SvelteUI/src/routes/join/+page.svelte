<script lang="ts">
  // Join Videos page (issue #163). Search to add videos to an ordered list,
  // reorder/remove, then concatenate them into one new file. Stream-copy by
  // default (fast, lossless — best for clips from the same source); the
  // "re-encode" option normalizes mismatched inputs at the cost of speed/quality.
  import { onMount, onDestroy } from 'svelte';
  import { api } from '$lib/api';
  import type { JoinProgress, SearchResult } from '$lib/types';

  type Picked = { id: string; title: string; subtitle: string };

  let query = $state('');
  let results = $state<SearchResult[]>([]);
  let searching = $state(false);
  let picked = $state<Picked[]>([]);
  let outName = $state('');
  let reencode = $state(false);

  let progress = $state<JoinProgress | null>(null);
  let busy = $state(false);
  let error = $state<string | null>(null);
  let poll: ReturnType<typeof setInterval> | null = null;

  const joining = $derived(!!progress?.active);
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
    try {
      const res = await api.search({ q, limit: 20 });
      results = res.results;
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      searching = false;
    }
  }

  function add(r: SearchResult) {
    if (pickedIds.has(r.id)) return;
    picked = [...picked, { id: r.id, title: r.title, subtitle: r.subtitle }];
  }
  function remove(id: string) { picked = picked.filter(p => p.id !== id); }
  function move(i: number, delta: number) {
    const j = i + delta;
    if (j < 0 || j >= picked.length) return;
    const next = [...picked];
    [next[i], next[j]] = [next[j], next[i]];
    picked = next;
  }

  onMount(async () => {
    try {
      const p = await api.getJoinProgress();
      if (p.active) { progress = p; startPolling(); }
    } catch { /* none running */ }
  });
  onDestroy(() => stopPolling());

  function startPolling() {
    stopPolling();
    poll = setInterval(async () => {
      try {
        const p = await api.getJoinProgress();
        progress = p;
        if (!p.active) { stopPolling(); if (p.phase === 'done') picked = []; }
      } catch { stopPolling(); }
    }, 1000);
  }
  function stopPolling() { if (poll) { clearInterval(poll); poll = null; } }

  async function join() {
    if (busy || joining || picked.length < 2) return;
    busy = true;
    error = null;
    try {
      progress = await api.startJoin(picked.map(p => p.id), reencode, outName.trim() || undefined);
      startPolling();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }
  async function stop() {
    try { progress = await api.stopJoin(); } catch { /* poll reconciles */ }
  }
</script>

<div class="p-4 max-w-5xl mx-auto flex flex-col gap-4">
  <h1 class="text-2xl font-semibold">Join Videos</h1>
  <p class="text-sm text-base-content/60">
    Concatenate videos into one new file, in order. Stream-copy is lossless and
    fast when the inputs share format (e.g. clips from the same source); turn on
    re-encode to join mismatched videos.
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
        <span>{progress.active ? 'Joining' : progress.phase === 'error' ? 'Finished with errors' : 'Joined'} — {progress.done}/{progress.total}</span>
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
    <!-- Search to add -->
    <div class="flex flex-col gap-2">
      <input class="input input-bordered input-sm w-full" placeholder="Search videos to add…" bind:value={query} oninput={onQueryInput} />
      <div class="flex flex-col gap-1 max-h-96 overflow-auto">
        {#if searching}<div class="text-xs text-base-content/50">Searching…</div>{/if}
        {#each results as r (r.id)}
          <button
            class="flex items-center gap-2 text-left p-1 rounded hover:bg-base-200 disabled:opacity-40"
            onclick={() => add(r)}
            disabled={pickedIds.has(r.id)}
          >
            <img src={api.posterUrl(r.id)} alt="" class="w-16 h-10 object-cover bg-black rounded shrink-0" loading="lazy" />
            <span class="min-w-0">
              <span class="block text-xs truncate">{r.title}</span>
              <span class="block text-[0.7rem] text-base-content/50 truncate">{r.subtitle}</span>
            </span>
          </button>
        {/each}
      </div>
    </div>

    <!-- Ordered join list -->
    <div class="flex flex-col gap-2">
      <div class="text-sm font-medium">To join ({picked.length}) — top plays first</div>
      {#if picked.length === 0}
        <div class="text-xs text-base-content/50 italic">Add at least two videos.</div>
      {/if}
      <ol class="flex flex-col gap-1">
        {#each picked as p, i (p.id)}
          <li class="flex items-center gap-2 p-1 rounded bg-base-200">
            <span class="text-xs text-base-content/50 w-5 text-right">{i + 1}.</span>
            <img src={api.posterUrl(p.id)} alt="" class="w-16 h-10 object-cover bg-black rounded shrink-0" loading="lazy" />
            <span class="min-w-0 flex-1"><span class="block text-xs truncate">{p.title}</span></span>
            <button class="btn btn-ghost btn-xs" onclick={() => move(i, -1)} disabled={i === 0 || joining} title="Move up">↑</button>
            <button class="btn btn-ghost btn-xs" onclick={() => move(i, 1)} disabled={i === picked.length - 1 || joining} title="Move down">↓</button>
            <button class="btn btn-ghost btn-xs" onclick={() => remove(p.id)} disabled={joining} title="Remove">✕</button>
          </li>
        {/each}
      </ol>

      <div class="flex flex-col gap-2 mt-2">
        <label class="flex items-center gap-2 text-sm">
          <span class="shrink-0">Output name</span>
          <input class="input input-bordered input-sm flex-1" placeholder="(defaults to <first>_joined)" bind:value={outName} disabled={joining} />
        </label>
        <label class="label cursor-pointer justify-start gap-2 p-0 text-sm">
          <input type="checkbox" class="checkbox checkbox-sm" bind:checked={reencode} disabled={joining} />
          Re-encode (needed if the videos have different formats/resolutions)
        </label>
        <div class="flex gap-2">
          {#if joining}
            <button class="btn btn-sm btn-warning" onclick={stop}>Stop</button>
          {:else}
            <button class="btn btn-sm btn-primary" onclick={join} disabled={busy || picked.length < 2}>
              {#if busy}<span class="loading loading-spinner loading-xs"></span>{/if}
              Join {picked.length} video{picked.length === 1 ? '' : 's'}
            </button>
          {/if}
        </div>
      </div>
    </div>
  </div>
</div>

<script lang="ts">
  // Live view of the API's 48-hour in-memory log buffer. Polls /api/logs on
  // a short interval and lets the user filter client-side by level + free
  // text, with the search term highlighted in each rendered line.
  //
  // In Development the page also offers a "Seq" tab that embeds the
  // dockerized Seq UI (http://localhost:5341) for structured queries
  // across the same log stream — turned off in production builds where
  // Seq isn't expected to run.
  import { onDestroy, onMount } from 'svelte';
  import { api, ApiError } from '$lib/api';
  import type { LogEvent } from '$lib/types';
  import {
    loadColumnWidths,
    saveColumnWidths,
    resizable,
  } from '$lib/tableUtils.svelte';

  const LEVELS = ['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical'] as const;
  type Level = (typeof LEVELS)[number];

  // Seq embed config. The URL is the user's browser-side view of Seq, so
  // localhost is correct even though the backend talks to it the same way
  // (same dev host). Only show the tab in dev — adapter-static prod builds
  // don't bundle a Seq instance.
  const SEQ_URL = 'http://localhost:5341';
  const seqAvailable = import.meta.env.DEV;
  type Source = 'memory' | 'seq';
  let source = $state<Source>('memory');

  let events = $state<LogEvent[]>([]);
  let loadError = $state<string | null>(null);
  let lastUpdated = $state<Date | null>(null);
  // Initial-load flag — true while the very first /api/logs call is
  // in flight so the page can render a centered spinner instead of
  // the empty-state message. Goes false once the first response
  // (success or failure) lands and stays false; subsequent polls
  // use `polling` for a subtler header indicator.
  let initialLoading = $state(true);
  let polling = $state(false);
  // How many minutes back to fetch on each poll. 5 matches the
  // backend's default and is plenty for live debugging. Older
  // history lives in Seq.
  const WINDOW_MINUTES = 5;
  const FETCH_LIMIT = 1000;

  // Each level starts enabled except Trace/Debug which are usually noise.
  let levelEnabled = $state<Record<Level, boolean>>({
    Trace: false, Debug: false,
    Information: true, Warning: true, Error: true, Critical: true
  });
  let searchInput = $state('');
  let paused = $state(false);
  let autoScroll = $state(true);

  // Tail container: scroll to bottom after each poll unless the user pauses
  // or scrolls up.
  let tailEl: HTMLDivElement | null = $state(null);

  // Column widths persisted to localStorage. Defaults mirror the prior
  // hard-coded `w-24 / w-24 / w-48` Tailwind values for time/level/category
  // (24 * 4 = 96px, 48 * 4 = 192px). Message gets a generous default since
  // it's the actual payload column.
  const WIDTHS_KEY = 'logs.tail';
  let widths = $state<Record<string, number>>(loadColumnWidths(WIDTHS_KEY, {
    time: 96,
    level: 96,
    category: 192,
    message: 900,
  }));
  function setWidth(col: string, w: number) {
    widths = { ...widths, [col]: w };
    saveColumnWidths(WIDTHS_KEY, widths);
  }
  function getWidth(col: string, fallback: number): number {
    return widths[col] ?? fallback;
  }
  // Explicit table width (see DataTableModal note re: max-content).
  const totalWidth = $derived(
    getWidth('time', 96)
    + getWidth('level', 96)
    + getWidth('category', 192)
    + getWidth('message', 900)
  );

  async function load() {
    polling = true;
    try {
      events = await api.getLogs({ sinceMinutes: WINDOW_MINUTES, take: FETCH_LIMIT });
      lastUpdated = new Date();
      loadError = null;
    } catch (e) {
      loadError = e instanceof ApiError || e instanceof Error ? e.message : String(e);
    } finally {
      polling = false;
      initialLoading = false;
    }
  }

  const visible = $derived.by(() => {
    const q = searchInput.trim().toLowerCase();
    return events.filter((e) => {
      if (!levelEnabled[e.level as Level]) return false;
      if (q.length === 0) return true;
      // Match in message, category, or the exception dump.
      return e.message.toLowerCase().includes(q)
          || e.category.toLowerCase().includes(q)
          || (e.exception?.toLowerCase().includes(q) ?? false);
    });
  });

  // Highlight the search term inside a string. Returns an array of
  // {text, match} segments the template concatenates.
  function highlight(text: string, q: string): Array<{ text: string; match: boolean }> {
    if (!q) return [{ text, match: false }];
    const needle = q.toLowerCase();
    const hay = text.toLowerCase();
    const out: Array<{ text: string; match: boolean }> = [];
    let i = 0;
    while (i < text.length) {
      const found = hay.indexOf(needle, i);
      if (found < 0) { out.push({ text: text.slice(i), match: false }); break; }
      if (found > i) out.push({ text: text.slice(i, found), match: false });
      out.push({ text: text.slice(found, found + q.length), match: true });
      i = found + q.length;
    }
    return out;
  }

  function levelBadgeClass(level: string): string {
    switch (level) {
      case 'Critical': return 'badge badge-error badge-sm';
      case 'Error':    return 'badge badge-error badge-sm';
      case 'Warning':  return 'badge badge-warning badge-sm';
      case 'Information': return 'badge badge-info badge-sm';
      case 'Debug':    return 'badge badge-ghost badge-sm';
      case 'Trace':    return 'badge badge-ghost badge-sm opacity-60';
      default:         return 'badge badge-ghost badge-sm';
    }
  }

  function shortCategory(c: string): string {
    // "VideoOrganizer.API.Services.ThumbnailWarmingService" → "ThumbnailWarmingService"
    const i = c.lastIndexOf('.');
    return i >= 0 ? c.slice(i + 1) : c;
  }

  function formatTime(iso: string): string {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${String(d.getMilliseconds()).padStart(3, '0')}`;
  }

  let pollHandle: ReturnType<typeof setInterval> | null = null;
  onMount(async () => {
    await load();
    pollHandle = setInterval(() => {
      // Skip the API hit when the user is viewing the Seq tab — there's
      // no UI consuming the in-memory feed in that mode, no point churning.
      if (!paused && source === 'memory') load();
    }, 2500);
  });
  onDestroy(() => { if (pollHandle !== null) clearInterval(pollHandle); });

  // Scroll the tail to the bottom whenever events changes — but only if the
  // user hasn't scrolled up and autoScroll isn't disabled.
  $effect(() => {
    void visible.length;
    if (!autoScroll || !tailEl) return;
    queueMicrotask(() => {
      if (tailEl) tailEl.scrollTop = tailEl.scrollHeight;
    });
  });

  function toggleAllLevels(on: boolean) {
    levelEnabled = { Trace: on, Debug: on, Information: on, Warning: on, Error: on, Critical: on };
  }
</script>

<svelte:head><title>Logs</title></svelte:head>

<!-- Full-height flex column so the Seq iframe (and the memory tail) can
     grow to fill all vertical space below the header. The 3rem subtracts
     the lg:p-6 padding on <main> so the bottom of the panel sits flush
     with the viewport. -->
<div class="flex flex-col gap-3 h-[calc(100vh-3rem)]">
  <div class="flex items-center gap-3 flex-wrap shrink-0 bg-base-100">
    <h1 class="text-2xl font-semibold">Logs</h1>
    {#if source === 'memory'}
      <div class="text-sm text-base-content/60 tabular-nums flex items-center gap-2">
        <span>
          {visible.length} of {events.length}
          {#if lastUpdated}· updated {formatTime(lastUpdated.toISOString())}{/if}
        </span>
        <!-- Subtle inline indicator that a background poll is in
             flight. Only shown after the initial load — the big
             centered spinner over the table covers that case. -->
        {#if polling && !initialLoading}
          <span class="loading loading-spinner loading-xs text-base-content/40" title="Polling…"></span>
        {/if}
      </div>
      <span class="text-xs text-base-content/50 italic">
        Last {WINDOW_MINUTES} min · max {FETCH_LIMIT}. Older logs in Seq.
      </span>
    {/if}

    {#if seqAvailable}
      <!-- Source toggle: in-memory ring buffer vs the dockerized Seq UI.
           Hidden in production builds (no Seq container expected). -->
      <div class="join ml-2" role="tablist" aria-label="Log source">
        <button
          type="button"
          role="tab"
          aria-selected={source === 'memory'}
          class="btn btn-sm join-item {source === 'memory' ? 'btn-primary' : 'btn-ghost'}"
          onclick={() => (source = 'memory')}
        >In-memory</button>
        <button
          type="button"
          role="tab"
          aria-selected={source === 'seq'}
          class="btn btn-sm join-item {source === 'seq' ? 'btn-primary' : 'btn-ghost'}"
          onclick={() => (source = 'seq')}
        >Seq</button>
      </div>
    {/if}

    {#if source === 'memory'}
      <label class="label cursor-pointer gap-2 ml-auto">
        <input type="checkbox" class="checkbox checkbox-sm" bind:checked={paused} />
        <span class="label-text">Pause polling</span>
      </label>
      <label class="label cursor-pointer gap-2">
        <input type="checkbox" class="checkbox checkbox-sm" bind:checked={autoScroll} />
        <span class="label-text">Auto-scroll</span>
      </label>
    {:else}
      <a
        href={SEQ_URL}
        target="_blank"
        rel="noopener noreferrer"
        class="btn btn-ghost btn-sm ml-auto"
      >Open in new tab ↗</a>
    {/if}
  </div>

  {#if source === 'memory'}
    <div class="flex items-center gap-2 flex-wrap shrink-0">
      <input
        class="input input-bordered flex-1 min-w-[16rem]"
        placeholder="Live filter — type to highlight matches..."
        bind:value={searchInput}
        autocomplete="off"
      />
      {#if searchInput}
        <button type="button" class="btn btn-ghost btn-sm" onclick={() => (searchInput = '')}>Clear</button>
      {/if}
    </div>

    <div class="flex items-center gap-2 flex-wrap text-sm shrink-0">
      <span class="text-base-content/60">Levels:</span>
      {#each LEVELS as lv (lv)}
        <label class="label cursor-pointer gap-1 py-0">
          <input
            type="checkbox"
            class="checkbox checkbox-xs"
            bind:checked={levelEnabled[lv]}
          />
          <span class="label-text text-xs">{lv}</span>
        </label>
      {/each}
      <button type="button" class="btn btn-ghost btn-xs" onclick={() => toggleAllLevels(true)}>All</button>
      <button type="button" class="btn btn-ghost btn-xs" onclick={() => toggleAllLevels(false)}>None</button>
    </div>

    {#if loadError}<div class="alert alert-error py-2 text-sm shrink-0">{loadError}</div>{/if}

    <div
      bind:this={tailEl}
      class="card bg-base-200 p-0 flex-1 min-h-0 overflow-auto font-mono text-xs relative"
    >
      {#if initialLoading}
        <!-- Centered spinner over the empty card while the first
             /api/logs call is in flight. The fetch is bounded to
             the last 5 minutes + 1000 entries server-side, so this
             should resolve in well under a second on a healthy
             API — but a chatty boot can spike, and the spinner is
             a better signal than a flash of "No log events yet." -->
        <div class="absolute inset-0 flex flex-col items-center justify-center gap-3 text-base-content/60">
          <span class="loading loading-spinner loading-lg text-primary"></span>
          <span class="text-sm">Loading recent logs…</span>
        </div>
      {:else if visible.length === 0}
        <div class="p-4 text-base-content/60 italic">
          {events.length === 0 ? 'No log events yet.' : 'No events match the current filter.'}
        </div>
      {:else}
        <!-- table-layout:fixed lets the colgroup widths apply so users
             can drag column borders to resize. The thead is sticky to
             the scroll container so labels + resize handles stay
             visible while tailing. -->
        <table class="table table-compact table-zebra resizable-table" style="table-layout: fixed; width: {totalWidth}px;">
          <colgroup>
            <col style="width: {getWidth('time', 96)}px" />
            <col style="width: {getWidth('level', 96)}px" />
            <col style="width: {getWidth('category', 192)}px" />
            <col style="width: {getWidth('message', 900)}px" />
          </colgroup>
          <thead class="sticky top-0 z-10 bg-base-200 shadow-[0_1px_0_0_var(--color-base-300)]">
            <tr>
              {#each [
                { key: 'time', label: 'Time', def: 96 },
                { key: 'level', label: 'Level', def: 96 },
                { key: 'category', label: 'Category', def: 192 },
                { key: 'message', label: 'Message', def: 900 },
              ] as col (col.key)}
                <th
                  class="relative select-none p-0 text-left bg-base-200"
                  style="width: {getWidth(col.key, col.def)}px;"
                >
                  <span class="block px-3 py-2 truncate text-xs uppercase tracking-wide text-base-content/70">{col.label}</span>
                  <button
                    type="button"
                    aria-label={`Resize ${col.label} (double-click to auto-fit)`}
                    class="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/40 active:bg-primary/60 z-10"
                    use:resizable={{
                      getWidth: () => getWidth(col.key, 100),
                      setWidth: (w) => setWidth(col.key, w),
                    }}
                  ></button>
                </th>
              {/each}
            </tr>
          </thead>
          <tbody>
            {#each visible as e (e.timestamp + ':' + e.category + ':' + e.message)}
              <tr>
                <td class="align-top text-base-content/60 whitespace-nowrap">
                  {formatTime(e.timestamp)}
                </td>
                <td class="align-top">
                  <span class={levelBadgeClass(e.level)}>{e.level}</span>
                </td>
                <td class="align-top text-base-content/70 truncate" title={e.category}>
                  {#each highlight(shortCategory(e.category), searchInput) as seg (seg.text)}
                    {#if seg.match}<mark class="bg-warning/50 rounded px-0.5">{seg.text}</mark>{:else}{seg.text}{/if}
                  {/each}
                </td>
                <td class="align-top whitespace-pre-wrap break-words">
                  {#each highlight(e.message, searchInput) as seg (seg.text)}
                    {#if seg.match}<mark class="bg-warning/50 rounded px-0.5">{seg.text}</mark>{:else}{seg.text}{/if}
                  {/each}
                  {#if e.exception}
                    <details class="mt-1 text-base-content/70">
                      <summary class="cursor-pointer">Exception</summary>
                      <pre class="whitespace-pre-wrap break-words mt-1">{e.exception}</pre>
                    </details>
                  {/if}
                </td>
              </tr>
            {/each}
          </tbody>
        </table>
      {/if}
    </div>
  {:else}
    <!-- Seq embed. The iframe parent (localhost:5173) and the iframe src
         (localhost:5341) are different origins, so any cross-frame messaging
         is blocked — but plain rendering works. If Seq isn't running or
         CSP blocks the embed, the "Open in new tab" link in the header is
         the fallback. flex-1 + min-h-0 lets the iframe grow to fill all
         remaining vertical space below the header. -->
    <div class="card bg-base-200 p-0 overflow-hidden flex-1 min-h-0">
      <iframe
        src={SEQ_URL}
        title="Seq structured log viewer"
        class="w-full h-full border-0 bg-base-100"
        loading="lazy"
        referrerpolicy="no-referrer"
      ></iframe>
    </div>
    <p class="text-xs text-base-content/60 shrink-0">
      Embedded from <code>{SEQ_URL}</code>. Showing a blank panel? Make sure the
      <code>seq</code> container is up (<code>docker compose ps seq</code>) and
      use "Open in new tab" if your browser blocks the iframe.
    </p>
  {/if}
</div>

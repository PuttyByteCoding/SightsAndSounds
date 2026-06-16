<script lang="ts">
  // Ctrl+K / Cmd+K command palette — global search across every
  // searchable field on a Video (filename, path, notes, MD5, tag
  // names). Lives once at the layout level so it's available from
  // every route.
  //
  // v1 returns video results only. The kind-discriminated result
  // shape means v2 can add tag / source / etc. results — every kind
  // gets its own {:else if r.kind === 'X'} branch in the result list
  // and its own click behavior. No structural change required.
  //
  // Open state is controlled by the parent ($bindable) so the layout
  // can flip it on a global Ctrl+K keydown. Esc closes from inside.
  // Clicking the backdrop closes too.
  import { goto } from '$app/navigation';
  import { tick } from 'svelte';
  import { api, ApiError } from '$lib/api';
  import type { SearchResult, VideoSearchResult } from '$lib/types';

  interface Props {
    /** Two-way bound. Parent flips this on Ctrl+K; we flip it to false on Esc / pick. */
    open: boolean;
  }

  let { open = $bindable() }: Props = $props();

  // Fixed page size for both the initial fetch and every infinite-
  // scroll page. Server clamps to [1, 200]; 50 keeps the response
  // light enough for fast first-render but big enough that the user
  // doesn't trigger load-more after every flick of the wheel.
  const PAGE_SIZE = 50;

  let query = $state('');
  let results = $state<SearchResult[]>([]);
  let totalCount = $state(0);
  let loading = $state(false);     // initial-page fetch in flight
  let loadingMore = $state(false); // pagination fetch in flight
  let errorMsg = $state<string | null>(null);
  // 0-based index of the keyboard-highlighted result. Reset on every
  // new query so the user always lands on the top hit. Clamp into
  // [0, results.length - 1] when results shrink mid-typing.
  let selected = $state(0);

  // Derived: are there more results on the server beyond what we've
  // loaded? Drives both the IntersectionObserver fetch and the
  // ArrowDown-at-bottom auto-fetch. Avoids storing a separate
  // `hasMore` state that could drift from results.length.
  const hasMore = $derived(results.length < totalCount);

  // Input ref so we can autofocus on open and re-focus after the
  // user clicks back in.
  let inputEl: HTMLInputElement | null = $state(null);

  // Debounce handle. 200ms feels snappy for fast typists without
  // hammering the API on every keystroke (a 5-character query would
  // otherwise fire 5 requests in <100ms).
  let debounceTimer: ReturnType<typeof setTimeout> | null = null;

  // Per-query session token. Bumped whenever a new query starts —
  // both initial fetches AND pagination fetches verify the token
  // before applying their response. So a stale "load more" for the
  // OLD query that returns AFTER the user typed a new query gets
  // discarded; otherwise it would append wrong-query results to the
  // new query's list.
  let queryToken = 0;
  // Cancels the in-flight search when a new query starts. The queryToken above
  // already discards stale *results*; this also drops the wasted request. (#131)
  let searchAbort: AbortController | null = null;

  // Autofocus + reset on open. Async because the input might not
  // exist on the very first $effect tick.
  $effect(() => {
    if (open) {
      tick().then(() => inputEl?.focus());
    } else {
      // Closing — clear any pending debounce so we don't fire a
      // request against a closed palette.
      if (debounceTimer !== null) {
        clearTimeout(debounceTimer);
        debounceTimer = null;
      }
    }
  });

  // Re-run search when query changes. Debounced + token-gated.
  $effect(() => {
    const q = query;       // make the dependency explicit
    if (debounceTimer !== null) clearTimeout(debounceTimer);
    if (!q.trim()) {
      // Bump the token so any in-flight pagination from the previous
      // query is discarded before its response can append.
      queryToken++;
      results = [];
      totalCount = 0;
      loading = false;
      loadingMore = false;
      errorMsg = null;
      return;
    }
    debounceTimer = setTimeout(() => void runInitialSearch(q), 200);
  });

  // First page of results. Replaces `results` wholesale. Bumps the
  // query token so any pagination still in flight from a prior query
  // is invalidated.
  async function runInitialSearch(q: string) {
    const token = ++queryToken;
    searchAbort?.abort();
    searchAbort = new AbortController();
    loading = true;
    errorMsg = null;
    try {
      const response = await api.search({ q, limit: PAGE_SIZE, offset: 0 }, searchAbort.signal);
      if (token !== queryToken) return;
      results = response.results;
      totalCount = response.totalCount;
      selected = 0;
    } catch (e) {
      if (token !== queryToken) return;
      errorMsg = e instanceof ApiError || e instanceof Error ? e.message : String(e);
      results = [];
      totalCount = 0;
    } finally {
      if (token === queryToken) loading = false;
    }
  }

  // Subsequent pages. Appends to `results`. Guards on loading flags,
  // empty query, and "already loaded everything." Pins to the
  // current query token so a query change mid-flight discards the
  // append.
  async function loadMore() {
    if (loading || loadingMore) return;
    const q = query.trim();
    if (!q) return;
    if (!hasMore) return;
    const token = queryToken; // pin to the active query session
    loadingMore = true;
    try {
      const response = await api.search({
        q,
        limit: PAGE_SIZE,
        offset: results.length,
      }, searchAbort?.signal);
      if (token !== queryToken) return;
      results = [...results, ...response.results];
      // totalCount can technically shift between pages if the
      // underlying data changed (a video was deleted, etc.); take
      // the latest server value as authoritative.
      totalCount = response.totalCount;
    } catch (e) {
      if (token !== queryToken) return;
      errorMsg = e instanceof ApiError || e instanceof Error ? e.message : String(e);
    } finally {
      if (token === queryToken) loadingMore = false;
    }
  }

  // Keyboard navigation. Window-level so it works even if focus is
  // briefly elsewhere (e.g. user clicked a result then re-opened).
  function onKey(e: KeyboardEvent) {
    if (!open) return;
    if (e.key === 'Escape') {
      e.preventDefault();
      close();
    } else if (e.key === 'ArrowDown') {
      e.preventDefault();
      if (results.length > 0) {
        selected = Math.min(selected + 1, results.length - 1);
        // If the cursor just landed on the last visible row and the
        // server has more, kick off a page load. Keyboard-only users
        // can keep arrowing through without ever scrolling — without
        // this, they'd hit the bottom and the cursor would just stop.
        if (selected === results.length - 1 && hasMore) {
          void loadMore();
        }
      }
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      if (results.length > 0) selected = Math.max(selected - 1, 0);
    } else if (e.key === 'Enter') {
      e.preventDefault();
      const r = results[selected];
      if (r) pick(r);
    }
  }

  function close() {
    open = false;
  }

  function pick(r: SearchResult) {
    // Discriminated dispatch — each kind has its own destination.
    // v2 kinds (tag/source/…) plug in here with their own goto.
    if (r.kind === 'video') {
      // Pass both id (play this specific video) AND searchQuery (so
      // /browse loads the full result-set as the playlist context).
      // The user lands in their pick AND can next/prev through the
      // rest of the matches.
      const q = query.trim();
      const url = q
        ? `/browse?id=${encodeURIComponent(r.id)}&searchQuery=${encodeURIComponent(q)}`
        : `/browse?id=${encodeURIComponent(r.id)}`;
      void goto(url);
    }
    close();
  }

  // "Play all results" — navigate to /browse with just the search
  // query (no id), letting /browse pick the playlist's first video.
  // Disabled while results haven't loaded yet, the result list is
  // empty, or the query is blank.
  function playAll() {
    const q = query.trim();
    if (!q || totalCount === 0) return;
    void goto(`/browse?searchQuery=${encodeURIComponent(q)}`);
    close();
  }

  // --- Cell formatting --------------------------------------------------

  function formatBytes(bytes: number): string {
    if (!bytes || bytes <= 0) return '';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0;
    let n = bytes;
    while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
    return `${n.toFixed(n >= 10 || i === 0 ? 0 : 1)} ${units[i]}`;
  }

  // .NET TimeSpan "[d.]hh:mm:ss[.fffffff]" → "M:SS" or "H:MM:SS".
  // Same shape as VideoCard.formatDuration; duplicated here to keep
  // the palette zero-dependency on Video card.
  function formatDuration(ts: string | null | undefined): string {
    if (!ts) return '';
    const m = ts.match(/^(?:(\d+)\.)?(\d+):(\d+):(\d+)/);
    if (!m) return '';
    const hours = parseInt(m[2], 10) + (m[1] ? parseInt(m[1], 10) * 24 : 0);
    const mins = parseInt(m[3], 10);
    const secs = parseInt(m[4], 10);
    if (hours > 0) return `${hours}:${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')}`;
    return `${mins}:${String(secs).padStart(2, '0')}`;
  }

  // For a video result, surface which fields matched as small
  // chip-style badges. `tag:Performer/Bob Marley` renders as
  // `Performer · Bob Marley` so the user can see WHY a video showed
  // up when the filename doesn't contain the query.
  function matchLabel(field: string): string {
    if (field.startsWith('tag:')) {
      const [, rest] = field.split(':', 2);
      const slash = rest.indexOf('/');
      if (slash >= 0) return `${rest.slice(0, slash)} · ${rest.slice(slash + 1)}`;
      return rest;
    }
    switch (field) {
      case 'fileName': return 'name';
      case 'filePath': return 'path';
      case 'notes':    return 'notes';
      case 'md5':      return 'md5';
      default:         return field;
    }
  }

  // Scroll the highlighted row into view when keyboard nav moves it
  // outside the visible area. Re-runs on selected change.
  let listEl: HTMLDivElement | null = $state(null);
  $effect(() => {
    void selected;
    if (!listEl) return;
    const row = listEl.querySelector<HTMLElement>(`[data-row-index="${selected}"]`);
    row?.scrollIntoView({ block: 'nearest' });
  });

  // --- Infinite scroll ---------------------------------------------------
  // Sentinel div placed at the bottom of the result list. An
  // IntersectionObserver scoped to the scroll container (listEl)
  // fires loadMore() whenever the sentinel becomes visible. That
  // happens both when the user scrolls down AND when a new page
  // appended doesn't fill the viewport (in which case loadMore is
  // called again and the loop continues until the viewport is full
  // OR `hasMore` flips false).
  //
  // The 200px rootMargin pre-loads the next page slightly before the
  // user actually scrolls to the bottom — feels seamless instead of
  // making them wait at the edge while results fetch.
  let sentinelEl: HTMLDivElement | null = $state(null);
  $effect(() => {
    if (!sentinelEl || !listEl) return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          void loadMore();
        }
      },
      { root: listEl, rootMargin: '200px 0px', threshold: 0 }
    );
    observer.observe(sentinelEl);
    return () => observer.disconnect();
  });
</script>

<svelte:window onkeydown={onKey} />

{#if open}
  <!-- Modal scaffold matches daisyUI's .modal pattern — backdrop
       overlay, centered box. We don't use daisyUI's .modal-open
       because we want the size/positioning under explicit control
       (the palette is narrower than a normal dialog, anchored near
       the top). -->
  <div
    class="fixed inset-0 z-[1000] flex items-start justify-center pt-24 bg-black/40 backdrop-blur-sm"
    role="presentation"
    onclick={close}
  >
    <!-- svelte-ignore a11y_click_events_have_key_events -->
    <div
      class="w-full max-w-2xl mx-4 bg-base-100 rounded-lg shadow-2xl border border-base-300 flex flex-col max-h-[70vh] overflow-hidden"
      role="dialog"
      aria-modal="true"
      aria-label="Search"
      tabindex="-1"
      onclick={(e) => e.stopPropagation()}
    >
      <!-- Input row. The leading magnifying glass is decorative; the
           real affordance is the placeholder + the autofocus when
           the palette opens. -->
      <div class="flex items-center gap-2 px-4 py-3 border-b border-base-300 shrink-0">
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" class="w-4 h-4 opacity-60 shrink-0">
          <path fill="currentColor" d="M15.5 14h-.79l-.28-.27A6.471 6.471 0 0016 9.5 6.5 6.5 0 109.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z" />
        </svg>
        <input
          bind:this={inputEl}
          class="flex-1 bg-transparent border-0 outline-none text-base placeholder:text-base-content/40"
          placeholder="Search filenames, paths, tags, notes…"
          bind:value={query}
          autocomplete="off"
          spellcheck="false"
        />
        {#if loading}
          <span class="loading loading-spinner loading-xs text-base-content/40" title="Searching…"></span>
        {/if}
        <kbd class="kbd kbd-sm text-base-content/50">esc</kbd>
      </div>

      <!-- "Play all" action bar. Sits between the input row and the
           result list. Only meaningful when we have results AND a
           non-empty query — otherwise hidden so the empty / no-
           results states don't look cluttered. The button forwards
           the current query to /browse?searchQuery=… which turns
           the entire match set into the active playlist (and thus
           the thumbnail strip under the video player). -->
      {#if totalCount > 0 && query.trim()}
        <div class="flex items-center justify-between gap-2 px-4 py-2 border-b border-base-300 shrink-0 bg-base-200/50">
          <span class="text-xs text-base-content/60 tabular-nums">
            {totalCount} {totalCount === 1 ? 'match' : 'matches'}
          </span>
          <button
            type="button"
            class="btn btn-xs btn-soft btn-primary btn-cta"
            onclick={playAll}
            title="Open all matching videos as a playlist in /browse"
          >
            ▶ Play all ({totalCount})
          </button>
        </div>
      {/if}

      <!-- Result list. Scrolls inside the modal box; height bounded
           by the box's max-h. data-row-index lets the keyboard-nav
           effect scroll the active row into view. -->
      <div bind:this={listEl} class="overflow-y-auto flex-1 min-h-0">
        {#if errorMsg}
          <div class="alert alert-error text-sm m-3" role="alert" aria-live="assertive">{errorMsg}</div>
        {:else if !query.trim()}
          <div class="p-8 text-center text-sm text-base-content/50">
            <div class="mb-2">Type to search every video field.</div>
            <div class="text-xs">
              Matches filename, path, notes, MD5, and tag names.
              <kbd class="kbd kbd-xs">↑</kbd>
              <kbd class="kbd kbd-xs">↓</kbd>
              to navigate ·
              <kbd class="kbd kbd-xs">Enter</kbd>
              to open
            </div>
          </div>
        {:else if loading && results.length === 0}
          <div class="p-8 text-center text-sm text-base-content/50">
            <span class="loading loading-spinner loading-sm"></span>
            <div class="mt-2">Searching…</div>
          </div>
        {:else if results.length === 0}
          <div class="p-8 text-center text-sm text-base-content/50">
            No results for "{query.trim()}".
          </div>
        {:else}
          <!-- Result rows. Discriminated rendering by `kind` —
               adding a v2 kind is one {:else if} branch + a
               kind-specific row block.
               Each row is keyboard-selectable AND mouse-selectable.
               Highlighting follows the keyboard cursor; the mouse
               hover sets the cursor too so they stay in sync. -->
          <ul class="py-1">
            {#each results as r, i (r.kind + ':' + r.id)}
              {#if r.kind === 'video'}
                {@const video = r as VideoSearchResult}
                <li>
                  <button
                    type="button"
                    data-row-index={i}
                    class="w-full text-left px-3 py-2 flex items-start gap-3 hover:bg-base-200 {i === selected ? 'bg-base-200' : ''}"
                    onmouseenter={() => (selected = i)}
                    onclick={() => pick(r)}
                  >
                    <!-- Poster thumbnail. Errors hide the image so a
                         missing poster doesn't leave a broken-icon
                         box. The aspect-video container reserves
                         space so the row height doesn't jump as
                         posters lazy-load. -->
                    <div class="w-24 aspect-video bg-base-300 rounded overflow-hidden shrink-0">
                      <img
                        src={api.posterUrl(video.id)}
                        loading="lazy"
                        alt=""
                        class="w-full h-full object-cover"
                        onerror={(e) => ((e.currentTarget as HTMLImageElement).style.visibility = 'hidden')}
                      />
                    </div>
                    <div class="flex-1 min-w-0">
                      <div class="font-medium truncate flex items-center gap-1">
                        {video.title}
                        {#if video.isClip}
                          <span class="badge badge-warning badge-xs shrink-0">clip</span>
                        {/if}
                      </div>
                      <div class="text-xs text-base-content/60 truncate font-mono">{video.subtitle}</div>
                      <div class="mt-1 flex flex-wrap items-center gap-1 text-xs text-base-content/60">
                        {#if video.duration}<span class="tabular-nums">{formatDuration(video.duration)}</span>{/if}
                        {#if video.fileSize > 0}<span class="tabular-nums">{formatBytes(video.fileSize)}</span>{/if}
                        {#each video.matchedFields as field (field)}
                          <span class="badge badge-ghost badge-xs">{matchLabel(field)}</span>
                        {/each}
                      </div>
                    </div>
                  </button>
                </li>
              {/if}
            {/each}
          </ul>

          <!-- Infinite-scroll sentinel + footer. The sentinel is the
               IntersectionObserver target — once it enters the
               viewport, loadMore() fetches the next page. Sitting
               just below the last result with a 200px rootMargin
               means the next page starts loading slightly before
               the user actually reaches the bottom, so paging feels
               continuous instead of "scroll, wait, scroll, wait."
               When `hasMore` flips false the sentinel stays in
               place but the observer's intersection no longer
               triggers a fetch (loadMore guards on hasMore). -->
          <div bind:this={sentinelEl} class="h-1" aria-hidden="true"></div>
          {#if loadingMore}
            <div class="p-3 text-center text-xs text-base-content/60 flex items-center justify-center gap-2 border-t border-base-300">
              <span class="loading loading-spinner loading-xs"></span>
              Loading more…
            </div>
          {:else if hasMore}
            <!-- Sentinel hasn't been intersected yet — render a tiny
                 hint so the user knows there's more even before
                 they scroll. Compact tabular count keeps the row
                 unobtrusive. -->
            <div class="p-2 text-center text-xs text-base-content/40 border-t border-base-300 tabular-nums">
              {results.length} of {totalCount} · scroll for more
            </div>
          {:else if results.length > PAGE_SIZE}
            <!-- All loaded. Only show the "all results" line when
                 the user has actually paginated past the first
                 page; otherwise the result count is obvious from
                 the visible list. -->
            <div class="p-2 text-center text-xs text-base-content/40 border-t border-base-300 tabular-nums">
              All {totalCount} results
            </div>
          {/if}
        {/if}
      </div>
    </div>
  </div>
{/if}

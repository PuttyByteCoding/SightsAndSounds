<script lang="ts">
  // Simplified Browse page rewritten for the unified tag model. Lists every
  // video filtered by tag + status + folder filters. Plays inline with
  // the existing VideoPlayer and offers the generic EditTagsPanel.
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import type { Video, VideoSet, Tag, TagGroup, FilterRef } from '$lib/types';
  import VideoCard from '$lib/components/VideoCard.svelte';
  import VideoPlayer from '$lib/components/VideoPlayer.svelte';
  import EditTagsPanel from '$lib/components/EditTagsPanel.svelte';
  import FilterDialog from '$lib/components/FilterDialog.svelte';
  import TagEditModal from '$lib/components/TagEditModal.svelte';
  import { filterStore } from '$lib/filterStore.svelte';
  import { pillClass, filterSlot, filterSlotClass } from '$lib/tagColors';

  let videos = $state<Video[]>([]);
  let videosLoading = $state(false);
  let loadError = $state<string | null>(null);

  let groups = $state<TagGroup[]>([]);
  let tagsByGroup = $state<Record<string, Tag[]>>({});
  let expandedGroups = $state<Set<string>>(new Set());

  let sets = $state<VideoSet[]>([]);

  let playingVideo = $state<Video | null>(null);
  let showEditTagsPanel = $state(false);
  // True until the first videos load and we've shuffled-and-auto-played.
  let initialAutoplayPending = true;

  // --- Infinite-scroll chunking -----------------------------------------
  // We render the grid in pages of CHUNK_SIZE so a 1000-video filter
  // doesn't try to mount 1000 VideoCards (each of which warms a VTT) at
  // once. An IntersectionObserver on a sentinel at the bottom of the
  // grid bumps the visible count as the user scrolls.
  const CHUNK_SIZE = 24;
  let visibleCount = $state(CHUNK_SIZE);
  // Reset back to one chunk whenever the underlying list changes — the
  // user just changed filter / reshuffled, so old scroll position is
  // meaningless. Using a counter trips the $effect even when length
  // stays the same (e.g. reshuffle of the same set).
  let visibleResetSeed = $state(0);
  $effect(() => {
    void visibleResetSeed;
    visibleCount = CHUNK_SIZE;
  });
  const visibleVideos = $derived(videos.slice(0, visibleCount));
  let scrollSentinelEl: HTMLDivElement | null = $state(null);
  $effect(() => {
    if (!scrollSentinelEl) return;
    const obs = new IntersectionObserver((entries) => {
      for (const e of entries) {
        if (e.isIntersecting && visibleCount < videos.length) {
          // Bump by a chunk; clamp at the total so we don't overshoot.
          visibleCount = Math.min(videos.length, visibleCount + CHUNK_SIZE);
        }
      }
    }, { rootMargin: '300px' });
    obs.observe(scrollSentinelEl);
    return () => obs.disconnect();
  });

  // --- Player / grid drag-resize ---------------------------------------
  // The right pane is a sticky-player-on-top + thumbnail-grid-below
  // layout. The drag handle controls the player's min-height so the
  // player still grows when the video naturally needs more space.
  // Persisted so the layout survives navigation away and back.
  const PLAYER_HEIGHT_MIN = 220;
  const PLAYER_HEIGHT_DEFAULT = 480;
  let playerHeight = $state(PLAYER_HEIGHT_DEFAULT);
  let dragging = $state(false);
  // Snapshot of the values at the moment dragging starts so we don't
  // re-read the DOM on every mousemove tick.
  let dragStartY = 0;
  let dragStartHeight = 0;

  // --- Thumbnail size --------------------------------------------------
  // User-controlled minimum tile width; the grid uses auto-fill +
  // minmax so columns reflow naturally as the user drags. Persisted to
  // localStorage so the choice sticks.
  const THUMB_WIDTH_MIN = 120;
  const THUMB_WIDTH_MAX = 400;
  const THUMB_WIDTH_DEFAULT = 200;
  let thumbWidth = $state(THUMB_WIDTH_DEFAULT);

  onMount(() => {
    const stored = Number(localStorage.getItem('browsePlayerHeight'));
    if (Number.isFinite(stored) && stored >= PLAYER_HEIGHT_MIN) {
      playerHeight = stored;
    }
    const storedThumb = Number(localStorage.getItem('browseThumbWidth'));
    if (Number.isFinite(storedThumb) && storedThumb >= THUMB_WIDTH_MIN && storedThumb <= THUMB_WIDTH_MAX) {
      thumbWidth = storedThumb;
    }
  });

  // Save on each change. Cheap (a few writes per drag) and keeps the
  // localStorage in sync without a separate "save" gesture.
  $effect(() => {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem('browseThumbWidth', String(thumbWidth));
  });

  // Pointer-event-based drag (works for mouse, touch, pen). Using
  // PointerEvent + setPointerCapture means even if the cursor sails off
  // the handle mid-drag we keep getting move events from the same
  // pointer — no need for window-level listeners or worrying about
  // child-element pointer leakage.
  function startDrag(e: PointerEvent) {
    e.preventDefault();
    e.stopPropagation();
    dragging = true;
    dragStartY = e.clientY;
    dragStartHeight = playerHeight;
    const target = e.currentTarget as Element;
    target.setPointerCapture(e.pointerId);
  }
  function onDragMove(e: PointerEvent) {
    if (!dragging) return;
    const delta = e.clientY - dragStartY;
    // Clamp so the player can't shrink below a usable size or grow
    // past the viewport. The 300px floor reserves room for at least
    // one row of thumbnails below.
    const max = window.innerHeight - 300;
    playerHeight = Math.max(PLAYER_HEIGHT_MIN, Math.min(max, dragStartHeight + delta));
  }
  function endDrag(e: PointerEvent) {
    if (!dragging) return;
    dragging = false;
    const target = e.currentTarget as Element;
    if (target.hasPointerCapture(e.pointerId)) target.releasePointerCapture(e.pointerId);
    localStorage.setItem('browsePlayerHeight', String(playerHeight));
  }

  function shuffleInPlace<T>(arr: T[]): T[] {
    const a = [...arr];
    for (let i = a.length - 1; i > 0; i--) {
      const j = Math.floor(Math.random() * (i + 1));
      [a[i], a[j]] = [a[j], a[i]];
    }
    return a;
  }

  // User-triggered reshuffle of the current grid into a new random order
  // and jump to the new first video. Resets the infinite-scroll window
  // so the user starts at the top of the new playlist.
  function reshufflePlaylist() {
    if (videos.length === 0) return;
    videos = shuffleInPlace(videos);
    playingVideo = videos[0];
    visibleResetSeed++;
  }

  async function refreshVideos() {
    videosLoading = true;
    loadError = null;
    try {
      const filter = {
        required: filterStore.required.map((t): FilterRef => ({ type: t.type, value: t.value })),
        optional: filterStore.optional.map((t): FilterRef => ({ type: t.type, value: t.value })),
        excluded: filterStore.excluded.map((t): FilterRef => ({ type: t.type, value: t.value }))
      };
      const fetched = await api.filterVideos(filter);
      // First load: shuffle into a random playlist and auto-play the first.
      // Subsequent filter changes leave the order alone and don't disrupt
      // whatever video is currently playing.
      if (initialAutoplayPending && fetched.length > 0) {
        videos = shuffleInPlace(fetched);
        playingVideo = videos[0];
        initialAutoplayPending = false;
      } else {
        videos = fetched;
      }
      // Filter change → reset the infinite-scroll window so the new
      // result set starts from the top.
      visibleResetSeed++;
    } catch (e: any) {
      loadError = e?.message ?? 'Failed to load videos';
    } finally {
      videosLoading = false;
    }
  }

  $effect(() => {
    // Re-fetch whenever the filter store changes.
    void filterStore.required; void filterStore.optional; void filterStore.excluded;
    refreshVideos();
  });

  async function loadSidebar() {
    try {
      groups = await api.listTagGroups();
      sets = await api.listVideoSets();
      await loadFavorites();
    } catch (e: any) {
      loadError = e?.message ?? 'Failed to load sidebar';
    }
  }

  // Cache every tag once so the search box can scan them without a round
  // trip per keystroke. Favorites are derived from the same list.
  let allTags = $state<Tag[]>([]);
  const favoriteTags = $derived(allTags.filter(t => t.isFavorite));
  async function loadFavorites() {
    try {
      allTags = await api.listTags({ withCounts: true });
    } catch (e: any) {
      loadError = e?.message ?? 'Failed to load tags';
    }
  }

  // ---- Global filter search ----
  // One search box that scans tag names + aliases, system statuses, and
  // "Missing <group>" entries. Each result tags itself with its source so
  // the row label reads e.g. "Bob Marley · Performer" or "Won't Play · System".
  let searchQuery = $state('');
  type SearchResult =
    | { kind: 'tag'; tag: Tag; matchedAlias?: string }
    | { kind: 'status'; value: 'favorite' | 'needsReview' | 'wontPlay' | 'markedForDeletion'; label: string }
    | { kind: 'missing'; group: TagGroup };

  const searchResults = $derived.by<SearchResult[]>(() => {
    const q = searchQuery.trim().toLowerCase();
    if (!q) return [];
    const out: SearchResult[] = [];
    for (const t of allTags) {
      if (t.name.toLowerCase().includes(q)) {
        out.push({ kind: 'tag', tag: t });
        continue;
      }
      const aliasHit = t.aliases.find(a => a.toLowerCase().includes(q));
      if (aliasHit) out.push({ kind: 'tag', tag: t, matchedAlias: aliasHit });
    }
    const statuses = [
      { value: 'favorite', label: 'Favorite' },
      { value: 'needsReview', label: 'Needs Review' },
      { value: 'wontPlay', label: "Won't Play" },
      { value: 'markedForDeletion', label: 'To Delete' }
    ] as const;
    for (const s of statuses) {
      if (s.label.toLowerCase().includes(q)) {
        out.push({ kind: 'status', value: s.value, label: s.label });
      }
    }
    for (const g of groups) {
      // Match either the bare group name or the full "Missing X" phrase.
      if (g.name.toLowerCase().includes(q) || `missing ${g.name}`.toLowerCase().includes(q)) {
        out.push({ kind: 'missing', group: g });
      }
    }
    return out;
  });

  function applySearchResult(r: SearchResult) {
    if (r.kind === 'tag') pickTag(r.tag);
    else if (r.kind === 'status') pickStatus(r.value, r.label);
    else pickMissing(r.group);
    searchQuery = '';
  }

  async function toggleGroup(g: TagGroup) {
    const next = new Set(expandedGroups);
    if (next.has(g.id)) {
      next.delete(g.id);
    } else {
      next.add(g.id);
      if (!tagsByGroup[g.id]) {
        try {
          tagsByGroup[g.id] = await api.listTags({ groupId: g.id, withCounts: true });
        } catch (e: any) {
          loadError = e?.message ?? 'Failed to load tags';
        }
      }
    }
    expandedGroups = next;
  }

  function pickTag(tag: Tag) {
    filterStore.requestAdd({
      type: 'tag',
      value: tag.id,
      label: tag.name,
      tagGroupName: tag.tagGroupName
    });
  }


  // ✎ on tag-tree row.
  let editTagModalShow = $state(false);
  let editingTag = $state<Tag | null>(null);
  async function openTagEdit(tag: Tag, e: Event) {
    e.stopPropagation();
    e.preventDefault();
    editingTag = tag;
    editTagModalShow = true;
  }
  async function onTagSavedFromSidebar(saved: Tag) {
    // Refresh that group's tag list so name/aliases reflect.
    if (tagsByGroup[saved.tagGroupId]) {
      tagsByGroup[saved.tagGroupId] = await api.listTags({
        groupId: saved.tagGroupId,
        withCounts: true
      });
    }
    // The favorite flag may have been toggled — refresh the top section.
    await loadFavorites();
  }

  function pickStatus(status: 'needsReview' | 'wontPlay' | 'markedForDeletion' | 'favorite', label: string) {
    filterStore.requestAdd({ type: 'status', value: status, label });
  }

  function pickFolder(s: VideoSet) {
    filterStore.requestAdd({ type: 'folder', value: s.path, label: s.name });
  }

  function pickMissing(g: TagGroup) {
    // Wire value is "tagGroup:<groupId>" — see types.ts FilterRef.
    filterStore.requestAdd({
      type: 'missing',
      value: `tagGroup:${g.id}`,
      label: `Missing ${g.name}`
    });
  }

  function open(v: Video) {
    playingVideo = v;
  }

  // Move to the next/previous video in the current grid order. Wired into
  // VideoPlayer's arrow-key handlers; the player gates by `tagsPanelOpen`
  // so plain arrows nav when the panel is closed and Shift+arrows do when
  // it's open.
  function goNext() {
    if (!playingVideo) return;
    const idx = videos.findIndex(v => v.id === playingVideo!.id);
    if (idx >= 0 && idx + 1 < videos.length) playingVideo = videos[idx + 1];
  }
  function goPrev() {
    if (!playingVideo) return;
    const idx = videos.findIndex(v => v.id === playingVideo!.id);
    if (idx > 0) playingVideo = videos[idx - 1];
  }

  async function refreshPlaying() {
    if (!playingVideo) return;
    const fresh = await api.getVideo(playingVideo.id);
    if (fresh) playingVideo = fresh;
    await refreshVideos();
  }

  onMount(loadSidebar);
</script>

<svelte:head><title>Videos - Video Organizer</title></svelte:head>

<div class="flex flex-col gap-4">
  <h1 class="text-2xl font-semibold">Videos</h1>

  {#if loadError}
    <div class="alert alert-error">
      <span>{loadError}</span>
      <button class="btn btn-sm" onclick={() => (loadError = null)}>Dismiss</button>
    </div>
  {/if}

  <div class="grid grid-cols-1 lg:grid-cols-[280px_1fr] gap-4">
    <!-- Sidebar -->
    <!-- Filter sidebar — sticky-pinned to the viewport top so it stays
         visible while the user scrolls thumbnails on the right. The
         max-height + internal overflow-y-auto means tall sidebars
         (lots of expanded tag groups) get their own scroll bar instead
         of pushing past the viewport. The z-index keeps it above the
         sticky video player when the user has scrolled both into the
         same vertical band. -->
    <aside
      class="card bg-base-200 p-3 space-y-3 sticky top-0 z-20 max-h-screen overflow-y-auto"
    >
      <!-- Global search: scans tag names + aliases, system statuses, and
           Missing-<group>. Each row labels its source so the user can tell
           "Bob Marley · Performer" apart from "Won't Play · System". -->
      <div>
        <input
          type="text"
          class="input input-bordered input-sm w-full"
          placeholder="Search tags, status, missing…"
          bind:value={searchQuery}
          autocomplete="off"
        />
        {#if searchQuery.trim()}
          <div class="bg-base-100 rounded-box mt-1 max-h-64 overflow-auto border border-base-300">
            {#each searchResults as r, i (i)}
              <button
                type="button"
                class="w-full text-left px-2 py-1 hover:bg-base-200 flex items-center justify-between gap-2 text-sm"
                onclick={() => applySearchResult(r)}
              >
                <span class="truncate">
                  {#if r.kind === 'tag'}
                    {r.tag.name}{#if r.matchedAlias}<span class="text-xs text-base-content/50 ml-1">(alias: {r.matchedAlias})</span>{/if}
                  {:else if r.kind === 'status'}
                    {r.label}
                  {:else}
                    Missing {r.group.name}
                  {/if}
                </span>
                <span class="text-xs text-base-content/50 shrink-0 italic">
                  {#if r.kind === 'tag'}{r.tag.tagGroupName}
                  {:else}System
                  {/if}
                </span>
              </button>
            {/each}
            {#if searchResults.length === 0}
              <div class="px-2 py-1 text-sm text-base-content/50 italic">No matches</div>
            {/if}
          </div>
        {/if}
      </div>

      <div>
        <div class="flex items-center justify-between mb-1">
          <h2 class="font-semibold text-sm">Filter</h2>
          {#if !filterStore.isEmpty()}
            <button class="btn btn-xs" onclick={() => filterStore.clear()}>Clear</button>
          {/if}
        </div>
        {#if filterStore.required.length > 0}
          <div class="text-xs text-base-content/60 mt-1">Required</div>
          <div class="flex flex-wrap gap-1 mb-1">
            {#each filterStore.required as t (`req-${t.type}-${t.value}`)}
              <span class="badge {filterSlotClass('required')} gap-1">
                {t.label}
                <button onclick={() => filterStore.remove(t)}>×</button>
              </span>
            {/each}
          </div>
        {/if}
        {#if filterStore.optional.length > 0}
          <div class="text-xs text-base-content/60 mt-1">Optional</div>
          <div class="flex flex-wrap gap-1 mb-1">
            {#each filterStore.optional as t (`opt-${t.type}-${t.value}`)}
              <span class="badge {filterSlotClass('optional')} gap-1">
                {t.label}
                <button onclick={() => filterStore.remove(t)}>×</button>
              </span>
            {/each}
          </div>
        {/if}
        {#if filterStore.excluded.length > 0}
          <div class="text-xs text-base-content/60 mt-1">Excluded</div>
          <div class="flex flex-wrap gap-1 mb-1">
            {#each filterStore.excluded as t (`exc-${t.type}-${t.value}`)}
              <span class="badge {filterSlotClass('excluded')} gap-1">
                {t.label}
                <button onclick={() => filterStore.remove(t)}>×</button>
              </span>
            {/each}
          </div>
        {/if}
      </div>

      <div>
        <h3 class="font-semibold text-sm mb-1">Status</h3>
        <div class="flex flex-wrap gap-1 bg-base-100 rounded-box p-2">
          <button
            type="button"
            class="inline-block px-2 py-0.5 rounded-full text-xs border cursor-pointer"
            style="background-color: rgb(168 162 158 / 0.20); border-color: rgb(168 162 158 / 0.45); color: rgb(253 224 71);"
            onclick={() => pickStatus('favorite', 'Favorite')}
          >★ Favorite</button>
          <button
            type="button"
            class="inline-block px-2 py-0.5 rounded-full text-xs border cursor-pointer"
            style="background-color: rgb(168 162 158 / 0.20); border-color: rgb(168 162 158 / 0.45); color: rgb(214 211 209);"
            onclick={() => pickStatus('needsReview', 'Needs Review')}
          >Needs Review</button>
          <button
            type="button"
            class="inline-block px-2 py-0.5 rounded-full text-xs border cursor-pointer"
            style="background-color: rgb(249 115 22 / 0.20); border-color: rgb(249 115 22 / 0.45); color: rgb(253 186 116);"
            onclick={() => pickStatus('wontPlay', "Won't Play")}
          >Won't Play</button>
          <button
            type="button"
            class="inline-block px-2 py-0.5 rounded-full text-xs border cursor-pointer"
            style="background-color: rgb(239 68 68 / 0.20); border-color: rgb(239 68 68 / 0.45); color: rgb(252 165 165);"
            onclick={() => pickStatus('markedForDeletion', 'To Delete')}
          >To Delete</button>
        </div>
      </div>

      {#if favoriteTags.length > 0}
        <div>
          <h3 class="font-semibold text-sm mb-1 flex items-center gap-1">
            <svg viewBox="0 0 24 24" class="h-4 w-4"
              fill="rgb(234 179 8)"
              stroke="rgb(255 255 255 / 0.85)" stroke-width="1.25" stroke-linejoin="round">
              <path d="M12 2.5 L14.6 8.9 L21.5 9.5 L16.2 14.1 L17.8 20.9 L12 17.3 L6.2 20.9 L7.8 14.1 L2.5 9.5 L9.4 8.9 Z" />
            </svg>
            Favorite Tags
          </h3>
          <div class="flex flex-wrap gap-1 bg-base-100 rounded-box p-2">
            {#each favoriteTags as t (t.id)}
              {@const slot = filterSlot(t.id)}
              <span
                class="badge {pillClass(t.id, t.tagGroupName)} gap-1"
                title={slot ? `In filter: ${slot}` : `Filter by ${t.tagGroupName}: ${t.name}`}
              >
                <button
                  type="button"
                  class="cursor-pointer"
                  onclick={() => pickTag(t)}
                >{t.name}</button>
                <span class="opacity-60 text-xs tabular-nums">{t.videoCount}</span>
                <button
                  type="button"
                  class="opacity-70 hover:opacity-100"
                  onclick={(e) => openTagEdit(t, e)}
                  title="Edit tag"
                  aria-label="Edit {t.name}"
                >✎</button>
              </span>
            {/each}
          </div>
        </div>
      {/if}

      {#if groups.length > 0}
        <div>
          <h3 class="font-semibold text-sm mb-1">Missing tags from…</h3>
          <div class="flex flex-wrap gap-1 bg-base-100 rounded-box p-2">
            {#each groups as g (g.id)}
              <button
                type="button"
                class="badge badge-tag-missing cursor-pointer"
                onclick={() => pickMissing(g)}
                title="Show videos with no tag in {g.name}"
              >{g.name}</button>
            {/each}
          </div>
        </div>
      {/if}

      {#if sets.length > 0}
        <div>
          <h3 class="font-semibold text-sm mb-1">Folders</h3>
          <ul class="menu menu-sm bg-base-100 rounded-box">
            {#each sets as s (s.id)}
              <li><button onclick={() => pickFolder(s)}>{s.name}</button></li>
            {/each}
          </ul>
        </div>
      {/if}

      <div>
        <h3 class="font-semibold text-sm mb-1">Tag Groups</h3>
        <div class="bg-base-100 rounded-box divide-y divide-base-300">
          {#each groups as g (g.id)}
            <details open={expandedGroups.has(g.id)} class="px-2 py-1">
              <summary
                class="cursor-pointer flex items-center justify-between text-sm font-medium py-1"
                onclick={(e) => { e.preventDefault(); toggleGroup(g); }}
              >
                <span>{g.name}</span>
                <span class="badge badge-sm">{g.tagCount}</span>
              </summary>
              {#if expandedGroups.has(g.id)}
                <div class="flex flex-wrap gap-1 mt-1">
                  {#each tagsByGroup[g.id] ?? [] as t (t.id)}
                    {@const slot = filterSlot(t.id)}
                    <span
                      class="badge {pillClass(t.id, g.name)} gap-1"
                      title={slot ? `In filter: ${slot}` : `Filter by ${g.name}: ${t.name}`}
                    >
                      <button
                        type="button"
                        class="cursor-pointer"
                        onclick={() => pickTag(t)}
                      >{t.name}</button>
                      <span class="opacity-60 text-xs tabular-nums">{t.videoCount}</span>
                      <button
                        type="button"
                        class="opacity-70 hover:opacity-100"
                        onclick={(e) => openTagEdit(t, e)}
                        title="Edit tag"
                        aria-label="Edit {t.name}"
                      >✎</button>
                    </span>
                  {/each}
                  {#if (tagsByGroup[g.id] ?? []).length === 0}
                    <span class="text-xs text-base-content/50">No tags yet.</span>
                  {/if}
                </div>
              {/if}
            </details>
          {/each}
        </div>
      </div>
    </aside>

    <!-- Main area: natural document flow so the thumbnails pane bumps
         down as the player grows (drag-resize divider OR the video's
         own aspect ratio pushing the box taller). The player is
         sticky-pinned to the page top so it stays visible while the
         user scrolls thumbnails. EditTagsPanel docks as an absolute-
         positioned hover strip on the right edge of this section so
         toggling it doesn't reflow the player's width. -->
    <section class="relative flex flex-col">
      {#if playingVideo}
        <!-- Player area — wrapper sticks to the page top so it doesn't
             scroll away while the user browses thumbs. `min-height`
             tracks the divider so dragging down enlarges the wrapper
             (and lets the video grow into the new room). The video's
             own `max-height` is wired to the same divider value via
             `maxVideoHeightPx` below — that's what makes dragging UP
             actually shrink the picture. Without that wiring the video
             holds at its 70vh default no matter how high you drag.
             Subtract ~72px of wrapper chrome (p-3 + button row + gap)
             so the picture fits cleanly inside the wrapper. -->
        <div
          class="card bg-base-200 p-3 sticky top-0 z-10"
          style="min-height: {playerHeight}px;"
        >
          <VideoPlayer
            bind:video={playingVideo}
            shortcutsEnabled={true}
            maxVideoHeightPx={Math.max(100, playerHeight - 72)}
            tagsPanelOpen={showEditTagsPanel}
            onToggleTags={() => (showEditTagsPanel = !showEditTagsPanel)}
            onRequestNext={goNext}
            onRequestPrev={goPrev}
          />
          <div class="flex justify-end gap-2 mt-2">
            <button class="btn btn-sm" onclick={() => (showEditTagsPanel = !showEditTagsPanel)}>
              {showEditTagsPanel ? 'Close Tags' : 'Tags'}
            </button>
            <button class="btn btn-sm" onclick={() => (playingVideo = null)}>Close Player</button>
          </div>
        </div>

        <!-- Drag handle: 12px hit area, visible 4px bar with three grip
             dots. Pointer-event-based drag with setPointerCapture so the
             cursor can leave the handle mid-drag. Double-click resets to
             the default height. Stays in normal flow (not sticky) —
             it's an occasional control. -->
        <!-- svelte-ignore a11y_no_noninteractive_tabindex -->
        <div
          role="separator"
          aria-orientation="horizontal"
          aria-label="Resize player (double-click to reset)"
          tabindex="0"
          class="group relative h-3 shrink-0 cursor-ns-resize select-none w-full touch-none"
          title="Drag to resize · double-click to reset"
          onpointerdown={startDrag}
          onpointermove={onDragMove}
          onpointerup={endDrag}
          onpointercancel={endDrag}
          ondblclick={() => { playerHeight = PLAYER_HEIGHT_DEFAULT; localStorage.setItem('browsePlayerHeight', String(PLAYER_HEIGHT_DEFAULT)); }}
        >
          <span
            class="absolute inset-x-0 top-1/2 -translate-y-1/2 h-1 rounded-full transition-colors pointer-events-none
                   {dragging ? 'bg-primary' : 'bg-base-300 group-hover:bg-primary/60'}"
          ></span>
          <span
            class="absolute top-1/2 left-1/2 -translate-x-1/2 -translate-y-1/2 flex gap-1 pointer-events-none"
            aria-hidden="true"
          >
            <span class="w-1 h-1 rounded-full bg-base-content/60"></span>
            <span class="w-1 h-1 rounded-full bg-base-content/60"></span>
            <span class="w-1 h-1 rounded-full bg-base-content/60"></span>
          </span>
        </div>
      {/if}

      <!-- Header row — count, loading indicator, thumb-size slider,
           reshuffle. flex-wrap so the slider drops to its own row on
           narrow viewports instead of squeezing into nothing. -->
      <div class="flex justify-between items-center shrink-0 mb-2 mt-2 gap-3 flex-wrap">
        <p class="text-sm text-base-content/70">
          {#if videosLoading}
            Loading…
          {:else}
            {videos.length} videos{#if visibleCount < videos.length} · showing {visibleCount}{/if}
          {/if}
        </p>
        <div class="flex items-center gap-3 ml-auto">
          <!-- Thumbnail size slider. Smaller icon on the left, larger
               on the right so the direction is intuitive. The grid uses
               auto-fill so changing this value reflows columns live. -->
          <label class="flex items-center gap-2 text-xs text-base-content/60" title="Thumbnail size">
            <svg viewBox="0 0 24 24" class="h-3 w-3 fill-current"><rect x="6" y="6" width="12" height="12" rx="1"/></svg>
            <input
              type="range"
              class="range range-xs w-32"
              min={THUMB_WIDTH_MIN}
              max={THUMB_WIDTH_MAX}
              step="20"
              bind:value={thumbWidth}
              aria-label="Thumbnail size"
            />
            <svg viewBox="0 0 24 24" class="h-5 w-5 fill-current"><rect x="2" y="2" width="20" height="20" rx="2"/></svg>
          </label>
          {#if videosLoading}<span class="loading loading-dots loading-sm"></span>{/if}
          <button
            type="button"
            class="btn btn-sm"
            disabled={videos.length === 0 || videosLoading}
            onclick={reshufflePlaylist}
            title="Shuffle the current grid into a new random playlist and jump to the first video"
          >🔀 Reshuffle</button>
        </div>
      </div>

      <!-- Thumbnail grid — natural document flow now. Page scrolls when
           total content exceeds viewport; the sticky player above stays
           pinned at the top of the viewport during that scroll. The
           grid uses auto-fill + minmax so changing the thumbnail-size
           slider above immediately reflows the column count. -->
      <div>
        <div
          class="grid gap-3"
          style="grid-template-columns: repeat(auto-fill, minmax({thumbWidth}px, 1fr));"
        >
          {#each visibleVideos as v (v.id)}
            <VideoCard
              video={v}
              onopen={open}
              active={playingVideo?.id === v.id}
            />
          {/each}
        </div>

        <!-- Sentinel: empty div the IntersectionObserver watches for to
             load the next chunk. Lives inside the scroll region so its
             intersections fire against the right root. -->
        {#if visibleCount < videos.length}
          <div bind:this={scrollSentinelEl} class="h-12 flex items-center justify-center text-xs text-base-content/50">
            Loading more… ({videos.length - visibleCount} remaining)
          </div>
        {/if}

        {#if !videosLoading && videos.length === 0}
          <p class="text-base-content/60">No videos match the current filter.</p>
        {/if}
      </div>

      <!-- Hover-expandable Tags panel docked on the right edge of the
           section. Default state is a thin 12-px strip with a vertical
           "TAGS" label; hovering or focusing inside grows it to 360px
           and reveals the full EditTagsPanel content. The panel itself
           is generic — the strip styling lives entirely here so the
           panel keeps its standalone shape for other pages. The wrapper
           is absolutely positioned so toggling it on/off doesn't push
           the player or thumbnail grid around. -->
      {#if showEditTagsPanel && playingVideo}
        <div
          class="tags-strip absolute top-0 right-0 bottom-0 z-20 flex items-stretch shadow-xl overflow-hidden bg-base-200 border-l border-base-300 transition-[width] duration-150 ease-out"
        >
          <div
            class="strip-label w-3 shrink-0 bg-primary/20 hover:bg-primary/30 border-r border-base-300 flex items-center justify-center text-[10px] font-semibold uppercase tracking-widest text-base-content/70 select-none cursor-ew-resize transition-opacity"
            style="writing-mode: vertical-rl; text-orientation: mixed;"
            aria-hidden="true"
          >Tags</div>
          <div class="strip-body flex-1 min-w-0 overflow-y-auto">
            <EditTagsPanel
              bind:video={playingVideo}
              bind:show={showEditTagsPanel}
              onAfterSave={refreshPlaying}
            />
          </div>
        </div>
      {/if}
    </section>
  </div>
</div>

<FilterDialog />
<TagEditModal bind:show={editTagModalShow} tag={editingTag} onSaved={onTagSavedFromSidebar} />

<style>
  /* Hover-expandable Tags strip. Tailwind can't drive a transition
     through arbitrary widths via utility classes alone, so the
     collapsed/expanded sizes live here. The body fades alongside the
     width so half-width frames during the transition aren't visually
     jarring. */
  .tags-strip {
    width: 1rem;
  }
  .tags-strip:hover,
  .tags-strip:focus-within {
    width: 360px;
  }
  .tags-strip .strip-body {
    opacity: 0;
    transition: opacity 120ms ease-out 30ms;
    pointer-events: none;
  }
  .tags-strip:hover .strip-body,
  .tags-strip:focus-within .strip-body {
    opacity: 1;
    pointer-events: auto;
  }
  /* Subtle accent on the label when the user hasn't expanded yet so
     it reads as an interactive handle, not a static border. */
  .tags-strip:hover .strip-label,
  .tags-strip:focus-within .strip-label {
    opacity: 0.4;
  }
</style>

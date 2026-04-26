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

  function shuffleInPlace<T>(arr: T[]): T[] {
    const a = [...arr];
    for (let i = a.length - 1; i > 0; i--) {
      const j = Math.floor(Math.random() * (i + 1));
      [a[i], a[j]] = [a[j], a[i]];
    }
    return a;
  }

  // User-triggered reshuffle of the current grid into a new random order
  // and jump to the new first video.
  function reshufflePlaylist() {
    if (videos.length === 0) return;
    videos = shuffleInPlace(videos);
    playingVideo = videos[0];
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
    <aside class="card bg-base-200 p-3 space-y-3 h-fit">
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

    <!-- Main area -->
    <section class="space-y-3">
      {#if playingVideo}
        <div class="grid grid-cols-1 {showEditTagsPanel ? 'lg:grid-cols-[1fr_360px]' : ''} gap-3">
          <div class="card bg-base-200 p-3">
            <VideoPlayer
              bind:video={playingVideo}
              shortcutsEnabled={true}
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
          {#if showEditTagsPanel}
            <EditTagsPanel
              bind:video={playingVideo}
              bind:show={showEditTagsPanel}
              onAfterSave={refreshPlaying}
            />
          {/if}
        </div>
      {/if}

      <div class="flex justify-between items-center">
        <p class="text-sm text-base-content/70">
          {videosLoading ? 'Loading…' : `${videos.length} videos`}
        </p>
        <div class="flex items-center gap-2">
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

      <div class="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6 gap-3">
        {#each videos as v (v.id)}
          <VideoCard
            video={v}
            onopen={open}
            active={playingVideo?.id === v.id}
          />
        {/each}
      </div>

      {#if !videosLoading && videos.length === 0}
        <p class="text-base-content/60">No videos match the current filter.</p>
      {/if}
    </section>
  </div>
</div>

<FilterDialog />
<TagEditModal bind:show={editTagModalShow} tag={editingTag} onSaved={onTagSavedFromSidebar} />

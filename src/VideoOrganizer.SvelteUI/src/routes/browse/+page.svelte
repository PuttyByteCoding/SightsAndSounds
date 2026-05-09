<script lang="ts">
  // Simplified Browse page rewritten for the unified tag model. Lists every
  // video filtered by tag + status + folder filters. Plays inline with
  // the existing VideoPlayer and offers the generic EditTagsPanel.
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { api } from '$lib/api';
  import type {
    Video,
    VideoSet,
    Tag,
    TagGroup,
    FilterRef,
    ImportBrowseDirectory,
    VideoTagSummary,
    FlagCounts
  } from '$lib/types';
  import VideoCard from '$lib/components/VideoCard.svelte';
  import VideoPlayer from '$lib/components/VideoPlayer.svelte';
  import EditTagsPanel from '$lib/components/EditTagsPanel.svelte';
  import FileInfoPanel from '$lib/components/FileInfoPanel.svelte';
  import FilterDialog from '$lib/components/FilterDialog.svelte';
  import TagEditModal from '$lib/components/TagEditModal.svelte';
  import FolderTreeNode from '$lib/components/FolderTreeNode.svelte';
  import RemoteHostBanner from '$lib/components/RemoteHostBanner.svelte';
  import { filterStore } from '$lib/filterStore.svelte';
  import { pillClass, filterSlot, filterSlotClass } from '$lib/tagColors';

  let videos = $state<Video[]>([]);
  let videosLoading = $state(false);
  let loadError = $state<string | null>(null);

  let groups = $state<TagGroup[]>([]);
  let tagsByGroup = $state<Record<string, Tag[]>>({});
  let expandedGroups = $state<Set<string>>(new Set());

  let sets = $state<VideoSet[]>([]);
  // Annotated source roots used to seed the Folders tree. Comes from
  // /api/import/browse with no path — same recursive video-count and
  // imported-count numbers each subfolder gets on expand. Falls back
  // to a sets-derived stub if the call fails so the tree still works
  // (just without count badges at the root level).
  let folderRoots = $state<ImportBrowseDirectory[]>([]);
  // True when folderRoots came from a real browseImport response;
  // false in the fallback. Gates the "only folders with imports"
  // filter — applying it to fallback stubs (importedCount=0 for
  // every root) would hide the whole tree.
  let folderRootsAnnotated = $state(false);
  // Tree shows only folders that contain at least one imported
  // video. importedCount is recursive server-side, so dropping a
  // zero-import root prunes the whole subtree safely.
  const visibleFolderRoots = $derived(
    folderRootsAnnotated
      ? folderRoots.filter(r => r.importedCount > 0)
      : folderRoots
  );

  let playingVideo = $state<Video | null>(null);
  let showEditTagsPanel = $state(false);
  // I-key toggles a read-only File Info side panel. Independent of
  // the Tags panel: either or both can be open at once.
  let showFileInfo = $state(false);
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

  // --- Section-level collapse ------------------------------------------
  // Each tree-style section in the sidebar (Flags, Favorite Tags,
  // Folders, Tag Groups) carries a chevron next to its title so the
  // user can collapse it. The body hides; the chevron rotates; the
  // heading stays so the user can re-open it. Persisted per section
  // to localStorage.
  type SectionKey = 'flags' | 'favorites' | 'related' | 'folders' | 'tagGroups';
  const SECTION_KEYS: readonly SectionKey[] = ['flags', 'favorites', 'related', 'folders', 'tagGroups'];
  let sectionCollapsed = $state<Record<SectionKey, boolean>>({
    flags: false,
    favorites: false,
    related: false,
    folders: false,
    tagGroups: false
  });
  function toggleSection(k: SectionKey) {
    sectionCollapsed[k] = !sectionCollapsed[k];
    if (typeof localStorage !== 'undefined') {
      localStorage.setItem(`browseSection_${k}_collapsed`, sectionCollapsed[k] ? '1' : '0');
    }
  }

  onMount(() => {
    const stored = Number(localStorage.getItem('browsePlayerHeight'));
    if (Number.isFinite(stored) && stored >= PLAYER_HEIGHT_MIN) {
      playerHeight = stored;
    }
    const storedThumb = Number(localStorage.getItem('browseThumbWidth'));
    if (Number.isFinite(storedThumb) && storedThumb >= THUMB_WIDTH_MIN && storedThumb <= THUMB_WIDTH_MAX) {
      thumbWidth = storedThumb;
    }
    sidebarCollapsed = localStorage.getItem('browseSidebarCollapsed') === '1';
    for (const k of SECTION_KEYS) {
      sectionCollapsed[k] = localStorage.getItem(`browseSection_${k}_collapsed`) === '1';
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
    // Empty-install case: loadSidebar has navigated to /import. Skip
    // the filter call so we don't pop a transient error banner during
    // the navigation.
    if (isEmptyInstall) return;
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

  // Once the initial sidebar load decides the DB is empty (no
  // VideoSets configured) we skip the filterVideos call entirely and
  // bounce to the import page. Tracked so the $effect that re-fetches
  // on filter changes (below) doesn't kick off an /api/videos/filter
  // request before the navigation completes.
  let isEmptyInstall = $state(false);

  // Filter-tree collapse state. When collapsed the sidebar shrinks to
  // a thin column with just a re-open chevron, freeing the entire row
  // for the player + thumbnails. Persisted so the choice survives
  // navigation away and back.
  let sidebarCollapsed = $state(false);
  function toggleSidebar() {
    sidebarCollapsed = !sidebarCollapsed;
    if (typeof localStorage !== 'undefined') {
      localStorage.setItem('browseSidebarCollapsed', sidebarCollapsed ? '1' : '0');
    }
  }

  async function loadSidebar() {
    try {
      // VideoSets must load FIRST so the empty-install redirect fires
      // before we try other sidebar fetches (tag-groups, tags). Those
      // can also fail on a fresh DB if seeding hasn't completed, and
      // there's no point loading them when we're about to navigate
      // away anyway. Two redirect cases collapse into the same code
      // path: no VideoSets configured, OR sets exist but no videos
      // have been imported yet — in both, /browse has nothing to
      // show, so send the user to /import.
      sets = await api.listVideoSets();
      if (sets.length === 0) {
        isEmptyInstall = true;
        goto('/import', { replaceState: true });
        return;
      }
      const totalVideos = await api.getVideoCount();
      if (totalVideos === 0) {
        // Sets exist but the library is empty — same destination.
        // replaceState keeps the back-button history clean so the
        // user doesn't get bounced /browse → /import → /browse.
        isEmptyInstall = true;
        goto('/import', { replaceState: true });
        return;
      }
      groups = await api.listTagGroups();
      await loadFavorites();
      void refreshFlagCounts();
      // Fetch annotated source roots so the Folders tree shows
      // import-progress badges at the root level (and we get the
      // real `hasSubdirectories` flag rather than an always-on
      // chevron). Failures here are non-fatal — the user can still
      // browse the tree, just without count badges on the roots.
      try {
        const browse = await api.browseImport();
        folderRoots = browse.directories;
        folderRootsAnnotated = true;
      } catch (e: any) {
        // Stub out roots from `sets` so the tree still renders.
        // videoCount=0 suppresses the badge; chevron defaults true
        // to preserve the affordance — the per-folder browseImport
        // call on expand will surface real children if any exist.
        // folderRootsAnnotated stays false so the imports-only
        // filter doesn't accidentally hide every root.
        folderRoots = sets.map(s => ({
          name: s.name,
          fullPath: s.path,
          hasSubdirectories: true,
          videoCount: 0,
          importedCount: 0
        }));
        folderRootsAnnotated = false;
      }
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

  // Refresh sidebar tag-attachment counts after any save that could
  // change them — a panel save, a video nav-save with dirty tagIds, or
  // a tag-create/update from the modal. One round-trip via
  // loadFavorites() picks up every tag's videoCount; we then re-bucket
  // the result into the per-group caches that the expanded
  // tag-tree section displays. Unexpanded groups stay empty until
  // toggleGroup() loads them lazily — no need to fetch what isn't
  // showing.
  async function refreshSidebarTagCounts() {
    await loadFavorites();
    for (const groupId of Object.keys(tagsByGroup)) {
      tagsByGroup[groupId] = allTags.filter(t => t.tagGroupId === groupId);
    }
    // Flags can flip in lockstep with tag saves (e.g. R toggles
    // NeedsReview, the player nav-save can flip various flags) —
    // refresh the badge counts here so they don't go stale.
    void refreshFlagCounts();
  }

  // ---- Global filter search ----
  // One search box that scans tag names + aliases, system statuses, and
  // "Missing <group>" entries. Each result tags itself with its source so
  // the row label reads e.g. "Bob Marley · Performer" or "Playback Issue · System".
  let searchQuery = $state('');
  type SearchResult =
    | { kind: 'tag'; tag: Tag; matchedAlias?: string }
    | { kind: 'status'; value: 'favorite' | 'needsReview' | 'playbackIssue' | 'markedForDeletion'; label: string }
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
      { value: 'playbackIssue', label: "Playback Issue" },
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
  async function onTagSavedFromSidebar(_saved: Tag) {
    // Re-pull groups for the tagCount badge that counts tags-per-group
    // (only changes when tags are created/deleted). Then refresh
    // allTags + every expanded group cache via the shared helper so
    // pill names, aliases, favorites, and per-tag video counts are
    // all in sync after a create/edit.
    groups = await api.listTagGroups();
    await refreshSidebarTagCounts();
  }

  function pickStatus(status: 'needsReview' | 'playbackIssue' | 'markedForDeletion' | 'favorite', label: string) {
    // Status hits from the search box still go through the picker
    // dialog (Required / Optional / Excluded). The Flags tree skips
    // the dialog and uses applyFlag below; both call paths can
    // coexist because they end up writing to the same filterStore
    // keys (`status::<value>`).
    filterStore.requestAdd({ type: 'status', value: status, label });
  }

  // --- Flags tree -----------------------------------------------------
  // The Flags tree mixes two kinds of items:
  //   • Built-in boolean flags on the Video entity (Favorite,
  //     Needs Review, Playback Issue, To Delete) — these write `status`
  //     filters.
  //   • Tags that live in a tag group named "Flags" (case-insensitive)
  //     — these write `tag` filters but use the same three-option UI
  //     so the user has one place for everything that's True/False.
  // Both kinds expand to three leaf options:
  //   No Filter → remove from every bucket
  //   True      → put in Required (video must have it set)
  //   False     → put in Excluded (video must NOT have it set)
  // We skip the picker dialog entirely; filterStore.apply() routes
  // straight into the chosen bucket, idempotently replacing whatever
  // bucket the same item was in before.
  type FlagValue = 'favorite' | 'needsReview' | 'playbackIssue' | 'markedForDeletion';
  interface FlagDef { value: FlagValue; label: string; }
  const FLAG_DEFS: FlagDef[] = [
    { value: 'favorite',          label: 'Favorite' },
    { value: 'needsReview',       label: 'Needs Review' },
    { value: 'playbackIssue',          label: "Playback Issue" },
    { value: 'markedForDeletion', label: 'To Delete' }
  ];

  // Per-flag total counts, refreshed on initial sidebar load and
  // after any save that could flip a flag (refreshSidebarTagCounts).
  // Rendered as a small muted count to the right of each flag row in
  // the Flags tree so the user can see how many videos would match
  // before applying the filter.
  let flagCounts = $state<FlagCounts>({
    favorite: 0,
    needsReview: 0,
    playbackIssue: 0,
    markedForDeletion: 0
  });
  async function refreshFlagCounts() {
    try {
      flagCounts = await api.getFlagCounts();
    } catch {
      // Non-fatal — leave existing counts (or zeros) in place. The
      // sidebar still works; the badges just go stale until the
      // next successful refresh.
    }
  }

  type FlagItem =
    | { kind: 'bool'; def: FlagDef }
    | { kind: 'tag'; tag: Tag };

  // Stable identifier per flag item — used both for expansion state
  // and for keyed `{#each}` rendering. Built-in flags are namespaced
  // separately from tag flags so the two can never collide.
  function flagItemKey(item: FlagItem): string {
    return item.kind === 'bool' ? `bool:${item.def.value}` : `tag:${item.tag.id}`;
  }
  function flagItemLabel(item: FlagItem): string {
    return item.kind === 'bool' ? item.def.label : item.tag.name;
  }

  // Reads filterStore to determine which of the three options is
  // currently active for a flag item, used both for the inline state
  // indicator on each row and as the input to cycleFlag below.
  // Returns 'true' / 'false' / 'nofilter'.
  function flagState(item: FlagItem): 'true' | 'false' | 'nofilter' {
    if (item.kind === 'bool') {
      const v = item.def.value;
      if (filterStore.required.some(t => t.type === 'status' && t.value === v)) return 'true';
      if (filterStore.excluded.some(t => t.type === 'status' && t.value === v)) return 'false';
    } else {
      const id = item.tag.id;
      if (filterStore.required.some(t => t.type === 'tag' && t.value === id)) return 'true';
      if (filterStore.excluded.some(t => t.type === 'tag' && t.value === id)) return 'false';
    }
    return 'nofilter';
  }

  function applyFlag(item: FlagItem, opt: 'nofilter' | 'true' | 'false') {
    const tag = item.kind === 'bool'
      ? { type: 'status' as const, value: item.def.value, label: item.def.label }
      : {
          type: 'tag' as const,
          value: item.tag.id,
          label: item.tag.name,
          tagGroupName: item.tag.tagGroupName
        };
    if (opt === 'nofilter') filterStore.remove(tag);
    else if (opt === 'true') filterStore.apply(tag, 'required');
    else filterStore.apply(tag, 'excluded');
  }

  // Tristate-cycle handler. Each flag row in the Flags tree is a
  // single button that advances the filter state on every click —
  // Any → True → False → Any. Compact one-row-per-flag UI (Option A
  // from the patterns review) instead of expand-then-pick.
  function cycleFlag(item: FlagItem) {
    const cur = flagState(item);
    const next = cur === 'nofilter' ? 'true' : cur === 'true' ? 'false' : 'nofilter';
    applyFlag(item, next);
  }
  function flagStateLabel(s: 'true' | 'false' | 'nofilter'): string {
    return s === 'true' ? 'True' : s === 'false' ? 'False' : 'Any';
  }

  // The "Flags" tag group, if the user has defined one (case-
  // insensitive name match). Its tags are surfaced inline in the
  // Flags tree and hidden from the regular Tag Groups tree so
  // boolean-style attributes live in one place.
  const flagsGroup = $derived(
    groups.find(g => g.name.trim().toLowerCase() === 'flags') ?? null
  );
  // Tags that belong to the Flags group, sorted by name. Pulled from
  // the cached `allTags` list (loadFavorites loads everything once)
  // so no extra round-trip is needed.
  const flagsGroupTags = $derived(
    flagsGroup
      ? allTags
          .filter(t => t.tagGroupId === flagsGroup.id)
          .slice()
          .sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name))
      : []
  );
  // FlagItem list used by the Flags tree template. Built-in boolean
  // flags first, tag-flags second.
  const flagItems = $derived<FlagItem[]>([
    ...FLAG_DEFS.map(def => ({ kind: 'bool' as const, def })),
    ...flagsGroupTags.map(tag => ({ kind: 'tag' as const, tag }))
  ]);
  // Tag Groups tree skips the Flags group since its tags already
  // appear in the Flags tree above.
  const nonFlagsGroups = $derived(
    flagsGroup ? groups.filter(g => g.id !== flagsGroup.id) : groups
  );

  // --- Related Tags ---------------------------------------------------
  // Aggregates every tag that appears on at least one video in the
  // currently filtered grid, grouped by TagGroup. Each entry carries
  // a count = number of videos in the filtered set that have it.
  // Useful for refinement: "Bob Marley shows up in 50 videos; what
  // other Albums / Years / Performers are co-tagged with him?"
  // Computed client-side from the existing `videos` state — no extra
  // round-trip — and re-derives whenever the filtered list changes.
  // The Flags group is excluded since its tags already live in the
  // Flags tree.
  interface RelatedTagEntry { tag: VideoTagSummary; count: number; }
  interface RelatedTagGroup { groupId: string; groupName: string; tags: RelatedTagEntry[]; total: number; }
  const relatedTagsByGroup = $derived.by<RelatedTagGroup[]>(() => {
    const flagsGroupId = flagsGroup?.id;
    const buckets = new Map<string, { groupName: string; tags: Map<string, RelatedTagEntry> }>();
    for (const v of videos) {
      for (const t of v.tags) {
        if (t.tagGroupId === flagsGroupId) continue;
        let bucket = buckets.get(t.tagGroupId);
        if (!bucket) {
          bucket = { groupName: t.tagGroupName, tags: new Map() };
          buckets.set(t.tagGroupId, bucket);
        }
        const existing = bucket.tags.get(t.id);
        if (existing) existing.count++;
        else bucket.tags.set(t.id, { tag: t, count: 1 });
      }
    }
    return Array.from(buckets.entries())
      .map(([groupId, b]) => {
        // Sort tags by count desc, then alphabetic — high-frequency
        // co-tags surface first so the user spots the strongest
        // relationships at a glance.
        const tags = Array.from(b.tags.values())
          .sort((a, b) => b.count - a.count || a.tag.name.localeCompare(b.tag.name));
        const total = tags.reduce((s, e) => s + e.count, 0);
        return { groupId, groupName: b.groupName, tags, total };
      })
      .sort((a, b) => a.groupName.localeCompare(b.groupName));
  });

  // Per-group expand state for the Related Tags tree. Independent
  // from `expandedGroups` (Tag Groups tree) so a user expanding a
  // group in one place doesn't auto-expand the same name in the
  // other.
  let expandedRelatedGroups = $state<Set<string>>(new Set());
  function toggleRelatedGroup(groupId: string) {
    const next = new Set(expandedRelatedGroups);
    if (next.has(groupId)) next.delete(groupId);
    else next.add(groupId);
    expandedRelatedGroups = next;
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
    // Tag attachments may have changed → sidebar pill counts could
    // be stale. Cheaper than refetching every expanded group
    // separately because loadFavorites returns all tags in one call.
    await refreshSidebarTagCounts();
  }

  // Surgical patch into the videos array for a single row. Used when
  // the player returns an updated Video from a state-mutating call
  // (mark-for-deletion, unmark-for-deletion, toggleFavorite,
  // toggleNeedsReview, mark/unmark Playback Issue) so the grid
  // thumbnail can re-render with its new flag — most visibly the
  // marked-for-deletion grayscale + trash overlay — without a full
  // refetch. Auto-advance may have moved playingVideo on by the
  // time we land here, so the patched row is purely a background
  // grid update. Flag-count badges in the sidebar's Flags tree key
  // off `flagCounts`, so we refresh those too — otherwise hitting
  // F or R in the player wouldn't move the count.
  function patchVideoInGrid(updated: Video) {
    const idx = videos.findIndex(v => v.id === updated.id);
    if (idx < 0) return;
    videos = [...videos.slice(0, idx), updated, ...videos.slice(idx + 1)];
    void refreshFlagCounts();
  }

  onMount(loadSidebar);
</script>

<svelte:head><title>Videos - Video Organizer</title></svelte:head>

<div class="flex flex-col gap-4">
  <!-- Local-only diagnostic affordances live in the player's
       Playback Issue overlay — banner reminds remote users those
       buttons won't appear. -->
  <RemoteHostBanner />
  <h1 class="text-2xl font-semibold">Videos</h1>

  {#if loadError}
    <div class="alert alert-error">
      <span>{loadError}</span>
      <button class="btn btn-sm" onclick={() => (loadError = null)}>Dismiss</button>
    </div>
  {/if}

  <!-- Grid columns swap based on sidebar collapse state. Collapsed
       state shrinks the aside to a 48px chevron-only column so the
       player + thumbnails get the full width back. Both transitions
       go through grid-template-columns, which animates smoothly with
       a CSS transition on the grid container. -->
  <div
    class="grid grid-cols-1 gap-4 transition-[grid-template-columns] duration-150 {sidebarCollapsed ? 'lg:grid-cols-[48px_1fr]' : 'lg:grid-cols-[280px_1fr]'}"
  >
    <!-- Sidebar -->
    <!-- Filter sidebar — sticky-pinned to the viewport top so it stays
         visible while the user scrolls thumbnails on the right. The
         max-height + internal overflow-y-auto means tall sidebars
         (lots of expanded tag groups) get their own scroll bar instead
         of pushing past the viewport. The z-index keeps it above the
         sticky video player when the user has scrolled both into the
         same vertical band. -->
    <aside
      class="card bg-base-200 sticky top-0 z-20 max-h-screen overflow-y-auto {sidebarCollapsed ? 'p-1' : 'p-3 space-y-3'}"
    >
      <!-- Sidebar header. Expanded shows the "Filters" title on the
           left with a ‹ chevron on the right (clicks to collapse).
           Collapsed shows just the › chevron centered in the 48px
           column (clicks to expand). The title gives the chevron
           something to anchor against — without it the lone arrow
           reads as floating UI noise. -->
      <div class="flex items-center {sidebarCollapsed ? 'justify-center' : 'justify-between'} border-b border-base-300/70 pb-2">
        {#if !sidebarCollapsed}
          <h2 class="text-base font-semibold pl-1">Filters</h2>
        {/if}
        <button
          type="button"
          class="btn btn-ghost btn-square"
          aria-label={sidebarCollapsed ? 'Expand filter sidebar' : 'Collapse filter sidebar'}
          title={sidebarCollapsed ? 'Expand filter sidebar' : 'Collapse filter sidebar'}
          onclick={toggleSidebar}
        >
          <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" class="h-7 w-7 fill-current">
            {#if sidebarCollapsed}
              <path d="M9 6l6 6-6 6V6z" />
            {:else}
              <path d="M15 6l-6 6 6 6V6z" />
            {/if}
          </svg>
        </button>
      </div>

      {#if !sidebarCollapsed}
      <!-- Global search: scans tag names + aliases, system statuses, and
           Missing-<group>. Each row labels its source so the user can tell
           "Bob Marley · Performer" apart from "Playback Issue · System". -->
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
        <button
          type="button"
          class="flex items-center gap-1 w-full text-left mb-1 hover:bg-base-200 rounded px-1 py-0.5"
          onclick={() => toggleSection('flags')}
          aria-expanded={!sectionCollapsed.flags}
        >
          <svg
            xmlns="http://www.w3.org/2000/svg"
            viewBox="0 0 24 24"
            class="h-3 w-3 fill-current transition-transform {sectionCollapsed.flags ? '' : 'rotate-90'}"
          >
            <path d="M9 6l6 6-6 6V6z" />
          </svg>
          <h3 class="font-semibold text-sm">Flags</h3>
        </button>
        {#if !sectionCollapsed.flags}
        <!-- Flags tree (Option A — tristate cycle button). Each flag
             is a single clickable row that advances state on every
             click: Any → True → False → Any. The right-side state
             indicator shows the current state at a glance:
                ○  hollow circle, muted   → Any (not in filter)
                ✓  green check            → True  (Required bucket)
                ✗  red cross              → False (Excluded bucket)
             No expansion, no nested options — minimum cost per change
             and current state always visible. Boolean flags from the
             Video entity render first, then any tags from a tag
             group named "Flags" so the user gets one unified list. -->
        <div class="bg-base-100 rounded-box p-1 text-sm">
          {#each flagItems as item (flagItemKey(item))}
            {@const itemLabel = flagItemLabel(item)}
            {@const state = flagState(item)}
            {@const itemCount = item.kind === 'bool' ? flagCounts[item.def.value] : item.tag.videoCount}
            <button
              type="button"
              class="w-full flex items-center gap-1 hover:bg-base-200 rounded text-left"
              onclick={() => cycleFlag(item)}
              title="{itemLabel}: {flagStateLabel(state)} — click to cycle"
              aria-label="{itemLabel}, currently {flagStateLabel(state)}, click to cycle"
            >
              <!-- Empty chevron-slot keeps the indent rhythm aligned
                   with other tree sections (which put a chevron here
                   on expandable rows). -->
              <span class="shrink-0 w-5 h-5" aria-hidden="true"></span>
              <span class="shrink-0 w-4 h-4 flex items-center justify-center" aria-hidden="true">
                {#if item.kind === 'bool'}
                  {#if item.def.value === 'favorite'}
                    <svg viewBox="0 0 24 24" class="h-3 w-3"
                      fill="rgb(234 179 8)"
                      stroke="rgb(255 255 255 / 0.85)" stroke-width="1.25" stroke-linejoin="round">
                      <path d="M12 2.5 L14.6 8.9 L21.5 9.5 L16.2 14.1 L17.8 20.9 L12 17.3 L6.2 20.9 L7.8 14.1 L2.5 9.5 L9.4 8.9 Z" />
                    </svg>
                  {:else if item.def.value === 'needsReview'}
                    <svg viewBox="0 0 24 24" class="h-3 w-3 fill-current" style="color: rgb(56 189 248);">
                      <path d="M12 5c-7 0-10 7-10 7s3 7 10 7 10-7 10-7-3-7-10-7zm0 11a4 4 0 110-8 4 4 0 010 8zm0-6a2 2 0 100 4 2 2 0 000-4z" />
                    </svg>
                  {:else if item.def.value === 'playbackIssue'}
                    <svg viewBox="0 0 24 24" class="h-3 w-3 fill-current" style="color: rgb(249 115 22);">
                      <path d="M12 2a10 10 0 100 20 10 10 0 000-20zm0 2a8 8 0 016.32 12.9L7.1 5.68A8 8 0 0112 4zM5.68 7.1l11.22 11.22A8 8 0 015.68 7.1z" />
                    </svg>
                  {:else}
                    <svg viewBox="0 0 24 24" class="h-3 w-3 fill-current" style="color: rgb(239 68 68);">
                      <path d="M9 3a1 1 0 00-1 1v1H5a1 1 0 100 2h14a1 1 0 100-2h-3V4a1 1 0 00-1-1H9zm-2 6v11a2 2 0 002 2h6a2 2 0 002-2V9H7zm2 2h2v8H9v-8zm4 0h2v8h-2v-8z" />
                    </svg>
                  {/if}
                {:else}
                  <!-- Tag-flag: small generic flag icon so the row
                       visually slots in next to the colored boolean
                       flags. -->
                  <svg viewBox="0 0 24 24" class="h-3 w-3 fill-current text-base-content/60">
                    <path d="M5 3a1 1 0 011-1h11l-2 4 2 4H6v9H4V3a1 1 0 011-1z"/>
                  </svg>
                {/if}
              </span>
              <span class="flex-1 min-w-0 truncate py-1 font-medium {state !== 'nofilter' ? 'text-primary' : ''}">{itemLabel}</span>
              <!-- Per-flag count badge — number of videos under
                   enabled VideoSets that currently have this flag
                   set. Refreshes on initial load and after any
                   sidebar-tag-counts refresh (which fires when the
                   user saves changes that could flip a flag). -->
              <span
                class="shrink-0 text-xs tabular-nums opacity-50"
                title="{itemCount} video{itemCount === 1 ? '' : 's'} with {itemLabel} set"
              >{itemCount}</span>
              <!-- Tristate indicator. Distinct shape AND color per
                   state so the user can scan the column visually. -->
              <span class="shrink-0 w-5 h-5 flex items-center justify-center" aria-hidden="true">
                {#if state === 'true'}
                  <svg viewBox="0 0 24 24" class="h-3.5 w-3.5 fill-current text-success">
                    <path d="M9 16.2l-3.5-3.5L4 14.2l5 5L20 8.2l-1.5-1.5z"/>
                  </svg>
                {:else if state === 'false'}
                  <svg viewBox="0 0 24 24" class="h-3.5 w-3.5 fill-current text-error">
                    <path d="M19 6.4 17.6 5 12 10.6 6.4 5 5 6.4 10.6 12 5 17.6 6.4 19 12 13.4 17.6 19 19 17.6 13.4 12z"/>
                  </svg>
                {:else}
                  <svg viewBox="0 0 24 24" class="h-3.5 w-3.5 text-base-content/35"
                    fill="none" stroke="currentColor" stroke-width="2">
                    <circle cx="12" cy="12" r="8"/>
                  </svg>
                {/if}
              </span>
            </button>
          {/each}
        </div>
        {/if}
      </div>

      {#if favoriteTags.length > 0}
        <div>
          <button
            type="button"
            class="flex items-center gap-1 w-full text-left mb-1 hover:bg-base-200 rounded px-1 py-0.5"
            onclick={() => toggleSection('favorites')}
            aria-expanded={!sectionCollapsed.favorites}
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 24 24"
              class="h-3 w-3 fill-current transition-transform {sectionCollapsed.favorites ? '' : 'rotate-90'}"
            >
              <path d="M9 6l6 6-6 6V6z" />
            </svg>
            <svg viewBox="0 0 24 24" class="h-4 w-4"
              fill="rgb(234 179 8)"
              stroke="rgb(255 255 255 / 0.85)" stroke-width="1.25" stroke-linejoin="round">
              <path d="M12 2.5 L14.6 8.9 L21.5 9.5 L16.2 14.1 L17.8 20.9 L12 17.3 L6.2 20.9 L7.8 14.1 L2.5 9.5 L9.4 8.9 Z" />
            </svg>
            <h3 class="font-semibold text-sm">Favorite Tags</h3>
          </button>
          {#if !sectionCollapsed.favorites}
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
          {/if}
        </div>
      {/if}

      {#if relatedTagsByGroup.length > 0}
        <div>
          <button
            type="button"
            class="flex items-center gap-1 w-full text-left mb-1 hover:bg-base-200 rounded px-1 py-0.5"
            onclick={() => toggleSection('related')}
            aria-expanded={!sectionCollapsed.related}
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 24 24"
              class="h-3 w-3 fill-current transition-transform {sectionCollapsed.related ? '' : 'rotate-90'}"
            >
              <path d="M9 6l6 6-6 6V6z" />
            </svg>
            <h3 class="font-semibold text-sm">Related Tags</h3>
          </button>
          {#if !sectionCollapsed.related}
          <!-- Related Tags tree. For every tag found on at least one
               video in the current filtered grid, surfaces it under
               its tag group with a count = number of those videos
               carrying it. Live re-derives whenever the grid changes,
               so as the user narrows the filter the suggestions stay
               relevant. Click a tag to add it to the filter. -->
          <div class="bg-base-100 rounded-box p-1 text-sm">
            {#each relatedTagsByGroup as g (g.groupId)}
              {@const isExpanded = expandedRelatedGroups.has(g.groupId)}
              <div>
                <div class="flex items-center gap-1 hover:bg-base-200 rounded">
                  <button
                    type="button"
                    class="shrink-0 w-5 h-5 flex items-center justify-center text-base-content/70 hover:text-base-content"
                    aria-label={isExpanded ? `Collapse ${g.groupName}` : `Expand ${g.groupName}`}
                    title={isExpanded ? 'Collapse' : 'Expand'}
                    onclick={() => toggleRelatedGroup(g.groupId)}
                  >
                    <svg
                      xmlns="http://www.w3.org/2000/svg"
                      viewBox="0 0 24 24"
                      class="h-3 w-3 fill-current transition-transform {isExpanded ? 'rotate-90' : ''}"
                    >
                      <path d="M9 6l6 6-6 6V6z" />
                    </svg>
                  </button>
                  <button
                    type="button"
                    class="flex-1 min-w-0 text-left truncate py-1 font-medium hover:underline"
                    onclick={() => toggleRelatedGroup(g.groupId)}
                    title={isExpanded ? `Collapse ${g.groupName}` : `Expand ${g.groupName}`}
                  >{g.groupName}</button>
                  <span
                    class="shrink-0 text-xs tabular-nums opacity-50"
                    title="{g.tags.length} distinct tag{g.tags.length === 1 ? '' : 's'} across {g.total} video{g.total === 1 ? '' : 's'}"
                  >{g.tags.length}</span>
                </div>
                {#if isExpanded}
                  {#each g.tags as rt (rt.tag.id)}
                    {@const slot = filterSlot(rt.tag.id)}
                    <div
                      class="flex items-center gap-1 hover:bg-base-200 rounded"
                      style="padding-left: 0.75rem"
                    >
                      <span class="shrink-0 w-5 h-5" aria-hidden="true"></span>
                      <button
                        type="button"
                        class="flex-1 min-w-0 text-left truncate py-1 hover:underline"
                        onclick={() => pickTag({
                          id: rt.tag.id,
                          tagGroupId: rt.tag.tagGroupId,
                          tagGroupName: rt.tag.tagGroupName,
                          name: rt.tag.name,
                          aliases: [],
                          isFavorite: false,
                          sortOrder: 0,
                          notes: '',
                          videoCount: rt.count
                        })}
                        title={slot ? `In filter: ${slot}` : `Filter by ${g.groupName}: ${rt.tag.name}`}
                      >{rt.tag.name}</button>
                      <span class="shrink-0 text-xs tabular-nums opacity-50">{rt.count}</span>
                    </div>
                  {/each}
                {/if}
              </div>
            {/each}
          </div>
          {/if}
        </div>
      {/if}

      {#if visibleFolderRoots.length > 0}
        <div>
          <button
            type="button"
            class="flex items-center gap-1 w-full text-left mb-1 hover:bg-base-200 rounded px-1 py-0.5"
            onclick={() => toggleSection('folders')}
            aria-expanded={!sectionCollapsed.folders}
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 24 24"
              class="h-3 w-3 fill-current transition-transform {sectionCollapsed.folders ? '' : 'rotate-90'}"
            >
              <path d="M9 6l6 6-6 6V6z" />
            </svg>
            <h3 class="font-semibold text-sm">Folders</h3>
          </button>
          {#if !sectionCollapsed.folders}
          <!-- Tree view that mirrors the on-disk layout under each
               configured source root. Chevron expands a node, lazy-
               loading immediate children from /api/import/browse on
               first open. Clicking the label adds a folder filter
               keyed on the absolute path; the API matcher already
               accepts any path under an enabled VideoSet. Each row
               carries import-progress counts: muted "X" when fully
               imported, warning-tinted "X/Y" when partial. Only
               folders containing at least one imported video appear
               — the rest (and their subtrees) are pruned out. -->
          <div class="bg-base-100 rounded-box p-1">
            {#each visibleFolderRoots as root (root.fullPath)}
              <FolderTreeNode
                name={root.name}
                fullPath={root.fullPath}
                hasSubdirectories={root.hasSubdirectories}
                depth={0}
                videoCount={root.videoCount}
                importedCount={root.importedCount}
                onPickFolder={(path, label) =>
                  filterStore.requestAdd({ type: 'folder', value: path, label })}
              />
            {/each}
          </div>
          {/if}
        </div>
      {/if}

      <div>
        <button
          type="button"
          class="flex items-center gap-1 w-full text-left mb-1 hover:bg-base-200 rounded px-1 py-0.5"
          onclick={() => toggleSection('tagGroups')}
          aria-expanded={!sectionCollapsed.tagGroups}
        >
          <svg
            xmlns="http://www.w3.org/2000/svg"
            viewBox="0 0 24 24"
            class="h-3 w-3 fill-current transition-transform {sectionCollapsed.tagGroups ? '' : 'rotate-90'}"
          >
            <path d="M9 6l6 6-6 6V6z" />
          </svg>
          <h3 class="font-semibold text-sm">Tag Groups</h3>
        </button>
        {#if !sectionCollapsed.tagGroups}
        <!-- Tag Groups tree. Each group is a parent row with a chevron
             that lazy-loads its tags on first expand (toggleGroup). The
             tags themselves render as indented leaf rows — same chrome
             as Folders / Status / Missing — instead of the old colored
             pills, so the whole sidebar reads as one consistent tree.
             Each tag row keeps its videoCount badge and pencil-edit
             button on the right; the title attribute still calls out
             current filter slot membership. -->
        <div class="bg-base-100 rounded-box p-1 text-sm">
          {#each nonFlagsGroups as g (g.id)}
            {@const isExpanded = expandedGroups.has(g.id)}
            <div>
              <div class="flex items-center gap-1 hover:bg-base-200 rounded">
                <button
                  type="button"
                  class="shrink-0 w-5 h-5 flex items-center justify-center text-base-content/70 hover:text-base-content"
                  aria-label={isExpanded ? 'Collapse {g.name}' : 'Expand {g.name}'}
                  title={isExpanded ? 'Collapse' : 'Expand'}
                  onclick={() => toggleGroup(g)}
                >
                  <svg
                    xmlns="http://www.w3.org/2000/svg"
                    viewBox="0 0 24 24"
                    class="h-3 w-3 fill-current transition-transform {isExpanded ? 'rotate-90' : ''}"
                  >
                    <path d="M9 6l6 6-6 6V6z" />
                  </svg>
                </button>
                <button
                  type="button"
                  class="flex-1 min-w-0 text-left truncate py-1 font-medium hover:underline"
                  onclick={() => toggleGroup(g)}
                  title={isExpanded ? 'Collapse {g.name}' : 'Expand {g.name}'}
                >{g.name}</button>
                <span class="shrink-0 text-xs tabular-nums opacity-50">{g.tagCount}</span>
              </div>
              {#if isExpanded}
                <!-- "Missing / None" leaf — first child of every
                     group. Clicking adds a `Missing <group>` filter,
                     surfacing videos that have no tag from this
                     group. Lives inside the group instead of in a
                     separate Missing section so the user discovers
                     it next to the group's actual tags. The badge
                     shows how many videos currently have no tag in
                     this group; suppressed when zero. -->
                <div
                  class="flex items-center gap-1 hover:bg-base-200 rounded"
                  style="padding-left: 0.75rem"
                >
                  <span class="shrink-0 w-5 h-5" aria-hidden="true"></span>
                  <button
                    type="button"
                    class="flex-1 min-w-0 text-left truncate py-1 italic text-base-content/70 hover:underline"
                    onclick={() => pickMissing(g)}
                    title="Show videos with no tag in {g.name}"
                  >Missing / None</button>
                  {#if g.videosMissingCount > 0}
                    <span
                      class="shrink-0 text-xs tabular-nums opacity-50"
                      title="{g.videosMissingCount} video{g.videosMissingCount === 1 ? '' : 's'} with no {g.name} tag"
                    >{g.videosMissingCount}</span>
                  {/if}
                </div>
                {#each tagsByGroup[g.id] ?? [] as t (t.id)}
                  {@const slot = filterSlot(t.id)}
                  <div
                    class="flex items-center gap-1 hover:bg-base-200 rounded"
                    style="padding-left: 0.75rem"
                  >
                    <span class="shrink-0 w-5 h-5" aria-hidden="true"></span>
                    <button
                      type="button"
                      class="flex-1 min-w-0 text-left truncate py-1 hover:underline"
                      onclick={() => pickTag(t)}
                      title={slot ? `In filter: ${slot}` : `Filter by ${g.name}: ${t.name}`}
                    >{t.name}</button>
                    <span class="shrink-0 text-xs tabular-nums opacity-50">{t.videoCount}</span>
                    <button
                      type="button"
                      class="shrink-0 px-1 opacity-70 hover:opacity-100"
                      onclick={(e) => openTagEdit(t, e)}
                      title="Edit tag"
                      aria-label="Edit {t.name}"
                    >✎</button>
                  </div>
                {/each}
                {#if (tagsByGroup[g.id] ?? []).length === 0}
                  <div
                    class="text-xs text-base-content/50 italic py-1"
                    style="padding-left: 2rem"
                  >No tags yet.</div>
                {/if}
              {/if}
            </div>
          {/each}
        </div>
        {/if}
      </div>
      {/if}
    </aside>

    <!-- Main area: a flex-row of (content column | tags strip). The
         tags strip is a flow sibling instead of an absolute overlay,
         so when it expands on hover the content column shrinks to fit
         and the video reflows down to whatever room is left — no more
         floating over the picture. The content column is its own flex
         column with the sticky player at top, drag-resize divider,
         header row, and thumbnail grid in document flow. -->
    <section class="relative flex flex-row">
      <!-- Content column: holds player + handle + header + thumbs.
           min-w-0 lets it shrink past its content width when the strip
           expands; without it flex children refuse to shrink below
           their content's natural size. -->
      <div class="flex-1 min-w-0 flex flex-col">
      <!-- Sticky pinned column-top: filter chips + player card live in
           one wrapper so they stick to the viewport top together.
           Moving the chips out of the sidebar fixed the trees-jumping
           problem; placing them inside the same sticky wrapper as the
           player keeps them visible while the user scrolls thumbs.
           Wrapper is rendered whenever either piece is present so the
           chips still appear if no video is selected (e.g. empty
           filter result). -->
      {#if !filterStore.isEmpty() || playingVideo}
        <div class="sticky top-0 z-10 flex flex-col">
          {#if !filterStore.isEmpty()}
            <!-- Chips bar. Horizontal flow with flex-wrap so longer
                 filter sets break onto multiple lines without pushing
                 sibling chrome around. Solid bg-base-100 so scrolling
                 thumbnails don't bleed through. Each bucket
                 (Required / Optional / Excluded) is grouped under its
                 own label so the user can see at a glance what's
                 ANDed vs ORed vs negated. -->
            <div class="bg-base-100 border border-base-300 rounded-box px-3 py-2 mb-2 flex items-center gap-3 flex-wrap">
              <h2 class="font-semibold text-sm shrink-0">Filter:</h2>
              {#if filterStore.required.length > 0}
                <div class="flex items-center gap-1 flex-wrap">
                  <span class="text-xs text-base-content/60">Required</span>
                  {#each filterStore.required as t (`req-${t.type}-${t.value}`)}
                    <span class="badge {filterSlotClass('required')} gap-1">
                      {t.label}
                      <button onclick={() => filterStore.remove(t)}>×</button>
                    </span>
                  {/each}
                </div>
              {/if}
              {#if filterStore.optional.length > 0}
                <div class="flex items-center gap-1 flex-wrap">
                  <span class="text-xs text-base-content/60">Optional</span>
                  {#each filterStore.optional as t (`opt-${t.type}-${t.value}`)}
                    <span class="badge {filterSlotClass('optional')} gap-1">
                      {t.label}
                      <button onclick={() => filterStore.remove(t)}>×</button>
                    </span>
                  {/each}
                </div>
              {/if}
              {#if filterStore.excluded.length > 0}
                <div class="flex items-center gap-1 flex-wrap">
                  <span class="text-xs text-base-content/60">Excluded</span>
                  {#each filterStore.excluded as t (`exc-${t.type}-${t.value}`)}
                    <span class="badge {filterSlotClass('excluded')} gap-1">
                      {t.label}
                      <button onclick={() => filterStore.remove(t)}>×</button>
                    </span>
                  {/each}
                </div>
              {/if}
              <button class="btn btn-xs ml-auto shrink-0" onclick={() => filterStore.clear()}>Clear</button>
            </div>
          {/if}

          {#if playingVideo}
            <!-- Player area — `min-height` tracks the divider so
                 dragging down enlarges the card (and lets the video
                 grow into the new room). The video's own `max-height`
                 is wired to the same divider value via
                 `maxVideoHeightPx` below — that's what makes dragging
                 UP actually shrink the picture. Without that wiring
                 the video holds at its 70vh default no matter how
                 high you drag. Subtract ~72px of wrapper chrome
                 (p-3 + button row + gap) so the picture fits cleanly
                 inside the wrapper. -->
            <div
              class="card bg-base-200 p-3"
              style="min-height: {playerHeight}px;"
            >
              <VideoPlayer
                bind:video={playingVideo}
                shortcutsEnabled={true}
                maxVideoHeightPx={Math.max(100, playerHeight - 72)}
                tagsPanelOpen={showEditTagsPanel}
                onToggleTags={() => (showEditTagsPanel = !showEditTagsPanel)}
                onToggleFileInfo={() => (showFileInfo = !showFileInfo)}
                onRequestNext={goNext}
                onRequestPrev={goPrev}
                onAfterSave={refreshSidebarTagCounts}
                onVideoChanged={patchVideoInGrid}
              />
            </div>
          {/if}
        </div>
      {/if}

      {#if playingVideo}
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
      </div>
      <!-- ↑ end of content column -->

      <!-- Side panels — File Info on the inside (closer to the video,
           since reading metadata pairs naturally with watching), Tags
           on the outside. Each is a fixed 360px sticky column visible
           only while its own toggle is true; the I and T keys flip
           them independently. Both can be open at once on wide enough
           viewports. Each panel has internal overflow-y-auto + max-
           h-screen so they stay visible during thumbnail scroll and
           self-scroll when content is tall. -->
      {#if showFileInfo && playingVideo}
        <div
          class="sticky top-0 self-start max-h-screen w-[360px] shrink-0 overflow-y-auto bg-base-200 border-l border-base-300 shadow-xl"
        >
          <FileInfoPanel
            bind:show={showFileInfo}
            video={playingVideo}
          />
        </div>
      {/if}
      {#if showEditTagsPanel && playingVideo}
        <div
          class="sticky top-0 self-start max-h-screen w-[360px] shrink-0 overflow-y-auto bg-base-200 border-l border-base-300 shadow-xl"
        >
          <EditTagsPanel
            bind:video={playingVideo}
            bind:show={showEditTagsPanel}
            onAfterSave={refreshPlaying}
            onTagSaved={onTagSavedFromSidebar}
          />
        </div>
      {/if}
    </section>
  </div>
</div>

<FilterDialog />
<TagEditModal bind:show={editTagModalShow} tag={editingTag} onSaved={onTagSavedFromSidebar} />


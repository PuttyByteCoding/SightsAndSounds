<script lang="ts">
  // Simplified Browse page rewritten for the unified tag model. Lists every
  // video filtered by tag + status + folder filters. Plays inline with
  // the existing VideoPlayer and offers the generic EditTagsPanel.
  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import { api } from '$lib/api';
  import type {
    Video,
    VideoSet,
    Tag,
    TagGroup,
    FilterRef,
    PlaylistFilterRequest,
    ImportBrowseDirectory,
    VideoTagSummary,
    FlagCounts
  } from '$lib/types';
  import VideoCard from '$lib/components/VideoCard.svelte';
  import VideoPlayer from '$lib/components/VideoPlayer.svelte';
  import EditTagsPanel from '$lib/components/EditTagsPanel.svelte';
  import FileInfoPanel from '$lib/components/FileInfoPanel.svelte';
  import FilterDialog from '$lib/components/FilterDialog.svelte';
  import MoveFileDialog from '$lib/components/MoveFileDialog.svelte';
  import TagEditModal from '$lib/components/TagEditModal.svelte';
  import FolderTreeNode from '$lib/components/FolderTreeNode.svelte';
  import RemoteHostBanner from '$lib/components/RemoteHostBanner.svelte';
  import StartupStatus from '$lib/components/StartupStatus.svelte';
  import { filterStore } from '$lib/filterStore.svelte';
  import { planFilteredQueue } from '$lib/browseQueue';
  import { pillClass, filterSlot, filterSlotClass, filterSlotDot } from '$lib/tagColors';

  let videos = $state<Video[]>([]);
  // How many videos matched the current filter but were suppressed by a
  // hidden-by-default tag (#84). Surfaced as a status in the filter bar so the
  // count never silently mismatches what's shown.
  let hiddenByTagCount = $state(0);
  let videosLoading = $state(false);
  let loadError = $state<string | null>(null);

  // Slow-load diagnostics (issue #71). The page's two heavy first loads —
  // the sidebar (tag groups / tags for the filter tree) and the first video
  // queue — flip these flags when they finish. If both aren't done within
  // SLOW_LOAD_MS of mount, we pop the StartupStatus dialog so the user can
  // see exactly which component load is dragging instead of staring at a
  // blank grid. Mirrors the temporary landing page at routes/+page.svelte.
  const SLOW_LOAD_MS = 2000;
  let sidebarReady = $state(false);
  let firstVideosReady = $state(false);
  const pageReady = $derived(sidebarReady && firstVideosReady);
  let showLoadStatus = $state(false);
  // Auto-close the slow-load dialog the moment both heavy loads finish, so a
  // load that just barely crosses 2s flashes the dialog and dismisses itself
  // rather than forcing the user to close it.
  $effect(() => {
    if (pageReady) showLoadStatus = false;
  });

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
  // Bumped to force the folder tree to remount. FolderTreeNode caches
  // its children on first expand, so a stale subtree (e.g. after a
  // move drops a file into a folder) only refreshes when the nodes
  // are torn down and rebuilt — a {#key folderTreeSeed} block does
  // exactly that. Also re-fetches the annotated roots so a folder
  // that just became non-empty appears in the (imports-only) tree.
  let folderTreeSeed = $state(0);
  let folderTreeRefreshing = $state(false);

  let playingVideo = $state<Video | null>(null);
  let showEditTagsPanel = $state(false);
  // I-key toggles a read-only File Info side panel. Independent of
  // the Tags panel: either or both can be open at once.
  let showFileInfo = $state(false);
  // True until the first videos load completes. The first load may
  // shuffle into a random playlist; later filter changes keep server
  // order in Shuffle mode (see browseQueue.ts / planFilteredQueue).
  let firstLoad = true;

  // --- Server-side keyset pagination (#127) -----------------------------
  // The grid pulls one page at a time from /videos/filter-page and appends as
  // the user scrolls, instead of fetching the whole matched set and windowing
  // it client-side. The server owns ordering (including seeded shuffle), so
  // there's no client-side sort/shuffle anymore.
  const PAGE_SIZE = 48;
  let nextCursor = $state<string | null>(null);
  // Full count of the current filter's matches (the server total, not just the
  // pages loaded so far) — drives the "video N of M" badge.
  let totalCount = $state(0);
  let loadingMore = $state(false);
  // Bumped on every page-1 refresh; a loadMore that started under an older
  // generation discards its result so stale pages can't append to a new filter.
  let loadGen = 0;
  // Per-session shuffle seed so shuffle pages are stable across scrolls;
  // reshuffle picks a new one.
  let shuffleSeed = $state(newSeed());
  function newSeed(): string {
    return typeof crypto !== 'undefined' && crypto.randomUUID
      ? crypto.randomUUID()
      : Math.random().toString(36).slice(2);
  }
  // The filter used for the current result set — reused by loadMore so a
  // mid-scroll filter change can't interleave pages from different filters.
  let activeFilter: PlaylistFilterRequest | null = null;
  let scrollSentinelEl: HTMLDivElement | null = $state(null);
  $effect(() => {
    // Re-create the observer whenever the loaded count changes so it re-checks
    // the (now lower) sentinel and keeps loading while it stays in view —
    // without this it fires only on the first intersection (issue #33).
    void videos.length;
    void nextCursor;
    const el = scrollSentinelEl;
    if (!el) return;
    const obs = new IntersectionObserver((entries) => {
      for (const e of entries) {
        if (e.isIntersecting) void loadMore();
      }
    }, { rootMargin: '300px' });
    obs.observe(el);
    return () => obs.disconnect();
  });

  // --- Player auto-sizing (#171) ---------------------------------------
  // The player and the thumbnail strip share the viewport: the queue strip
  // is ALWAYS fully visible, and the video fills the height left above it.
  // We measure the player card's top plus the header-row/strip heights and
  // size the card to "everything left after the strip", so the strip never
  // gets pushed below the fold. The video itself is capped at 2× its native
  // size inside VideoPlayer (fitMaxWidth), so a tall pane never upscales it
  // past that ceiling — it just letterboxes.
  const PLAYER_HEIGHT_MIN = 220;
  let playerHeight = $state(480);
  let playerCardEl = $state<HTMLDivElement | null>(null);
  let playerHeaderRowEl = $state<HTMLDivElement | null>(null);
  let playerStripEl = $state<HTMLDivElement | null>(null);

  function recomputePlayerHeight() {
    if (typeof window === 'undefined' || !playerCardEl) return;
    const top = playerCardEl.getBoundingClientRect().top;
    // Strip + header-row heights are content-driven and don't change when the
    // player grows, so reserving them keeps the strip on-screen.
    const below = (playerHeaderRowEl?.offsetHeight ?? 0)
      + (playerStripEl?.offsetHeight ?? 0) + 28; // inter-element gaps/margins
    playerHeight = Math.max(PLAYER_HEIGHT_MIN, Math.round(window.innerHeight - top - below));
  }

  // --- Thumbnail size --------------------------------------------------
  // User-controlled minimum tile width; the grid uses auto-fill +
  // minmax so columns reflow naturally as the user drags. Persisted to
  // localStorage so the choice sticks. Also drives the player-mode
  // strip's fixed cell width.
  const THUMB_WIDTH_MIN = 120;
  const THUMB_WIDTH_MAX = 400;
  const THUMB_WIDTH_DEFAULT = 200;
  let thumbWidth = $state(THUMB_WIDTH_DEFAULT);

  // --- View mode (issue #23) -------------------------------------------
  // 'player': the video player on top with a single horizontal row of
  //   thumbnails (the "strip") below it.
  // 'grid':   the player is hidden and thumbnails fill the pane as a
  //   grid (a thumbnails-only browsing view). Persisted to localStorage.
  type ViewMode = 'player' | 'grid';
  let viewMode = $state<ViewMode>('player');
  function setViewMode(m: ViewMode) {
    viewMode = m;
    if (typeof localStorage !== 'undefined') localStorage.setItem('browseViewMode', m);
  }

  // Page-level shortcuts (issue #41): 'G' toggles Grid view, 'M' opens the
  // Move dialog for the current video. Handled here, not in VideoPlayer,
  // because the player is unmounted in grid mode. Ignored while typing in a
  // field, when a modifier is held, or when a dialog is capturing keys.
  function onBrowseKey(e: KeyboardEvent) {
    const isG = e.key === 'g' || e.key === 'G';
    const isM = e.key === 'm' || e.key === 'M';
    if (!isG && !isM) return;
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    const t = e.target as HTMLElement | null;
    if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return;
    if (filterStore.pending || moveDialogVideo || flagModalItem) return;
    if (isG) {
      e.preventDefault();
      setViewMode(viewMode === 'grid' ? 'player' : 'grid');
    } else if (playingVideo && !playingVideo.isClip) {
      // Clips share their parent's file and can't be moved on their own.
      e.preventDefault();
      openMoveDialog(playingVideo);
    }
  }

  // --- Section-level collapse ------------------------------------------
  // Each tree-style section in the sidebar (Flags, Favorite Tags,
  // Folders, Tag Groups) carries a chevron next to its title so the
  // user can collapse it. The body hides; the chevron rotates; the
  // heading stays so the user can re-open it. Persisted per section
  // to localStorage.
  type SectionKey = 'flags' | 'favorites' | 'related' | 'folders' | 'tagGroups';
  const SECTION_KEYS: readonly SectionKey[] = ['flags', 'favorites', 'related', 'folders', 'tagGroups'];
  // Default collapsed state per section, used when the user has no stored
  // preference. Related Tags defaults collapsed — it's a long, noisy,
  // grid-derived list that's mostly in the way during heavy tagging /
  // dev (issue #80). The user's own toggle still persists and wins.
  const SECTION_DEFAULTS: Record<SectionKey, boolean> = {
    flags: false,
    favorites: false,
    related: true,
    folders: false,
    tagGroups: false
  };
  let sectionCollapsed = $state<Record<SectionKey, boolean>>({ ...SECTION_DEFAULTS });
  function toggleSection(k: SectionKey) {
    sectionCollapsed[k] = !sectionCollapsed[k];
    if (typeof localStorage !== 'undefined') {
      localStorage.setItem(`browseSection_${k}_collapsed`, sectionCollapsed[k] ? '1' : '0');
    }
  }

  onMount(() => {
    const storedThumb = Number(localStorage.getItem('browseThumbWidth'));
    if (Number.isFinite(storedThumb) && storedThumb >= THUMB_WIDTH_MIN && storedThumb <= THUMB_WIDTH_MAX) {
      thumbWidth = storedThumb;
    }
    sidebarCollapsed = localStorage.getItem('browseSidebarCollapsed') === '1';
    for (const k of SECTION_KEYS) {
      // Honor a stored preference; otherwise fall back to the section's
      // default collapsed state (Related Tags starts collapsed).
      const stored = localStorage.getItem(`browseSection_${k}_collapsed`);
      sectionCollapsed[k] = stored === null ? SECTION_DEFAULTS[k] : stored === '1';
    }
    const storedSort = localStorage.getItem('browseSortMode');
    if (storedSort && SORT_MODES.some(m => m.value === storedSort)) {
      sortMode = storedSort as SortMode;
    }
    const storedView = localStorage.getItem('browseViewMode');
    if (storedView === 'player' || storedView === 'grid') {
      viewMode = storedView;
    }

    // Slow-load watchdog (issue #71): if the page hasn't finished its two
    // heavy first loads within SLOW_LOAD_MS, pop the diagnostics dialog.
    // Suppressed for the empty-install redirect (pageReady is already set on
    // that path before we navigate away).
    const slowLoadTimer = setTimeout(() => {
      if (!pageReady && !isEmptyInstall) showLoadStatus = true;
    }, SLOW_LOAD_MS);

    // Auto-size the player to fill the viewport above the strip (#171).
    const onResize = () => recomputePlayerHeight();
    window.addEventListener('resize', onResize);
    requestAnimationFrame(recomputePlayerHeight);
    return () => {
      clearTimeout(slowLoadTimer);
      window.removeEventListener('resize', onResize);
    };
  });

  // Re-measure when the playing video, view mode, or queue length changes —
  // any of which can shift the strip/header heights or the player's top.
  $effect(() => {
    void playingVideo?.id;
    void viewMode;
    void totalCount;
    if (viewMode === 'player') requestAnimationFrame(recomputePlayerHeight);
  });

  // Save on each change. Cheap (a few writes per drag) and keeps the
  // localStorage in sync without a separate "save" gesture.
  $effect(() => {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem('browseThumbWidth', String(thumbWidth));
  });



  // User-triggered reshuffle of the current grid into a new random order
  // and jump to the new first video. Resets the infinite-scroll window
  // so the user starts at the top of the new playlist. Also drops any
  // explicit sort back to Shuffle — a random order IS the shuffle mode,
  // and leaving the select on e.g. "File size" while showing a random
  // grid would lie about the current order.
  function reshufflePlaylist() {
    shuffleSeed = newSeed();        // new random order
    setSortMode('shuffle');
    refreshVideos({ resetPlayback: true }); // jump to the new first video
  }

  // --- Sorting -----------------------------------------------------------
  // Explicit grid orderings beyond the default random playlist. Applied
  // client-side over whatever the filter returned, so switching modes
  // never costs a round-trip. Persisted to localStorage so the choice
  // survives navigation away and back.
  type SortMode = 'shuffle' | 'fileName' | 'fileSize' | 'duration' | 'folderFile';
  const SORT_MODES: ReadonlyArray<{ value: SortMode; label: string }> = [
    { value: 'shuffle',    label: 'Shuffle' },
    { value: 'fileName',   label: 'File name' },
    { value: 'fileSize',   label: 'File size' },
    { value: 'duration',   label: 'Duration' },
    { value: 'folderFile', label: 'Folder, then file name' }
  ];
  let sortMode = $state<SortMode>('shuffle');
  // false = ascending (A→Z / smallest / shortest first). Reset to the
  // mode's natural default when the mode changes; the ↑↓ button flips it.
  let sortDescending = $state(false);

  // Ordering is server-side now (#127): /videos/filter-page sorts + paginates
  // per `sortMode`/`sortDescending`/`shuffleSeed`, so the old client-side
  // compareVideos/orderVideos/shuffleInPlace are gone.

  function setSortMode(mode: SortMode) {
    sortMode = mode;
    if (typeof localStorage !== 'undefined') localStorage.setItem('browseSortMode', mode);
  }

  // Select-change handler: refetch page 1 in the new order (the server sorts +
  // paginates now). Keeps the currently-playing video — its grid position just
  // changes; the Reshuffle button is the explicit "jump to a new first" action.
  function onSortModeChange(mode: SortMode) {
    setSortMode(mode);
    sortDescending = false;
    refreshVideos({ resetPlayback: false });
  }

  function toggleSortDirection() {
    if (sortMode === 'shuffle') return;
    sortDescending = !sortDescending;
    refreshVideos({ resetPlayback: false });
  }

  // refreshVideos re-fetches the filtered list. `fromFilter` marks the
  // calls driven by a filter/search change (the $effect below) — those
  // restart the queue from position 0 per issue #21. Other callers
  // (post-edit refreshPlaying, the "Show all" button) pass nothing and
  // leave playback untouched.
  // Cancels the previous filter fetch when a newer one starts, so rapid filter
  // changes can't resolve out of order (last-issued wins, not last-to-return). (#131)
  let inFlightFilter: AbortController | null = null;

  async function refreshVideos(
    { fromFilter = false, resetPlayback }: { fromFilter?: boolean; resetPlayback?: boolean } = {}
  ) {
    // Empty-library case: the welcome panel is showing instead of the
    // grid, so skip the (pointless) filter fetch and just mark this half
    // of the load gate done.
    if (isEmptyInstall) { firstVideosReady = true; return; }
    inFlightFilter?.abort();
    const ac = new AbortController();
    inFlightFilter = ac;
    const gen = ++loadGen; // supersedes any in-flight loadMore too
    videosLoading = true;
    loadError = null;
    try {
      // ?searchQuery= deep-link → AND a free-text substring into the
      // filter. Drives the "Play all results" path from the Ctrl+K
      // search palette. Trimmed; empty string is treated as no search.
      const searchQuery = (page.url.searchParams.get('searchQuery') ?? '').trim();
      const filter: PlaylistFilterRequest = {
        required: filterStore.required.map((t): FilterRef => ({ type: t.type, value: t.value })),
        optional: filterStore.optional.map((t): FilterRef => ({ type: t.type, value: t.value })),
        excluded: filterStore.excluded.map((t): FilterRef => ({ type: t.type, value: t.value })),
        searchQuery: searchQuery.length > 0 ? searchQuery : undefined
      };
      // Fetch the FIRST page; subsequent pages come from loadMore via the cursor.
      const result = await api.filterVideosPage(
        filter,
        { sort: sortMode, dir: sortDescending ? 'desc' : 'asc', limit: PAGE_SIZE, seed: shuffleSeed },
        ac.signal
      );
      if (ac.signal.aborted) return; // superseded mid-flight — a newer fetch owns the result
      activeFilter = filter;
      nextCursor = result.nextCursor;
      totalCount = result.totalCount;
      hiddenByTagCount = result.hiddenCount;
      // Decide the playing video. A filter/search change restarts the queue
      // from position 0 (issue #21): the current video stops and the new
      // queue's first entry plays. We suppress that reset while a ?id=
      // deep-link is opening a specific video (deepLinkInFlight). The server
      // already ordered the page, so `order` is identity here.
      const plan = planFilteredQueue({
        fetched: result.videos,
        playing: playingVideo,
        firstLoad,
        isShuffle: sortMode === 'shuffle',
        hasSearchQuery: !!filter.searchQuery,
        resetPlayback: resetPlayback ?? (fromFilter && !deepLinkInFlight),
        order: (l) => l
      });
      firstLoad = false;
      videos = plan.videos;
      playingVideo = plan.playing;
    } catch (e: any) {
      if (ac.signal.aborted || e?.name === 'AbortError') return; // superseded — ignore
      loadError = e?.message ?? 'Failed to load videos';
    } finally {
      // Only the latest request owns the loading flags; a superseded one
      // must not clear them out from under the newer fetch.
      if (inFlightFilter === ac) {
        inFlightFilter = null;
        videosLoading = false;
        // First video load is done (success or error) — the grid is no longer
        // "still loading", so the slow-load dialog shouldn't fire for it.
        firstVideosReady = true;
      }
    }
  }

  // Fetch + append the next keyset page when the scroll sentinel scrolls into
  // view (#127). Guarded against concurrent runs and against appending a stale
  // page after the filter/sort changed (loadGen).
  async function loadMore() {
    if (loadingMore || nextCursor === null || activeFilter === null) return;
    const gen = loadGen;
    const cursor = nextCursor;
    loadingMore = true;
    try {
      const result = await api.filterVideosPage(activeFilter, {
        sort: sortMode,
        dir: sortDescending ? 'desc' : 'asc',
        limit: PAGE_SIZE,
        cursor,
        seed: shuffleSeed
      });
      if (gen !== loadGen) return; // filter/sort changed mid-flight — discard
      videos = [...videos, ...result.videos];
      nextCursor = result.nextCursor;
    } catch {
      // Non-fatal: keep what's loaded; a later scroll retries.
    } finally {
      loadingMore = false;
    }
  }

  $effect(() => {
    // Re-fetch whenever the filter store OR the URL's searchQuery
    // changes. Touching `page.url.searchParams` makes the effect
    // reactive to navigation events (back/forward, palette "Play
    // all" clicks, etc.) without remounting the component. These are
    // the filter-driven refreshes that restart the queue from the top.
    void filterStore.required; void filterStore.optional; void filterStore.excluded;
    void page.url.searchParams.get('searchQuery');
    refreshVideos({ fromFilter: true });
  });

  // ?id= deep-link → play that specific video.
  //
  // Used by:
  //   · The Ctrl+K search palette ("open this match in the player")
  //   · The /history page's row click
  //   · Any external link sharing a specific video URL
  //
  // Behavior:
  //   1. Set deepLinkInFlight so a concurrent filter-driven refresh
  //      (both effects fire when the palette navigates to
  //      ?id=…&searchQuery=…) doesn't reset playback to videos[0] and
  //      clobber this specific pick.
  //   2. Fetch the video by id and set it as `playingVideo`.
  //   3. If the video isn't already in the current `videos` list
  //      (because it doesn't match the active filter), prepend it so
  //      next/prev navigation works against a non-empty playlist
  //      anchored on the user's choice.
  //
  // Reactive via `page.url.searchParams.get('id')` — clicking another
  // search result while already on /browse changes the URL without
  // remounting the component, but the effect re-runs and swaps to
  // the new video.
  let lastOpenedId = $state<string | null>(null);
  // True while a ?id= deep-link is opening a specific video. Set
  // synchronously here (before the concurrent filter refresh resumes
  // from its await) so refreshVideos sees it and skips the queue reset.
  let deepLinkInFlight = false;
  $effect(() => {
    const id = page.url.searchParams.get('id');
    if (!id) return;
    if (id === lastOpenedId) return;       // avoid re-fetching the same id
    if (playingVideo?.id === id) {
      lastOpenedId = id;
      return;
    }
    lastOpenedId = id;
    deepLinkInFlight = true;
    void openVideoFromUrl(id);
  });

  async function openVideoFromUrl(id: string) {
    try {
      const v = await api.getVideo(id);
      if (!v) return;
      if (!videos.some((x) => x.id === id)) {
        // Inject at the front so the player has a playlist context
        // even when the filter excludes this video. A later filter
        // change restarts the queue (issue #21) and drops this video —
        // the user's already in the player by then, so that's fine.
        videos = [v, ...videos];
      }
      playingVideo = v;
    } catch (e: any) {
      loadError = `Failed to open video: ${e?.message ?? String(e)}`;
    } finally {
      deepLinkInFlight = false;
    }
  }

  // When the initial sidebar load finds nothing to browse (no VideoSets
  // configured, OR sets exist but no videos imported yet) we show a
  // welcome / empty-library panel in place of the filter UI rather than
  // forcing the user to a setup page (issue #139 — the app no longer
  // *requires* a video source on first run). The flag also lets the
  // $effect that re-fetches on filter changes (below) skip the pointless
  // /api/videos/filter call while the library is empty.
  let isEmptyInstall = $state(false);
  // Distinguishes the two empty cases for the welcome copy: false = no
  // source configured at all (offer "Add a source"); true = a source
  // exists but it's empty (offer "Import videos").
  let hasAnySource = $state(false);

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
      // VideoSets must load FIRST so we can decide the empty-library
      // case before the other sidebar fetches (tag-groups, tags), which
      // have nothing to show on a fresh DB anyway. Two cases collapse
      // into the welcome panel: no VideoSets configured, OR sets exist
      // but no videos have been imported yet. In both, /browse shows the
      // welcome/empty-library state instead of the filter UI (issue
      // #139) — no redirect, so the user can still explore the app.
      sets = await api.listVideoSets();
      if (sets.length === 0) {
        isEmptyInstall = true;
        hasAnySource = false;
        sidebarReady = true;
        return;
      }
      const totalVideos = await api.getVideoCount();
      if (totalVideos === 0) {
        // A source exists but the library is empty — offer Import.
        isEmptyInstall = true;
        hasAnySource = true;
        sidebarReady = true;
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
      await loadFolderRoots();
    } catch (e: any) {
      loadError = e?.message ?? 'Failed to load sidebar';
    } finally {
      // Sidebar load is done (success or error) — clears its half of the
      // slow-load gate so the dialog only fires while work is genuinely
      // still in flight.
      sidebarReady = true;
    }
  }

  // Fetch the annotated source roots that seed the Folders tree.
  // Extracted from loadSidebar so the manual refresh button and the
  // post-move refresh can re-pull root-level counts without redoing
  // the whole sidebar load.
  async function loadFolderRoots(refresh = false) {
    try {
      const browse = await api.browseImport(null, refresh);
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
  }

  // Re-pull root counts and remount the tree so stale subtree caches
  // (FolderTreeNode.children) are discarded. Backs both the Sources
  // refresh button and the automatic refresh after a file move — the
  // moved file's destination folder only becomes navigable once its
  // ancestors' cached children are rebuilt. Remounting collapses the
  // tree; that's the accepted cost of a guaranteed-fresh view.
  // bypassCache=true (the manual refresh button) forces a full server
  // re-walk to pick up changes made outside the app. The post-move call
  // leaves it false: the move endpoint already evicted just the affected
  // folders, so a normal browse re-counts those and serves the rest from
  // cache — keeping "move then browse" fast. Remounting (folderTreeSeed)
  // re-pulls subtrees either way. (issue #4)
  async function refreshFolderTree(bypassCache = false) {
    if (folderTreeRefreshing) return;
    folderTreeRefreshing = true;
    try {
      await loadFolderRoots(bypassCache);
      folderTreeSeed++;
    } finally {
      folderTreeRefreshing = false;
    }
  }

  // --- Remove a folder from the library (issue #53) ----------------------
  // The folder-tree trash button asks here; we confirm (it's destructive)
  // then purge every imported video under the folder via the API. Files on
  // disk are untouched. After removal we refresh the tree (bypassing the
  // scan cache so counts are right) and the grid (the removed videos may be
  // on screen).
  let removeFolderTarget = $state<{ path: string; label: string; count: number } | null>(null);
  let removingFolder = $state(false);
  let removeFolderError = $state<string | null>(null);

  function askRemoveFolder(path: string, label: string, importedCount: number) {
    removeFolderError = null;
    removeFolderTarget = { path, label, count: importedCount };
  }

  async function confirmRemoveFolder() {
    if (!removeFolderTarget || removingFolder) return;
    removingFolder = true;
    removeFolderError = null;
    try {
      await api.removeLibraryFolder(removeFolderTarget.path);
      removeFolderTarget = null;
      await refreshFolderTree(true);
      await refreshVideos();
    } catch (e: any) {
      removeFolderError = e?.message ?? 'Failed to remove folder';
    } finally {
      removingFolder = false;
    }
  }

  // Cache every tag once so the search box can scan them without a round
  // trip per keystroke. Favorites are derived from the same list.
  let allTags = $state<Tag[]>([]);
  const favoriteTags = $derived(allTags.filter(t => t.isFavorite));

  // Favorite tags grouped by their tag group, mirroring the shape used by
  // Related Tags / Tag Groups. Renders as a collapsible tree section so
  // the user can tuck away groups they don't currently care about and
  // the sidebar layout stays consistent across all tag-listing sections.
  // Sorted by group name so the order is stable as favorites are added.
  type FavoriteGroup = { groupId: string; groupName: string; tags: Tag[] };
  const favoriteTagsByGroup = $derived.by<FavoriteGroup[]>(() => {
    const map = new Map<string, FavoriteGroup>();
    for (const t of favoriteTags) {
      let entry = map.get(t.tagGroupId);
      if (!entry) {
        entry = { groupId: t.tagGroupId, groupName: t.tagGroupName, tags: [] };
        map.set(t.tagGroupId, entry);
      }
      entry.tags.push(t);
    }
    const out = [...map.values()];
    out.sort((a, b) => a.groupName.localeCompare(b.groupName));
    for (const g of out) g.tags.sort((a, b) => a.name.localeCompare(b.name));
    return out;
  });
  // Expanded-by-default since the user explicitly favorited these.
  // Tracks which *individual* groups are open so a partial-collapse
  // state survives re-renders.
  let expandedFavoriteGroups = $state<Set<string>>(new Set());
  let favoriteExpansionInitialized = false;
  $effect(() => {
    // Auto-expand newly-arrived groups exactly once per page load.
    // After that the user's expand/collapse choices are sticky.
    if (favoriteExpansionInitialized) return;
    if (favoriteTagsByGroup.length === 0) return;
    expandedFavoriteGroups = new Set(favoriteTagsByGroup.map(g => g.groupId));
    favoriteExpansionInitialized = true;
  });
  function toggleFavoriteGroup(groupId: string) {
    const next = new Set(expandedFavoriteGroups);
    if (next.has(groupId)) next.delete(groupId);
    else next.add(groupId);
    expandedFavoriteGroups = next;
  }
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
    | { kind: 'status'; value: FlagValue; label: string }
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
      { value: 'markedForDeletion', label: 'To Delete' },
      { value: 'clip', label: 'Clip' },
      { value: 'embedded', label: 'Embedded' },
      { value: 'exported', label: 'Exported' },
      { value: 'edited', label: 'Edited' }
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

  function pickStatus(status: FlagValue, label: string) {
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
  type FlagValue = 'favorite' | 'needsReview' | 'playbackIssue' | 'markedForDeletion'
    | 'clip' | 'embedded' | 'exported' | 'edited';
  interface FlagDef { value: FlagValue; label: string; }
  const FLAG_DEFS: FlagDef[] = [
    { value: 'favorite',          label: 'Favorite' },
    { value: 'needsReview',       label: 'Needs Review' },
    { value: 'playbackIssue',          label: "Playback Issue" },
    { value: 'markedForDeletion', label: 'To Delete' },
    // Clip flags (#167). "Clip" is the umbrella (embedded child clip OR
    // user-marked OR exported); the others narrow it. Each writes a
    // `status:<value>` filter the server's MatchesFilter switch maps to the
    // matching boolean/structural predicate.
    { value: 'clip',              label: 'Clip' },
    { value: 'embedded',          label: 'Embedded' },
    { value: 'exported',          label: 'Exported' },
    { value: 'edited',            label: 'Edited' }
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
    markedForDeletion: 0,
    clip: 0,
    embedded: 0,
    exported: 0,
    edited: 0
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

  // Clicking a flag row opens a small modal to choose Required or
  // Excluded (issue #2) — an explicit pick instead of cycling the state
  // in place. The chosen flag item is held here while the modal is open.
  let flagModalItem = $state<FlagItem | null>(null);
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
    // Clicking a thumbnail in the grid (thumbnails-only) view jumps
    // back to the player so the pick actually starts watching (issue
    // #23) — the player is hidden in grid mode otherwise.
    if (viewMode === 'grid') setViewMode('player');
  }

  // --- Move file (issue #4) ---------------------------------------------
  // The MoveFileDialog is opened from the player toolbar (current video)
  // and from each grid card's Move… button. On success we patch the moved
  // row's new FilePath into the grid and the player.
  let moveDialogVideo = $state<Video | null>(null);
  function openMoveDialog(v: Video) {
    moveDialogVideo = v;
  }
  function onFileMoved(updated: Video) {
    patchVideoInGrid(updated);
    if (playingVideo?.id === updated.id) playingVideo = updated;
    // Refresh the Folders tree so the destination folder shows up
    // (and its counts update) — without this the moved file is in the
    // DB but unreachable via the filter tree until a full reload.
    void refreshFolderTree();
  }

  // --- Duplicate hunt ----------------------------------------------------
  // Flow (issue #6): pin the currently-playing video as the "anchor"
  // (the video you suspect has copies), keep arrow-navigating through
  // the grid, and press "Mark as duplicate" on anything that looks like
  // the same content. Pairs land on the /duplicates review page as
  // Pending. An optional one-shot filter narrows the grid to videos
  // within ±N seconds of the anchor's duration.
  let dupAnchor = $state<Video | null>(null);
  let dupMessage = $state<string | null>(null);
  let dupMessageIsError = $state(false);
  let dupToleranceSeconds = $state(10);
  let dupMarking = $state(false);

  // ".NET TimeSpan: [d.]hh:mm:ss[.fffffff]" → total seconds (0 when
  // unparseable, which keeps the comparison safe for odd rows).
  function tsToSeconds(ts: string | null | undefined): number {
    if (!ts) return 0;
    const m = ts.match(/^(?:(\d+)\.)?(\d+):(\d{2}):(\d{2})(?:\.(\d+))?$/);
    if (!m) return 0;
    const days = m[1] ? parseInt(m[1], 10) : 0;
    return ((days * 24 + parseInt(m[2], 10)) * 60 + parseInt(m[3], 10)) * 60
      + parseInt(m[4], 10);
  }

  function setDupAnchor() {
    if (!playingVideo) return;
    dupAnchor = playingVideo;
    dupMessage = null;
    dupMessageIsError = false;
  }

  function clearDupAnchor() {
    dupAnchor = null;
    dupMessage = null;
    dupMessageIsError = false;
  }

  async function markCurrentAsDuplicate() {
    if (!dupAnchor || !playingVideo || dupMarking) return;
    if (playingVideo.id === dupAnchor.id) {
      dupMessage = 'This IS the anchor video — navigate to a different video first.';
      dupMessageIsError = true;
      return;
    }
    dupMarking = true;
    try {
      await api.createDuplicate(dupAnchor.id, playingVideo.id);
      dupMessage = `Marked "${playingVideo.fileName}" as a possible duplicate.`;
      dupMessageIsError = false;
    } catch (e: any) {
      dupMessage = e?.message ?? 'Failed to mark duplicate';
      dupMessageIsError = true;
    } finally {
      dupMarking = false;
    }
  }

  // One-shot narrowing of the current grid to videos whose duration is
  // within ±tolerance of the anchor's. Deliberately not a live filter:
  // it transforms the list in place (anchor always kept) and the next
  // filter-store change or "Show all" click re-fetches the full set.
  function filterSimilarDuration() {
    if (!dupAnchor) return;
    const anchorSeconds = tsToSeconds(dupAnchor.duration);
    const tol = Math.max(0, dupToleranceSeconds);
    videos = videos.filter(v =>
      v.id === dupAnchor!.id
      || Math.abs(tsToSeconds(v.duration) - anchorSeconds) <= tol);
    // Client-side narrowing of the loaded set — stop paginating so a later
    // scroll doesn't append unfiltered pages over the narrowed view, and the
    // "N of M" badge reflects the narrowed count.
    nextCursor = null;
    totalCount = videos.length;
    dupMessage = `Narrowed to ${videos.length} video${videos.length === 1 ? '' : 's'} within ±${tol}s of the anchor.`;
    dupMessageIsError = false;
  }

  // 1-based position of the playing video within the current grid order.
  // Feeds the "x of y" badge in the player's title row. null when the
  // playing video isn't part of the filtered list (e.g. a ?id= deep-link
  // whose row was later clobbered by a filter change).
  const playingIndex = $derived.by<number | null>(() => {
    if (!playingVideo) return null;
    const idx = videos.findIndex(v => v.id === playingVideo!.id);
    return idx >= 0 ? idx + 1 : null;
  });

  // Move to the next/previous video in the current grid order. Wired into
  // VideoPlayer's arrow-key handlers; the player gates by `tagsPanelOpen`
  // so plain arrows nav when the panel is closed and Shift+arrows do when
  // it's open.
  async function goNext() {
    if (!playingVideo) return;
    const idx = videos.findIndex(v => v.id === playingVideo!.id);
    if (idx < 0) return;
    // At the end of the loaded pages but more exist → pull the next page first
    // so autoplay/Next doesn't dead-end before the result set is exhausted (#127).
    if (idx + 1 >= videos.length && nextCursor) await loadMore();
    if (idx + 1 < videos.length) playingVideo = videos[idx + 1];
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

  // Rename the currently-playing video's file (#172). Throws on failure so the
  // FileInfoPanel can surface the server's message inline.
  async function renameCurrentVideo(newName: string) {
    if (!playingVideo) return;
    const updated = await api.renameVideo(playingVideo.id, newName);
    playingVideo = updated;
    patchVideoInGrid(updated);
  }

  onMount(loadSidebar);
</script>

<svelte:head><title>Videos - Video Organizer</title></svelte:head>

<!-- Page-level 'G' = toggle Grid view (issue #41); see onBrowseKey. -->
<svelte:window onkeydown={onBrowseKey} />

<div class="flex flex-col gap-4">
  <!-- Local-only diagnostic affordances live in the player's
       Playback Issue overlay — banner reminds remote users those
       buttons won't appear. -->
  <RemoteHostBanner />
  <h1 class="text-2xl font-semibold">Videos</h1>

  {#if loadError}
    <div class="alert alert-error" role="alert" aria-live="assertive">
      <span>{loadError}</span>
      <button class="btn btn-sm" onclick={() => (loadError = null)}>Dismiss</button>
    </div>
  {/if}

  {#if isEmptyInstall}
    <!-- First-run / empty-library welcome (issue #139). The app no longer
         requires a video source up front, so instead of bouncing to a
         setup page we explain what's missing and offer the next step,
         while leaving the rest of the app reachable via the nav. -->
    <div class="hero bg-base-200 rounded-box py-16">
      <div class="hero-content text-center max-w-xl">
        <div class="flex flex-col items-center gap-4">
          <svg class="w-16 h-16 text-base-content/40" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
            <path d="M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zm6 4v8l6-4-6-4z" />
          </svg>
          <h2 class="text-2xl font-semibold">Your library is empty</h2>
          {#if hasAnySource}
            <p class="text-base-content/70">
              You've added a source, but no videos have been imported yet.
              Run an import to scan it and start browsing.
            </p>
            <div class="flex flex-wrap gap-2 justify-center mt-2">
              <a href="/import" class="btn btn-primary">Import videos</a>
              <a href="/sources" class="btn btn-ghost">Manage sources</a>
            </div>
          {:else}
            <p class="text-base-content/70">
              No video source is configured yet. Add a folder of videos as a
              source, then import it — or explore the rest of the app from the
              menu in the meantime.
            </p>
            <div class="flex flex-wrap gap-2 justify-center mt-2">
              <a href="/sources" class="btn btn-primary">Add a source</a>
              <a href="/import" class="btn btn-ghost">Import tool</a>
            </div>
          {/if}
        </div>
      </div>
    </div>
  {:else}
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
              onclick={() => (flagModalItem = item)}
              title="{itemLabel}: {flagStateLabel(state)} — click to filter"
              aria-label="{itemLabel}, currently {flagStateLabel(state)}, click to filter"
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
                  {:else if item.def.value === 'markedForDeletion'}
                    <svg viewBox="0 0 24 24" class="h-3 w-3 fill-current" style="color: rgb(239 68 68);">
                      <path d="M9 3a1 1 0 00-1 1v1H5a1 1 0 100 2h14a1 1 0 100-2h-3V4a1 1 0 00-1-1H9zm-2 6v11a2 2 0 002 2h6a2 2 0 002-2V9H7zm2 2h2v8H9v-8zm4 0h2v8h-2v-8z" />
                    </svg>
                  {:else}
                    <!-- Clip/edit flags (clip, embedded, exported, edited):
                         scissor icon, yellow-400 to match the clip indicator
                         on the VideoCard thumbnail. -->
                    <svg viewBox="0 0 24 24" class="h-3 w-3 fill-current" style="color: rgb(250 204 21);">
                      <path d="M9.64 7.64c.23-.5.36-1.05.36-1.64 0-2.21-1.79-4-4-4S2 3.79 2 6s1.79 4 4 4c.59 0 1.14-.13 1.64-.36L10 12l-2.36 2.36C7.14 14.13 6.59 14 6 14c-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4c0-.59-.13-1.14-.36-1.64L12 14l7 7h3v-1L9.64 7.64zM6 8c-1.1 0-2-.89-2-2s.9-2 2-2 2 .89 2 2-.9 2-2 2zm0 12c-1.1 0-2-.89-2-2s.9-2 2-2 2 .89 2 2-.9 2-2 2zm6-7.5c-.28 0-.5-.22-.5-.5s.22-.5.5-.5.5.22.5.5-.22.5-.5.5zM19 3l-6 6 2 2 7-7V3z" />
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

      {#if favoriteTagsByGroup.length > 0}
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
          <!-- Favorite Tags rendered as a collapsible tree, matching
               the shape used by Tag Groups, Sources, and Related Tags.
               Each tag group is a parent row with a chevron + count of
               favorites in that group; expanding shows the favorited
               tags as indented leaf rows with the standard count badge
               + ✎ edit affordance. Replaces the previous flat pill
               cluster, which scaled poorly past ~20 favorites and broke
               the visual rhythm of the rest of the sidebar. -->
          <div class="bg-base-100 rounded-box p-1 text-sm">
            {#each favoriteTagsByGroup as g (g.groupId)}
              {@const isExpanded = expandedFavoriteGroups.has(g.groupId)}
              <div>
                <div class="flex items-center gap-1 hover:bg-base-200 rounded">
                  <button
                    type="button"
                    class="shrink-0 w-5 h-5 flex items-center justify-center text-base-content/70 hover:text-base-content"
                    aria-label={isExpanded ? `Collapse ${g.groupName}` : `Expand ${g.groupName}`}
                    title={isExpanded ? 'Collapse' : 'Expand'}
                    onclick={() => toggleFavoriteGroup(g.groupId)}
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
                    onclick={() => toggleFavoriteGroup(g.groupId)}
                    title={isExpanded ? `Collapse ${g.groupName}` : `Expand ${g.groupName}`}
                  >{g.groupName}</button>
                  <span
                    class="shrink-0 text-xs tabular-nums opacity-50"
                    title="{g.tags.length} favorited tag{g.tags.length === 1 ? '' : 's'} in {g.groupName}"
                  >{g.tags.length}</span>
                </div>
                {#if isExpanded}
                  {#each g.tags as t (t.id)}
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
                        title={slot ? `In filter: ${slot}` : `Filter by ${g.groupName}: ${t.name}`}
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
                {/if}
              </div>
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
                      <!-- Filter-membership dot: a tag currently in the
                           filter shows a slot-colored dot (issue #80). -->
                      <span class="shrink-0 w-5 h-5 flex items-center justify-center" title={slot ? `In filter: ${slot}` : undefined}>
                        {#if slot}<span class="w-2 h-2 rounded-full {filterSlotDot(slot)}"></span>{/if}
                      </span>
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
                          videoCount: rt.count,
                          hiddenByDefault: false
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
          <div class="flex items-center gap-1 mb-1">
            <button
              type="button"
              class="flex items-center gap-1 flex-1 min-w-0 text-left hover:bg-base-200 rounded px-1 py-0.5"
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
              <h3 class="font-semibold text-sm">Sources</h3>
            </button>
            <button
              type="button"
              class="shrink-0 w-6 h-6 flex items-center justify-center rounded text-base-content/60 hover:text-base-content hover:bg-base-200 disabled:opacity-40"
              onclick={() => refreshFolderTree(true)}
              disabled={folderTreeRefreshing}
              aria-label="Refresh folder tree"
              title="Refresh folder tree"
            >
              <svg
                xmlns="http://www.w3.org/2000/svg"
                viewBox="0 0 24 24"
                class="h-3.5 w-3.5 fill-current {folderTreeRefreshing ? 'animate-spin' : ''}"
              >
                <path d="M17.65 6.35A7.96 7.96 0 0 0 12 4a8 8 0 1 0 7.74 10h-2.08A6 6 0 1 1 12 6c1.66 0 3.14.69 4.22 1.78L13 11h7V4l-2.35 2.35z" />
              </svg>
            </button>
          </div>
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
            {#key folderTreeSeed}
              {#each visibleFolderRoots as root (root.fullPath)}
                {@const matchingSet = sets.find(s => s.path === root.fullPath || s.name === root.name)}
                <FolderTreeNode
                  name={root.name}
                  fullPath={root.fullPath}
                  hasSubdirectories={root.hasSubdirectories}
                  depth={0}
                  videoCount={root.videoCount}
                  importedCount={root.importedCount}
                  enabled={matchingSet ? matchingSet.enabled : true}
                  onPickFolder={(path, label) =>
                    filterStore.requestAdd({ type: 'folder', value: path, label })}
                  onRemoveFolder={askRemoveFolder}
                />
              {/each}
            {/key}
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
                    <!-- Filter-membership dot: a tag currently in the
                         filter shows a slot-colored dot (issue #80). -->
                    <span class="shrink-0 w-5 h-5 flex items-center justify-center" title={slot ? `In filter: ${slot}` : undefined}>
                      {#if slot}<span class="w-2 h-2 rounded-full {filterSlotDot(slot)}"></span>{/if}
                    </span>
                    <button
                      type="button"
                      class="flex-1 min-w-0 text-left truncate py-1 hover:underline {t.hiddenByDefault ? 'text-base-content/45' : ''}"
                      onclick={() => pickTag(t)}
                      title={t.hiddenByDefault
                        ? `Hidden by default — filter for "${t.name}" to see its videos`
                        : slot ? `In filter: ${slot}` : `Filter by ${g.name}: ${t.name}`}
                    >{t.name}</button>
                    {#if t.hiddenByDefault}
                      <!-- Hidden-by-default marker (issue #84). -->
                      <span class="shrink-0 text-[10px] italic text-base-content/40" title="Hidden by default">(default hidden)</span>
                    {/if}
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
    <!-- min-w-0 overrides the implicit min-width: min-content that
         CSS grid items default to. Without it, this section stretches
         past its 1fr allotment to fit the player + panels + thumbs at
         their natural sizes — which pushes the side panels (Tags /
         File Info) off the right edge of the viewport when the user
         toggles them on. With min-w-0, the section honours 1fr; the
         flex-1 content column inside then shrinks to make room for
         the panels, and the cascading max-w-full chain on the player
         wrappers shrinks the video to fit. -->
    <section class="relative flex flex-row min-w-0">
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
      {#if !filterStore.isEmpty() || playingVideo || hiddenByTagCount > 0}
        <div class="sticky top-0 z-10 flex flex-col">
          <!-- Auto-hide transparency (#84): videos matching the current view but
               carrying a hidden-by-default tag are suppressed from the grid.
               Surface the count so the result total never silently mismatches a
               flag/tag badge — without revealing the hidden videos themselves. -->
          {#if hiddenByTagCount > 0}
            <div
              class="bg-base-100 border border-base-300 rounded-box px-3 py-1.5 mb-2 flex items-center gap-2 text-xs text-base-content/60"
              title="These match the current filter but carry a hidden-by-default tag. Filter for that tag to see them."
            >
              <span class="badge badge-ghost badge-sm shrink-0 tabular-nums">{hiddenByTagCount} hidden</span>
              <span>{hiddenByTagCount === 1 ? '1 video is' : `${hiddenByTagCount} videos are`} hidden by a hidden-by-default tag.</span>
            </div>
          {/if}
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
              <button class="btn btn-xs shrink-0" onclick={() => filterStore.clear()}>Clear Filters</button>
              {#if filterStore.required.length > 0}
                <div class="flex items-center gap-1 flex-wrap">
                  <span class="text-xs text-base-content/60">Required</span>
                  {#each filterStore.required as t (`req-${t.type}-${t.value}`)}
                    <!-- Filter chip — max-w + truncate so long
                         labels ellipsize instead of pushing the
                         row to wrap or scroll. Same pattern reused
                         by Optional / Excluded below. Clicking the
                         label cycles the slot (issue #80); × removes. -->
                    <span class="badge {filterSlotClass('required')} gap-1 max-w-[min(14rem,100%)] flex-nowrap">
                      <button
                        type="button"
                        class="truncate min-w-0 cursor-pointer"
                        onclick={() => filterStore.cycle(t)}
                        title={`${t.label} — Required. Click to cycle: Required → Optional → Excluded`}
                      >{t.label}</button>
                      <button class="shrink-0" onclick={() => filterStore.remove(t)} aria-label="Remove {t.label}">×</button>
                    </span>
                  {/each}
                </div>
              {/if}
              {#if filterStore.optional.length > 0}
                <div class="flex items-center gap-1 flex-wrap">
                  <span class="text-xs text-base-content/60">Optional</span>
                  {#each filterStore.optional as t (`opt-${t.type}-${t.value}`)}
                    <span class="badge {filterSlotClass('optional')} gap-1 max-w-[min(14rem,100%)] flex-nowrap">
                      <button
                        type="button"
                        class="truncate min-w-0 cursor-pointer"
                        onclick={() => filterStore.cycle(t)}
                        title={`${t.label} — Optional. Click to cycle: Required → Optional → Excluded`}
                      >{t.label}</button>
                      <button class="shrink-0" onclick={() => filterStore.remove(t)} aria-label="Remove {t.label}">×</button>
                    </span>
                  {/each}
                </div>
              {/if}
              {#if filterStore.excluded.length > 0}
                <div class="flex items-center gap-1 flex-wrap">
                  <span class="text-xs text-base-content/60">Excluded</span>
                  {#each filterStore.excluded as t (`exc-${t.type}-${t.value}`)}
                    <span class="badge {filterSlotClass('excluded')} gap-1 max-w-[min(14rem,100%)] flex-nowrap">
                      <button
                        type="button"
                        class="truncate min-w-0 cursor-pointer"
                        onclick={() => filterStore.cycle(t)}
                        title={`${t.label} — Excluded. Click to cycle: Required → Optional → Excluded`}
                      >{t.label}</button>
                      <button class="shrink-0" onclick={() => filterStore.remove(t)} aria-label="Remove {t.label}">×</button>
                    </span>
                  {/each}
                </div>
              {/if}
            </div>
          {/if}

          {#if dupAnchor}
            <!-- Duplicate-hunt bar. Lives in the sticky wrapper so the
                 anchor context stays visible while the user navigates
                 the grid looking for copies. -->
            <div class="bg-base-100 border border-warning/50 rounded-box px-3 py-2 mb-2 flex items-center gap-2 flex-wrap text-sm">
              <span class="badge badge-warning badge-sm uppercase tracking-wide shrink-0">Dup hunt</span>
              <span class="text-base-content/60 shrink-0">Anchor:</span>
              <button
                type="button"
                class="link link-hover truncate max-w-[18rem]"
                title="Jump back to the anchor video"
                onclick={() => { if (dupAnchor) playingVideo = dupAnchor; }}
              >{dupAnchor.fileName}</button>
              <button
                type="button"
                class="btn btn-warning btn-xs"
                disabled={dupMarking || !playingVideo || playingVideo.id === dupAnchor.id}
                onclick={markCurrentAsDuplicate}
                title="Flag the currently playing video as a possible duplicate of the anchor"
              >
                {#if dupMarking}<span class="loading loading-spinner loading-xs"></span>{/if}
                Mark current as duplicate
              </button>
              <label class="flex items-center gap-1 text-xs text-base-content/60" title="Narrow the grid to videos with a similar duration">
                ±
                <input
                  type="number"
                  class="input input-bordered input-xs w-16 tabular-nums"
                  min="0"
                  bind:value={dupToleranceSeconds}
                />
                s
              </label>
              <button
                type="button"
                class="btn btn-xs"
                onclick={filterSimilarDuration}
                title="Filter the grid to videos within ±{dupToleranceSeconds}s of the anchor's duration"
              >Similar length</button>
              <button
                type="button"
                class="btn btn-xs"
                onclick={() => refreshVideos()}
                title="Re-fetch the full filtered list (undoes Similar length)"
              >Show all</button>
              <a class="btn btn-xs btn-ghost" href="/duplicates" title="Review flagged pairs">Review →</a>
              <button
                type="button"
                class="btn btn-ghost btn-xs ml-auto"
                onclick={clearDupAnchor}
                aria-label="End duplicate hunt"
                title="End duplicate hunt"
              >✕</button>
              {#if dupMessage}
                <span class="basis-full {dupMessageIsError ? 'text-error' : 'text-success'} text-xs">{dupMessage}</span>
              {/if}
            </div>
          {/if}

          {#if playingVideo && viewMode === 'player'}
            <!-- Player area — `min-height` tracks the divider so
                 dragging down enlarges the card (and lets the video
                 grow into the new room). The video's own `max-height`
                 is wired to the same divider value via
                 `maxVideoHeightPx` below — that's what makes dragging
                 UP actually shrink the picture. Without that wiring
                 the video holds at its 70vh default no matter how
                 high you drag. Subtract ~72px of wrapper chrome
                 (p-3 + button row + gap) so the picture fits cleanly
                 inside the wrapper. Hidden in grid view (issue #23). -->
            <div
              bind:this={playerCardEl}
              class="card bg-base-200 p-3"
              style="height: {playerHeight}px;"
            >
              <VideoPlayer
                bind:video={playingVideo}
                shortcutsEnabled={true}
                maxVideoHeightPx={Math.max(100, playerHeight - 72)}
                playlistIndex={playingIndex}
                playlistTotal={totalCount}
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

      <!-- Header row — count, loading indicator, thumb-size slider,
           reshuffle. flex-wrap so the slider drops to its own row on
           narrow viewports instead of squeezing into nothing. -->
      <div bind:this={playerHeaderRowEl} class="flex justify-between items-center shrink-0 mb-2 mt-2 gap-3 flex-wrap">
        <p class="text-sm text-base-content/70">
          {#if videosLoading}
            Loading…
          {:else}
            {videos.length} loaded{#if nextCursor} · more on scroll{/if}
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
          {#if !dupAnchor}
            <button
              type="button"
              class="btn btn-sm"
              disabled={!playingVideo}
              onclick={setDupAnchor}
              title="Start a duplicate hunt: pin the playing video as the anchor, then navigate and mark look-alikes"
            >🎯 Find duplicates</button>
          {/if}
          <!-- Move the currently-playing video's file to another folder
               (issue #4). The grid cards carry their own Move… button. -->
          <button
            type="button"
            class="btn btn-sm"
            disabled={!playingVideo || playingVideo.isClip}
            onclick={() => playingVideo && openMoveDialog(playingVideo)}
            title="Move the current video's file to another folder"
          >↪ Move file</button>
          <!-- Sort mode select + direction toggle. Shuffle is the default
               random-playlist behavior; explicit modes re-order the grid
               client-side. The ↑↓ button flips ascending/descending and
               is hidden in Shuffle mode (direction is meaningless for a
               random order). -->
          <label class="flex items-center gap-1 text-xs text-base-content/60" title="Sort order">
            <span>Sort</span>
            <select
              class="select select-bordered select-sm"
              value={sortMode}
              onchange={(e) => onSortModeChange((e.currentTarget as HTMLSelectElement).value as SortMode)}
            >
              {#each SORT_MODES as m (m.value)}
                <option value={m.value}>{m.label}</option>
              {/each}
            </select>
          </label>
          {#if sortMode !== 'shuffle'}
            <button
              type="button"
              class="btn btn-sm btn-ghost px-2"
              onclick={toggleSortDirection}
              title={sortDescending ? 'Descending — click for ascending' : 'Ascending — click for descending'}
              aria-label={sortDescending ? 'Sort descending, click to sort ascending' : 'Sort ascending, click to sort descending'}
            >{sortDescending ? '↓' : '↑'}</button>
          {/if}
          <button
            type="button"
            class="btn btn-sm"
            disabled={videos.length === 0 || videosLoading}
            onclick={reshufflePlaylist}
            title="Shuffle the current grid into a new random playlist and jump to the first video"
          >🔀 Reshuffle</button>
          <!-- View toggle (issue #23): Player mode = video + single-row
               thumbnail strip; Grid mode = thumbnails-only grid with the
               player hidden. -->
          <button
            type="button"
            class="btn btn-sm"
            onclick={() => setViewMode(viewMode === 'player' ? 'grid' : 'player')}
            title={viewMode === 'player'
              ? 'Switch to grid view (hide the player, show all thumbnails)'
              : 'Switch to player view (video with a thumbnail strip)'}
            aria-label={viewMode === 'player' ? 'Switch to grid view' : 'Switch to player view'}
          >{viewMode === 'player' ? '▦ Grid' : '▶ Player'}</button>
        </div>
      </div>

      {#if viewMode === 'player'}
        <!-- Player-mode thumbnail strip (issues #23, #37). One horizontal
             row of the whole queue; the currently-playing thumbnail is
             kept CENTERED, with the earlier videos to its left and the
             upcoming ones to its right. VideoCard centers itself when
             active (centerOnActive) — near the ends the browser clamps the
             scroll so it sits as close to center as the queue allows. -->
        <div bind:this={playerStripEl} class="flex gap-3 overflow-x-auto pb-2">
          {#each videos as v (v.id)}
            <div class="shrink-0" style="width: {thumbWidth}px;">
              <VideoCard
                video={v}
                onopen={open}
                onmove={openMoveDialog}
                active={playingVideo?.id === v.id}
                centerOnActive
              />
            </div>
          {/each}
          <!-- Horizontal sentinel: the IntersectionObserver loads the
               next chunk as the user scrolls toward the right end. -->
          {#if nextCursor}
            <div
              bind:this={scrollSentinelEl}
              class="shrink-0 w-32 flex items-center justify-center text-xs text-base-content/50"
            >
              Loading more…
            </div>
          {/if}
        </div>

        {#if !videosLoading && videos.length === 0}
          <p class="text-base-content/60">No videos match the current filter.</p>
        {/if}
      {:else}
        <!-- Grid view (issue #23) — thumbnails-only browsing, player
             hidden. Natural document flow; auto-fill + minmax so the
             thumbnail-size slider reflows columns live. -->
        <div>
          <div
            class="grid gap-3"
            style="grid-template-columns: repeat(auto-fill, minmax({thumbWidth}px, 1fr));"
          >
            {#each videos as v (v.id)}
              <VideoCard
                video={v}
                onopen={open}
                onmove={openMoveDialog}
                active={playingVideo?.id === v.id}
              />
            {/each}
          </div>

          <!-- Sentinel: empty div the IntersectionObserver watches for to
               load the next chunk. Lives inside the scroll region so its
               intersections fire against the right root. -->
          {#if nextCursor}
            <div bind:this={scrollSentinelEl} class="h-12 flex items-center justify-center text-xs text-base-content/50">
              Loading more…
            </div>
          {/if}

          {#if !videosLoading && videos.length === 0}
            <p class="text-base-content/60">No videos match the current filter.</p>
          {/if}
        </div>
      {/if}
      </div>
      <!-- ↑ end of content column -->

      <!-- Side panels — File Info on the inside (closer to the video,
           since reading metadata pairs naturally with watching), Tags
           on the outside. Each is a fixed 360px sticky column visible
           only while its own toggle is true; the I and T keys flip
           them independently. Both can be open at once on wide enough
           viewports. Each panel has internal overflow-y-auto + max-
           h-screen so they stay visible during thumbnail scroll and
           self-scroll when content is tall. Hidden in grid view, which
           has no player to pair them with (issue #23). -->
      {#if showFileInfo && playingVideo && viewMode === 'player'}
        <div
          class="sticky top-0 z-10 self-start max-h-screen w-[360px] shrink-0 overflow-y-auto bg-base-200 border-l border-base-300 shadow-xl"
        >
          <FileInfoPanel
            bind:show={showFileInfo}
            video={playingVideo}
            onRename={renameCurrentVideo}
          />
        </div>
      {/if}
      {#if showEditTagsPanel && playingVideo && viewMode === 'player'}
        <div
          class="sticky top-0 z-10 self-start max-h-screen w-[360px] shrink-0 overflow-y-auto bg-base-200 border-l border-base-300 shadow-xl"
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
  {/if}
</div>

<MoveFileDialog
  video={moveDialogVideo}
  show={moveDialogVideo !== null}
  onClose={() => (moveDialogVideo = null)}
  onMoved={onFileMoved}
/>

<!-- Remove-folder confirmation (issue #53). Destructive: purges the folder's
     imported videos from the library (files on disk are kept). -->
{#if removeFolderTarget}
  {@const t = removeFolderTarget}
  <div class="modal modal-open" role="dialog" aria-modal="true">
    <div class="modal-box max-w-md">
      <h3 class="font-bold text-lg">Remove folder from library</h3>
      <p class="text-sm mt-2">
        Remove <span class="font-medium break-all">{t.label}</span> from the library?
      </p>
      <p class="text-sm mt-2">
        This deletes <span class="font-medium">{t.count}</span>
        imported video{t.count === 1 ? '' : 's'} (and their tags, notes, and properties)
        from the library. <span class="font-medium">Files on disk are not deleted.</span>
      </p>
      {#if removeFolderError}
        <div class="alert alert-error text-sm mt-3">{removeFolderError}</div>
      {/if}
      <div class="modal-action">
        <button class="btn btn-sm btn-cancel" onclick={() => (removeFolderTarget = null)} disabled={removingFolder}>
          Cancel
        </button>
        <button class="btn btn-sm btn-error" onclick={confirmRemoveFolder} disabled={removingFolder}>
          {#if removingFolder}<span class="loading loading-spinner loading-xs"></span>{/if}
          Remove {t.count} from library
        </button>
      </div>
    </div>
    <button
      type="button"
      class="modal-backdrop"
      aria-label="Cancel"
      onclick={() => { if (!removingFolder) removeFolderTarget = null; }}
    ></button>
  </div>
{/if}

<!-- Flag filter modal (issue #2). Clicking a flag in the Flags tree
     opens this to pick Required / Excluded (or Clear when one is set),
     instead of cycling the state in place. -->
{#if flagModalItem}
  {@const fi = flagModalItem}
  {@const fs = flagState(fi)}
  <div class="modal modal-open" role="dialog" aria-modal="true">
    <div class="modal-box max-w-sm">
      <h3 class="font-bold text-lg">Filter by flag</h3>
      <p class="text-sm mt-1 mb-4">
        <span class="font-medium">{flagItemLabel(fi)}</span> —
        currently <span class="font-medium">{flagStateLabel(fs)}</span>
      </p>
      <div class="flex gap-2">
        <button
          class="btn btn-sm btn-soft btn-success border border-success/50 flex-1"
          onclick={() => { applyFlag(fi, 'true'); flagModalItem = null; }}
        >Required</button>
        <button
          class="btn btn-sm btn-soft btn-error border border-error/50 flex-1"
          onclick={() => { applyFlag(fi, 'false'); flagModalItem = null; }}
        >Excluded</button>
      </div>
      {#if fs !== 'nofilter'}
        <button
          class="btn btn-sm btn-ghost w-full mt-2"
          onclick={() => { applyFlag(fi, 'nofilter'); flagModalItem = null; }}
        >Clear filter</button>
      {/if}
      <div class="modal-action mt-4">
        <button class="btn btn-sm btn-cancel" onclick={() => (flagModalItem = null)}>Cancel</button>
      </div>
    </div>
    <button
      type="button"
      class="modal-backdrop"
      aria-label="Cancel flag filter"
      onclick={() => (flagModalItem = null)}
    ></button>
  </div>
{/if}

<FilterDialog
  onTagChanged={async () => {
    // Tag rename / delete from inside the dialog — refresh the
    // sidebar (tag tree, counts, favorites) AND the playing video
    // pane (so its tag pills lose the deleted tag or pick up the
    // rename) AND the grid (so card tag pills update too).
    await refreshSidebarTagCounts();
    await refreshPlaying();
    void refreshVideos();
  }}
  onVideoChanged={async (videoId) => {
    // "Remove tag from this video" — re-fetch just that one row
    // and patch it into the grid so its tag pill disappears
    // immediately. If the removed tag was on the currently
    // playing video, refreshPlaying() also picks it up.
    try {
      const updated = await api.getVideo(videoId);
      if (updated) patchVideoInGrid(updated);
      if (playingVideo?.id === videoId) await refreshPlaying();
      // Tag-count badges on the sidebar tree key off video counts
      // — drop one usage and the count needs to redraw.
      await refreshSidebarTagCounts();
    } catch { /* non-fatal — UI re-syncs on next nav / reload */ }
  }}
/>
<TagEditModal bind:show={editTagModalShow} tag={editingTag} onSaved={onTagSavedFromSidebar} />

<!-- Slow-load diagnostics dialog (issue #71). Pops when the Videos page
     hasn't finished its sidebar + first-queue loads within SLOW_LOAD_MS, so
     a refresh/reload that stalls shows exactly which component is dragging
     instead of a blank grid. Same probe table as the startup landing page. -->
{#if showLoadStatus}
  <div class="modal modal-open" role="dialog" aria-modal="true">
    <div class="modal-box max-w-3xl">
      <header class="mb-3">
        <h3 class="text-lg font-semibold">Still loading…</h3>
        <p class="text-sm text-base-content/70 mt-1">
          The Videos page is taking more than {(SLOW_LOAD_MS / 1000).toFixed(0)} seconds
          to load. These checks time the data fetches that drive first paint —
          anything over 2 seconds is highlighted. The page keeps loading behind
          this dialog.
        </p>
      </header>

      <StartupStatus />

      <div class="modal-action">
        <button class="btn" onclick={() => (showLoadStatus = false)}>Close</button>
      </div>
    </div>
    <button
      type="button"
      class="modal-backdrop"
      aria-label="Close"
      onclick={() => (showLoadStatus = false)}
    ></button>
  </div>
{/if}


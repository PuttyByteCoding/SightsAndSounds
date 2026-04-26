<script lang="ts">
  // Reusable watching experience shared by the Video Player page and the
  // Video Browser page. Owns: the <video> element, the custom scrubber with
  // hover preview, current-time / duration readout, tag badges under the
  // video, and all keyboard shortcuts that act on "the current video" (seek,
  // favorite, won't-play, delete, undo, arrow-save-and-navigate).
  //
  // Does NOT own: playlist logic, Edit Tags side panel, Create Performer
  // dialog, File Info modal, or the "related videos" strip. The host page
  // composes those around this component as needed.

  import { tick, onMount } from 'svelte';
  import { api } from '$lib/api';
  import type { Tag, Video } from '$lib/types';
  import { playbackSettings } from '$lib/playbackSettings.svelte';
  import { filterStore } from '$lib/filterStore.svelte';
  import { pillClass } from '$lib/tagColors';
  import TagEditModal from './TagEditModal.svelte';

  interface Props {
    video: Video | null;
    // Fired when the user presses ← (after save-if-dirty completes). Host
    // decides what "previous" means (playlist, grid order, etc.).
    onRequestPrev?: () => void | Promise<void>;
    // Fired when the user presses →, or after a successful W/D mark-and-move,
    // or after R clears Needs Review.
    onRequestNext?: () => void | Promise<void>;
    // Opt-in: disable window-level shortcuts. Useful if a host wants to put
    // the player inside a modal that has its own key bindings.
    shortcutsEnabled?: boolean;
    // Host-controlled cap on the video element's height (px). When set, it
    // overrides the default 70vh ceiling so a draggable split bar on the
    // page can resize the player.
    maxVideoHeightPx?: number | null;
    // When true, plain ←/→ for prev/next is disabled — Shift is required.
    // Host sets this while the Edit Tags panel is open so accidental arrows
    // (e.g. while reaching for the keyboard) don't jump videos.
    tagsPanelOpen?: boolean;
    // Fired when the user presses T. Host owns the panel state.
    onToggleTags?: () => void;
  }

  let {
    video = $bindable(),
    onRequestPrev,
    onRequestNext,
    shortcutsEnabled = true,
    maxVideoHeightPx = null,
    tagsPanelOpen = false,
    onToggleTags
  }: Props = $props();

  // --- Internal state -------------------------------------------------------

  let videoEl: HTMLVideoElement | null = $state(null);

  // Tags from the first checkbox-mode group (i.e. the Flags group, by default)
  // ordered by SortOrder. Loaded once on mount; Alt+1..9 toggles the Nth.
  let flagTags = $state<Tag[]>([]);
  onMount(async () => {
    try {
      const groups = await api.listTagGroups();
      const flagGroup = groups
        .filter(g => g.displayAsCheckboxes)
        .sort((a, b) => a.sortOrder - b.sortOrder)[0];
      if (flagGroup) flagTags = await api.listTags({ groupId: flagGroup.id });
    } catch { /* non-fatal — Alt+digit just won't do anything */ }
  });

  // --- Tag edit modal ----------------------------------------------------
  let editTagModalShow = $state(false);
  let editingTag = $state<Tag | null>(null);

  async function openEditTagModal(tagId: string) {
    try {
      editingTag = await api.getTag(tagId);
      editTagModalShow = true;
    } catch (e) {
      errorMessage = `Failed to load tag: ${e instanceof Error ? e.message : String(e)}`;
    }
  }

  async function onTagSavedFromPlayer(saved: Tag) {
    if (!video) return;
    // Reflect new name in the inline pill list.
    const idx = video.tags.findIndex(t => t.id === saved.id);
    if (idx >= 0) {
      video.tags[idx] = {
        id: saved.id,
        tagGroupId: saved.tagGroupId,
        tagGroupName: saved.tagGroupName,
        name: saved.name
      };
    }
    // If the saved tag is in the flag group, refresh flagTags.
    if (flagTags.some(t => t.id === saved.id)) {
      const fIdx = flagTags.findIndex(t => t.id === saved.id);
      flagTags[fIdx] = saved;
    }
  }

  // Toggle the Nth flag tag (1-based) on the current video. Saves immediately
  // via api.setVideoTags and updates video.tags locally so badges reflect it.
  async function toggleFlagAt(oneBasedIdx: number) {
    if (!video) return;
    if (flagTags.length === 0) {
      errorMessage = `Alt+${oneBasedIdx}: no checkbox-mode tag group is configured. ` +
        `Open Tag Management and turn on "Display as checkboxes" for one of your groups.`;
      return;
    }
    const tag = flagTags[oneBasedIdx - 1];
    if (!tag) {
      errorMessage = `Alt+${oneBasedIdx}: only ${flagTags.length} flag tag(s) defined.`;
      return;
    }
    const has = video.tags.some(t => t.id === tag.id);
    const nextTagIds = has
      ? video.tags.filter(t => t.id !== tag.id).map(t => t.id)
      : [...video.tags.map(t => t.id), tag.id];
    try {
      await api.setVideoTags(video.id, { tagIds: nextTagIds });
      // Re-fetch and reassign rather than mutate. Reassigning the bindable
      // prop is the most reliable way to force every consumer (the inline
      // pills, EditTagsPanel via its bind:video, etc.) to react.
      const fresh = await api.getVideo(video.id);
      if (fresh) video = fresh;
      loadedVideoSnapshot = JSON.stringify(video);
    } catch (e) {
      errorMessage = `Failed to toggle flag: ${e instanceof Error ? e.message : String(e)}`;
    }
  }

  // Scrubber
  interface ScrubFrame { x: number; y: number; w: number; h: number; }
  let scrubFrames = $state<ScrubFrame[]>([]);
  let scrubSpriteSize = $state<{ w: number; h: number }>({ w: 0, h: 0 });
  let scrubBarEl: HTMLDivElement | null = $state(null);
  let scrubHoverX = $state<number | null>(null);
  let scrubHoverTime = $state<number | null>(null);
  let scrubHoverIdx = $state<number | null>(null);
  let videoProgress = $state(0);
  let videoCurrentTime = $state(0);
  let videoDuration = $state(0);
  // Hover tracking for the in-video scrubber overlay. When false, the
  // scrubber fades out so it doesn't obscure the picture; when true (or
  // while actively scrubbing via scrubHoverX), it's fully visible.
  let playerHovered = $state(false);
  const SCRUB_PREVIEW_W = 240;

  // Dirty tracking — JSON snapshot at load so ArrowLeft/Right can decide
  // whether to POST before firing onRequestPrev/Next.
  let loadedVideoSnapshot = $state<string | null>(null);

  // Mark+move (To Delete) busy state. Won't Play is a plain toggle now,
  // so it doesn't participate here.
  let markActionBusy = $state<'delete' | null>(null);
  let errorMessage = $state<string | null>(null);

  // Fade the column during navigation for immediate visual feedback before
  // the save POST + host's onRequestNext/Prev round-trip finishes.
  let isNavigating = $state(false);

  // Whichever video ID we've most recently initialized state for. Used to
  // avoid re-running the reset effect on every unrelated field mutation
  // (e.g. user ticking a flag).
  let initializedForId = $state<string | null>(null);

  // Watch-count beacon: we fire `POST /api/videos/{id}/watched` once per open
  // after the video has been *actually playing* for 10 accumulated seconds.
  // Uses wall-clock deltas sampled on each ontimeupdate so scrubbing doesn't
  // accidentally bump the counter.
  let watchAccumMs = 0;
  let watchLastTickMs: number | null = null;
  let watchBeaconFiredFor: string | null = null;
  const WATCH_BEACON_MS = 10_000;

  // Mark-mode: when true, auto-skip over DoNotShow blocks is disabled so the
  // user can scrub through "hidden" regions to edit them. `pendingBlockStart`
  // is the in-flight start time while the user is defining a new block — B
  // once marks the start, B again commits the block. Both reset per video.
  let markMode = $state(false);
  let pendingBlockStart = $state<number | null>(null);

  // Clip-in-progress: C captures the start, C again calls the API to create
  // the clip. Mirrored pattern to blocks but stored on the server as its own
  // Video row (see api.createClip). Resets per video.
  let pendingClipStart = $state<number | null>(null);
  let clipError = $state<string | null>(null);
  let clipCreating = $state(false);
  // Post-create preview: the newly-made clip opens in a modal so the user
  // can watch it (looping between in/out) and choose Keep or Discard.
  // Closing the modal by any means falls back to Keep.
  let previewingClip = $state<Video | null>(null);
  let previewVideoEl: HTMLVideoElement | null = $state(null);
  let previewDiscarding = $state(false);
  let previewError = $state<string | null>(null);
  // Custom scrubber state — native <video controls> would show the full
  // parent timeline, so we render our own bar scoped to the clip range.
  let previewPaused = $state(true);
  let previewMuted = $state(true);
  let previewCurrent = $state(0); // seconds into the parent file

  // Manual size control. null = fit the column (w-full, capped at both 70vh
  // height and 2x native width). A number = explicit rendered width in px.
  // Resets on every video change so a tweak for one file doesn't stick.
  let videoWidthPx = $state<number | null>(null);
  let videoNativeWidth = $state<number | null>(null);
  // Live-updated via ResizeObserver so the size indicator tracks any change —
  // zoom keys, window resize, column resize with the Edit Tags panel, etc.
  let renderedWidth = $state(0);
  const MAX_ZOOM = 2; // never render larger than 2x the native pixel width
  const ZOOM_STEP = 1.1;

  // --- Derived --------------------------------------------------------------

  const videoUrl = $derived(video ? api.streamUrl(video.id) : '');
  const isMarkedDelete = $derived(video?.markedForDeletion ?? false);

  // Rendered size as a percentage of the native (intrinsic) video dimensions.
  // null while we haven't yet measured native width (metadata not loaded) or
  // the element has no layout width.
  const sizePercent = $derived.by<number | null>(() => {
    if (!videoNativeWidth || videoNativeWidth <= 0) return null;
    if (renderedWidth <= 0) return null;
    return Math.round((renderedWidth / videoNativeWidth) * 100);
  });

  // --- Effects --------------------------------------------------------------

  // Re-init whenever the host swaps in a different video. Fires on every
  // assignment, but the id check skips reset when it's the same row just
  // being mutated.
  $effect(() => {
    if (!video) {
      initializedForId = null;
      loadedVideoSnapshot = null;
      scrubFrames = [];
      scrubSpriteSize = { w: 0, h: 0 };
      videoProgress = 0;
      videoCurrentTime = 0;
      videoDuration = 0;
      videoWidthPx = null;
      videoNativeWidth = null;
      markMode = false;
      pendingBlockStart = null;
      pendingClipStart = null;
      clipError = null;
      previewingClip = null;
      previewError = null;
      return;
    }
    if (video.id === initializedForId) return;
    initializedForId = video.id;
    loadedVideoSnapshot = JSON.stringify(video);
    loadScrubberFrames(video.id);
    // Reset watch-beacon accumulation for the new video.
    watchAccumMs = 0;
    watchLastTickMs = null;
    videoProgress = 0;
    videoCurrentTime = 0;
    videoDuration = 0;
    videoWidthPx = null;
    videoNativeWidth = null;
    markMode = false;
    pendingBlockStart = null;
    pendingClipStart = null;
    clipError = null;
    previewingClip = null;
    previewError = null;
    // After a tick, tell the <video> element to load the new source.
    tick().then(() => {
      if (videoEl) {
        videoEl.muted = true;
        videoEl.load();
        videoEl.play().catch(() => { /* autoplay may be blocked */ });
      }
    });
  });

  // Keep `renderedWidth` in sync with the video element's actual box so the
  // size indicator responds to fit-mode layout changes (window resize, side
  // panel toggling) as well as explicit zooms.
  $effect(() => {
    if (!videoEl) return;
    const ro = new ResizeObserver((entries) => {
      for (const entry of entries) renderedWidth = Math.round(entry.contentRect.width);
    });
    ro.observe(videoEl);
    return () => ro.disconnect();
  });

  // --- Scrubber helpers -----------------------------------------------------

  async function loadScrubberFrames(videoId: string) {
    scrubFrames = [];
    scrubSpriteSize = { w: 0, h: 0 };
    try {
      const res = await fetch(api.thumbnailsVttUrl(videoId));
      if (!res.ok) return;
      const text = await res.text();
      const re = /sprite\.jpg#xywh=(\d+),(\d+),(\d+),(\d+)/g;
      const out: ScrubFrame[] = [];
      let maxRight = 0, maxBottom = 0;
      let m: RegExpExecArray | null;
      while ((m = re.exec(text)) !== null) {
        const x = +m[1], y = +m[2], w = +m[3], h = +m[4];
        out.push({ x, y, w, h });
        if (x + w > maxRight) maxRight = x + w;
        if (y + h > maxBottom) maxBottom = y + h;
      }
      scrubFrames = out;
      scrubSpriteSize = { w: maxRight, h: maxBottom };
    } catch { /* non-fatal */ }
  }

  function onScrubMove(e: MouseEvent) {
    if (!scrubBarEl || !videoEl) return;
    const rect = scrubBarEl.getBoundingClientRect();
    const rel = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
    scrubHoverX = rel * rect.width;
    scrubHoverTime = Number.isFinite(videoEl.duration) ? rel * videoEl.duration : null;
    scrubHoverIdx = scrubFrames.length > 0
      ? Math.max(0, Math.min(scrubFrames.length - 1, Math.floor(rel * scrubFrames.length)))
      : null;
  }

  function onScrubLeave() {
    scrubHoverX = null;
    scrubHoverTime = null;
    scrubHoverIdx = null;
  }

  function onScrubClick(e: MouseEvent) {
    if (!scrubBarEl || !videoEl) return;
    const rect = scrubBarEl.getBoundingClientRect();
    const rel = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
    const d = Number.isFinite(videoEl.duration) ? videoEl.duration : 0;
    if (d > 0) videoEl.currentTime = rel * d;
  }

  function onVideoTimeUpdate() {
    if (!videoEl) return;
    const d = Number.isFinite(videoEl.duration) && videoEl.duration > 0 ? videoEl.duration : 0;
    videoCurrentTime = videoEl.currentTime;
    videoDuration = d;
    videoProgress = d === 0 ? 0 : videoEl.currentTime / d;

    // Accumulate wall-clock playback time and fire the watch beacon once we
    // clear 10 seconds. We sample on ontimeupdate (~4Hz while playing), so a
    // single delta is capped at ~500ms — anything larger means the browser
    // throttled us (hidden tab, scrub) and we skip that slice.
    if (video && watchBeaconFiredFor !== video.id && !videoEl.paused) {
      const now = Date.now();
      if (watchLastTickMs !== null) {
        const delta = now - watchLastTickMs;
        if (delta > 0 && delta < 1500) watchAccumMs += delta;
      }
      watchLastTickMs = now;
      if (watchAccumMs >= WATCH_BEACON_MS) {
        watchBeaconFiredFor = video.id;
        api.markWatched(video.id).catch(() => { /* fire-and-forget */ });
      }
    } else {
      watchLastTickMs = null;
    }

    // Clip loop: once playback reaches the out-point, jump back to the in-
    // point. Only underflow is fine — let the user scrub outside the range
    // freely; we only close the loop on the forward edge.
    if (video?.isClip
        && video.clipStartSeconds !== null && video.clipStartSeconds !== undefined
        && video.clipEndSeconds !== null && video.clipEndSeconds !== undefined) {
      if (videoEl.currentTime >= video.clipEndSeconds) {
        videoEl.currentTime = video.clipStartSeconds;
      }
    }

    // Auto-skip DoNotShow blocks during normal playback. Disabled while the
    // user is editing blocks in mark mode so they can scrub through the
    // "hidden" regions to review them.
    if (!markMode && video && video.videoBlocks?.length > 0) {
      const t = videoEl.currentTime;
      for (const b of video.videoBlocks) {
        if (b.videoBlockType !== 'hide') continue;
        const end = b.offsetInSeconds + b.lengthInSeconds;
        if (t >= b.offsetInSeconds && t < end) {
          // Nudge past the block; the `end` edge is exclusive so landing
          // exactly on it doesn't re-trigger the skip.
          videoEl.currentTime = Math.min(end, d - 0.01);
          break;
        }
      }
    }
  }

  function onVideoLoadedMetadata() {
    if (!videoEl) return;
    videoDuration = Number.isFinite(videoEl.duration) ? videoEl.duration : 0;
    // Capture the intrinsic width so zoom / actual-size can reference it.
    videoNativeWidth = videoEl.videoWidth > 0 ? videoEl.videoWidth : null;
    // For clip rows, jump to the in-point so the viewer starts at the
    // highlighted range rather than the top of the source file.
    if (video?.isClip && video.clipStartSeconds !== null && video.clipStartSeconds !== undefined) {
      try { videoEl.currentTime = video.clipStartSeconds; } catch { /* not seekable yet */ }
    }
  }

  // --- Size controls --------------------------------------------------------

  // Current effective rendered width in px — either an explicit override or
  // whatever the browser is currently laying out. Used as the base for +/-
  // so zooming from "fit" mode feels natural.
  function currentRenderedWidth(): number {
    if (videoWidthPx !== null) return videoWidthPx;
    return videoEl?.clientWidth ?? videoNativeWidth ?? 0;
  }

  function clampedWidth(px: number): number {
    if (!videoNativeWidth || videoNativeWidth <= 0) return px;
    const max = Math.round(videoNativeWidth * MAX_ZOOM);
    // Keep it at least 100px so a stray zoom-out doesn't vanish the video.
    return Math.max(100, Math.min(max, Math.round(px)));
  }

  function actualSize() {
    if (!videoNativeWidth) return;
    videoWidthPx = clampedWidth(videoNativeWidth);
  }

  function zoomIn() {
    const cur = currentRenderedWidth();
    if (cur <= 0) return;
    videoWidthPx = clampedWidth(cur * ZOOM_STEP);
  }

  function zoomOut() {
    const cur = currentRenderedWidth();
    if (cur <= 0) return;
    videoWidthPx = clampedWidth(cur / ZOOM_STEP);
  }

  function fitSize() {
    videoWidthPx = null;
  }

  // --- Block editing --------------------------------------------------------

  // Turn mark-mode on/off. Turning off drops any pending-but-unclosed block.
  function toggleMarkMode() {
    markMode = !markMode;
    if (!markMode) pendingBlockStart = null;
  }

  // [ key — capture the start of a new do-not-play block at the current time
  // and flip auto-skip off (mark mode) so the user can scrub freely to the
  // end point. Pressing [ again before ] just moves the start marker.
  function startBlock() {
    if (!video || !videoEl) return;
    markMode = true;
    pendingBlockStart = videoEl.currentTime;
  }

  // ] key — close the block at the current time. No-op if [ wasn't pressed
  // first. Normalizes regardless of which end is earlier, and drops
  // micro-blocks that almost certainly came from an accidental double-tap.
  // VideoBlock offset/length are int seconds server-side; floor/ceil so the
  // rounded block still fully covers the user's selection.
  function endBlock() {
    if (!video || !videoEl) return;
    if (pendingBlockStart === null) return;
    const t = videoEl.currentTime;
    const start = Math.min(pendingBlockStart, t);
    const end = Math.max(pendingBlockStart, t);
    const length = end - start;
    pendingBlockStart = null;
    if (length < 0.25) return;
    const offsetInt = Math.floor(start);
    const lengthInt = Math.max(1, Math.ceil(end) - offsetInt);
    video.videoBlocks = [
      ...video.videoBlocks,
      { offsetInSeconds: offsetInt, lengthInSeconds: lengthInt, videoBlockType: 'hide' }
    ];
  }

  function cancelPendingBlock() {
    pendingBlockStart = null;
  }

  // --- Clip authoring -------------------------------------------------------

  // C key — first press captures the clip's in-point, second press hits the
  // API to create the clip (tags inherited from parent). Refuses to run on a
  // clip video since clip-of-clip isn't allowed.
  async function markClipPoint() {
    if (!video || !videoEl) return;
    if (video.isClip) {
      clipError = 'Cannot create a clip of a clip.';
      return;
    }
    const t = videoEl.currentTime;
    if (pendingClipStart === null) {
      pendingClipStart = t;
      clipError = null;
      return;
    }
    const start = Math.min(pendingClipStart, t);
    const end = Math.max(pendingClipStart, t);
    pendingClipStart = null;
    if (end - start < 0.25) return;
    clipCreating = true;
    clipError = null;
    try {
      const clipId = await api.createClip(video.id, { startSeconds: start, endSeconds: end });
      const clip = await api.getVideo(clipId);
      if (!clip) throw new Error('Created clip not found.');
      try { videoEl?.pause(); } catch { /* nothing to do */ }
      previewingClip = clip;
      previewError = null;
      previewPaused = true;
      previewMuted = true;
      previewCurrent = clip.clipStartSeconds ?? 0;
    } catch (e) {
      clipError = e instanceof Error ? e.message : String(e);
    } finally {
      clipCreating = false;
    }
  }

  // --- Clip preview modal ---------------------------------------------------

  function closePreview() {
    previewingClip = null;
    previewError = null;
  }

  // "Keep" = close the modal; the clip is already persisted on the server.
  function keepPreviewClip() {
    if (previewDiscarding) return;
    closePreview();
  }

  async function discardPreviewClip() {
    if (!previewingClip || previewDiscarding) return;
    previewDiscarding = true;
    previewError = null;
    try {
      await api.deleteVideo(previewingClip.id);
      closePreview();
    } catch (e) {
      previewError = `Failed to discard: ${e instanceof Error ? e.message : String(e)}`;
    } finally {
      previewDiscarding = false;
    }
  }

  // Loop the preview. Metadata-loaded is our earliest safe seek point — we
  // seek first, then start playback, so the browser can't show a frame
  // outside the clip range before the seek lands.
  function onPreviewLoaded() {
    if (!previewVideoEl || !previewingClip) return;
    const start = previewingClip.clipStartSeconds ?? 0;
    try { previewVideoEl.currentTime = start; } catch { /* not seekable yet */ }
    previewVideoEl.play().catch(() => { /* autoplay may be blocked */ });
  }

  function onPreviewTimeUpdate() {
    if (!previewVideoEl || !previewingClip) return;
    const start = previewingClip.clipStartSeconds ?? 0;
    const end = previewingClip.clipEndSeconds ?? 0;
    previewCurrent = previewVideoEl.currentTime;
    if (end > start && previewVideoEl.currentTime >= end) {
      previewVideoEl.currentTime = start;
    }
  }

  // Clip-scoped progress: 0..1 within [clipStart, clipEnd].
  const previewProgress = $derived.by(() => {
    if (!previewingClip) return 0;
    const s = previewingClip.clipStartSeconds ?? 0;
    const e = previewingClip.clipEndSeconds ?? 0;
    if (e <= s) return 0;
    return Math.max(0, Math.min(1, (previewCurrent - s) / (e - s)));
  });

  const previewClipLength = $derived.by(() => {
    if (!previewingClip) return 0;
    const s = previewingClip.clipStartSeconds ?? 0;
    const e = previewingClip.clipEndSeconds ?? 0;
    return Math.max(0, e - s);
  });

  // Current position relative to the clip start (what the scrubber label shows).
  const previewRelativeTime = $derived.by(() => {
    if (!previewingClip) return 0;
    const s = previewingClip.clipStartSeconds ?? 0;
    return Math.max(0, previewCurrent - s);
  });

  function togglePreviewPlay() {
    if (!previewVideoEl) return;
    if (previewVideoEl.paused) previewVideoEl.play().catch(() => {});
    else previewVideoEl.pause();
  }

  function togglePreviewMute() {
    if (!previewVideoEl) return;
    previewMuted = !previewVideoEl.muted;
    previewVideoEl.muted = previewMuted;
  }

  // Map a click on the custom scrubber back to an absolute time inside the
  // parent file: 0% = clipStart, 100% = clipEnd. The ontimeupdate loop keeps
  // the out edge enforced.
  function onPreviewScrubClick(e: MouseEvent) {
    if (!previewVideoEl || !previewingClip) return;
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect();
    const rel = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
    const s = previewingClip.clipStartSeconds ?? 0;
    const eSec = previewingClip.clipEndSeconds ?? 0;
    previewVideoEl.currentTime = s + rel * Math.max(0, eSec - s);
  }

  function onPreviewPlay()  { previewPaused = false; }
  function onPreviewPause() { previewPaused = true; }

  function cancelPendingClip() {
    pendingClipStart = null;
  }

  function removeBlock(index: number) {
    if (!video) return;
    video.videoBlocks = video.videoBlocks.filter((_, i) => i !== index);
  }

  // Sorted copy for the "Blocks" list under the scrubber — user adds them in
  // the order they find them, but reviewing is easier in time order.
  type Block = Video['videoBlocks'][number];
  const sortedBlocks = $derived.by<Array<{ idx: number; block: Block }>>(() => {
    const v = video;
    if (!v) return [];
    return v.videoBlocks
      .map((block, idx) => ({ idx, block }))
      .sort((a, b) => a.block.offsetInSeconds - b.block.offsetInSeconds);
  });

  // --- Bookmarks ------------------------------------------------------------
  // Repurposes the existing ChapterMarker list on Video — semantically
  // identical (offset + label) and already persisted via the normal
  // save-on-navigate path, so no new storage.

  type Bookmark = Video['chapterMarkers'][number];

  function addBookmark() {
    if (!video || !videoEl) return;
    const t = videoEl.currentTime;
    video.chapterMarkers = [
      ...video.chapterMarkers,
      { offset: t, comment: formatClock(t) }
    ];
  }

  function jumpToBookmark(offset: number) {
    if (!videoEl) return;
    videoEl.currentTime = offset;
  }

  function removeBookmark(idx: number) {
    if (!video) return;
    video.chapterMarkers = video.chapterMarkers.filter((_, i) => i !== idx);
  }

  // Sorted view for display; user adds them as they find them, but browsing
  // is easier in time order. `idx` is the position in the original array so
  // delete + rename still target the right element.
  const sortedBookmarks = $derived.by<Array<{ idx: number; bookmark: Bookmark }>>(() => {
    const v = video;
    if (!v) return [];
    return v.chapterMarkers
      .map((bookmark, idx) => ({ idx, bookmark }))
      .sort((a, b) => a.bookmark.offset - b.bookmark.offset);
  });

  function togglePlayPause() {
    if (!videoEl) return;
    if (videoEl.paused) videoEl.play().catch(() => {});
    else videoEl.pause();
  }

  // True when any structural status flag is set.
  function hasAnyStatus(v: Video | null): boolean {
    if (!v) return false;
    return v.needsReview || v.wontPlay || v.markedForDeletion;
  }

  function formatClock(seconds: number): string {
    if (!Number.isFinite(seconds) || seconds < 0) return '0:00';
    const total = Math.floor(seconds);
    const h = Math.floor(total / 3600);
    const m = Math.floor((total % 3600) / 60);
    const s = total % 60;
    if (h > 0) return `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
    return `${m}:${String(s).padStart(2, '0')}`;
  }

  function scrubPreviewStyle(idx: number): string {
    const f = scrubFrames[idx];
    if (!f || scrubSpriteSize.w === 0 || !video) return '';
    const scale = SCRUB_PREVIEW_W / f.w;
    return [
      `background-image: url("${api.spriteUrl(video.id)}")`,
      `background-size: ${scrubSpriteSize.w * scale}px ${scrubSpriteSize.h * scale}px`,
      `background-position: -${f.x * scale}px -${f.y * scale}px`,
      `background-repeat: no-repeat`
    ].join('; ');
  }

  // --- Actions --------------------------------------------------------------

  // The seek shortcuts (Numpad, Shift+top-row digit, Numpad 0, Numpad −)
  // target whichever element is currently "active": the preview modal if
  // it's open, otherwise the main player. The range they clamp into mirrors
  // that context — main = [0, duration], preview = [clipStart, clipEnd].
  interface SeekCtx { el: HTMLVideoElement; min: number; max: number }

  function currentSeekContext(): SeekCtx | null {
    if (previewingClip && previewVideoEl) {
      const s = previewingClip.clipStartSeconds ?? 0;
      const e = previewingClip.clipEndSeconds ?? 0;
      return { el: previewVideoEl, min: s, max: e > s ? e : s };
    }
    if (videoEl && video) {
      const d = Number.isFinite(videoEl.duration) ? videoEl.duration : Number.POSITIVE_INFINITY;
      return { el: videoEl, min: 0, max: d };
    }
    return null;
  }

  function seekBy(delta: number) {
    const ctx = currentSeekContext();
    if (!ctx) return;
    const next = ctx.el.currentTime + delta;
    ctx.el.currentTime = Math.max(ctx.min, Math.min(next, ctx.max));
  }

  function seekToStart() {
    const ctx = currentSeekContext();
    if (!ctx) return;
    ctx.el.currentTime = ctx.min;
  }

  function seekToNearEnd() {
    const ctx = currentSeekContext();
    if (!ctx) return;
    // In the clip-preview modal we land 2s from the out-point (the whole clip
    // is usually short); on the main player we keep the familiar 10s-from-end.
    // Never earlier than the start of the active range.
    const offset = previewingClip ? 2 : 10;
    const target = Number.isFinite(ctx.max) ? ctx.max - offset : ctx.el.currentTime;
    ctx.el.currentTime = Math.max(ctx.min, target);
  }

  async function saveIfDirty(): Promise<boolean> {
    if (!video) return true;
    const active = document.activeElement;
    if (active instanceof HTMLElement && active !== document.body) active.blur();
    await tick();
    const currentJson = JSON.stringify(video);
    if (currentJson === loadedVideoSnapshot) return true;
    try {
      await api.updateVideo(video.id, {
        fileName: video.fileName,
        ingestDate: video.ingestDate,
        cameraType: video.cameraType,
        videoQuality: video.videoQuality,
        watchCount: video.watchCount,
        notes: video.notes,
        needsReview: video.needsReview,
        isFavorite: video.isFavorite,
        clipStartSeconds: video.clipStartSeconds,
        clipEndSeconds: video.clipEndSeconds,
        chapterMarkers: video.chapterMarkers,
        videoBlocks: video.videoBlocks,
        tagIds: video.tags.map(t => t.id)
      });
      loadedVideoSnapshot = currentJson;
      return true;
    } catch (e) {
      errorMessage = `Failed to save: ${e instanceof Error ? e.message : String(e)}`;
      return false;
    }
  }

  async function goNext() {
    isNavigating = true;
    try {
      if (await saveIfDirty()) await onRequestNext?.();
    } finally {
      isNavigating = false;
    }
  }

  async function goPrev() {
    isNavigating = true;
    try {
      if (await saveIfDirty()) await onRequestPrev?.();
    } finally {
      isNavigating = false;
    }
  }

  async function performMarkDelete() {
    if (!video) return;
    const markId = video.id;
    markActionBusy = 'delete';
    errorMessage = null;
    try {
      // Release the file handle by loading the next video first. The server
      // also retries a few times in case the tear-down hasn't flushed yet.
      if (videoEl) {
        videoEl.pause();
        videoEl.removeAttribute('src');
        videoEl.load();
      }
      await onRequestNext?.();
      await api.markForDeletion(markId);
    } catch (e) {
      errorMessage = e instanceof Error ? e.message : String(e);
    } finally {
      markActionBusy = null;
    }
  }

  async function performUnmarkDelete() {
    if (!video) return;
    const id = video.id;
    markActionBusy = 'delete';
    errorMessage = null;
    if (videoEl) {
      videoEl.pause();
      videoEl.removeAttribute('src');
      videoEl.load();
    }
    try {
      const updated = await api.unmarkForDeletion(id);
      video = updated;
      loadedVideoSnapshot = JSON.stringify(updated);
      await tick();
      if (videoEl) {
        videoEl.muted = true;
        videoEl.load();
        videoEl.play().catch(() => {});
      }
    } catch (e) {
      errorMessage = e instanceof Error ? e.message : String(e);
      if (videoEl) {
        videoEl.load();
        videoEl.play().catch(() => {});
      }
    } finally {
      markActionBusy = null;
    }
  }

  async function performUndo() {
    if (!video || markActionBusy !== null) return;
    if (!video.markedForDeletion) return;
    await performUnmarkDelete();
  }

  // Toggle the Won't Play flag on the current video and advance. Calls the
  // mark/unmark endpoints which also handle the file move into _WontPlay.
  async function toggleWontPlayAndAdvance() {
    if (!video) return;
    try {
      const updated = video.wontPlay
        ? await api.unmarkWontPlay(video.id)
        : await api.markWontPlay(video.id);
      video = updated;
      loadedVideoSnapshot = JSON.stringify(updated);
    } catch (e) {
      errorMessage = `Failed to toggle Won't Play: ${e instanceof Error ? e.message : String(e)}`;
      return;
    }
    await onRequestNext?.();
  }

  async function clearNeedsReview() {
    if (!video || !video.needsReview) return;
    try {
      await api.markReviewed(video.id);
      if (video) video.needsReview = false;
      loadedVideoSnapshot = JSON.stringify(video);
    } catch (e) {
      errorMessage = `Failed to clear Needs Review: ${e instanceof Error ? e.message : String(e)}`;
      return;
    }
    await goNext();
  }

  // Toggle the structural IsFavorite flag. No file-system side effect; just
  // a star flip. Stays on the current video so the user can keep watching.
  async function toggleFavorite() {
    if (!video) return;
    const next = !video.isFavorite;
    try {
      if (next) await api.markFavorite(video.id);
      else await api.unmarkFavorite(video.id);
      if (video) video.isFavorite = next;
      loadedVideoSnapshot = JSON.stringify(video);
    } catch (e) {
      errorMessage = `Failed to toggle Favorite: ${e instanceof Error ? e.message : String(e)}`;
    }
  }

  // --- Keyboard -------------------------------------------------------------

  function isTypingTarget(t: EventTarget | null): boolean {
    if (!(t instanceof HTMLElement)) return false;
    const tag = t.tagName;
    return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || t.isContentEditable;
  }

  async function onWindowKeyDown(e: KeyboardEvent) {
    if (!shortcutsEnabled || !video) return;
    // While the clip-preview modal is open, hand keys off to the modal:
    // Escape / Enter approve, Space toggles play/pause, and the Numpad /
    // Shift+digit seek keys below fall through — they retarget to the
    // preview element via currentSeekContext(). All other shortcuts are
    // swallowed so W/D/F etc. don't fire against the parent video behind
    // the modal.
    if (previewingClip) {
      if (e.key === 'Escape' || e.key === 'Enter') {
        e.preventDefault();
        e.stopPropagation();
        keepPreviewClip();
        return;
      }
      if (e.key === ' ') {
        e.preventDefault();
        e.stopPropagation();
        togglePreviewPlay();
        return;
      }
    }
    // ArrowLeft/Right save-if-dirty and navigate. Shift is required whenever
    // the user is typing in a tag input OR the Edit Tags panel is open — that
    // way an accidental ← while editing tags doesn't jump to another video.
    // Disabled outright while the clip-preview modal is open.
    const isTyping = isTypingTarget(e.target);
    const requireShift = isTyping || tagsPanelOpen;
    if (!previewingClip && e.key === 'ArrowRight') {
      if (requireShift && !e.shiftKey) return;
      e.preventDefault(); await goNext(); return;
    }
    if (!previewingClip && e.key === 'ArrowLeft') {
      if (requireShift && !e.shiftKey) return;
      e.preventDefault(); await goPrev(); return;
    }
    // Alt+1..9 toggles the Nth tag (by SortOrder) of the first
    // displayAsCheckboxes group. Top-row digits and numpad both work.
    if (e.altKey && !e.ctrlKey && !e.metaKey && !e.shiftKey) {
      let digit: number | null = null;
      if (e.code.startsWith('Digit')) {
        const d = parseInt(e.code.substring(5), 10);
        if (d >= 1 && d <= 9) digit = d;
      } else if (e.code.startsWith('Numpad')) {
        const d = parseInt(e.code.substring(6), 10);
        if (d >= 1 && d <= 9) digit = d;
      }
      if (digit !== null) {
        e.preventDefault();
        e.stopPropagation();
        await toggleFlagAt(digit);
        return;
      }
    }
    // Numpad keys are dedicated playback controls — they always seek even
    // while a tag input has focus, regardless of NumLock state. Top-row
    // digits type into the input (the typing-target check below handles
    // that path); use top row for numeric tag values like Year.
    //   Numpad 1/3/4/6/7/9  — relative seek (per playbackSettings)
    //   Numpad 0            — jump to start (00:00)
    //   Numpad -            — jump to 10s from the end
    if (e.code === 'Numpad0') {
      e.preventDefault();
      e.stopPropagation();
      seekToStart();
      return;
    }
    if (e.code === 'NumpadSubtract') {
      e.preventDefault();
      e.stopPropagation();
      seekToNearEnd();
      return;
    }
    const numpadSeek =
      e.code === 'Numpad1' ? -playbackSettings.key1Seconds :
      e.code === 'Numpad3' ? playbackSettings.key3Seconds :
      e.code === 'Numpad4' ? -playbackSettings.key4Seconds :
      e.code === 'Numpad6' ? playbackSettings.key6Seconds :
      e.code === 'Numpad7' ? -playbackSettings.key7Seconds :
      e.code === 'Numpad9' ? playbackSettings.key9Seconds :
      null;
    if (numpadSeek !== null) {
      e.preventDefault();
      e.stopPropagation();
      seekBy(numpadSeek);
      return;
    }
    // Shift+top-row digit is the keyboard-based equivalent for users without
    // a numpad. Match on e.code and the shifted character.
    if (e.shiftKey && !e.ctrlKey && !e.metaKey && !e.altKey) {
      const shiftSeek =
        e.code === 'Digit1' || e.key === '!' ? -playbackSettings.key1Seconds :
        e.code === 'Digit3' || e.key === '#' ? playbackSettings.key3Seconds :
        e.code === 'Digit4' || e.key === '$' ? -playbackSettings.key4Seconds :
        e.code === 'Digit6' || e.key === '^' ? playbackSettings.key6Seconds :
        e.code === 'Digit7' || e.key === '&' ? -playbackSettings.key7Seconds :
        e.code === 'Digit9' || e.key === '(' ? playbackSettings.key9Seconds :
        null;
      if (shiftSeek !== null) {
        e.preventDefault();
        e.stopPropagation();
        seekBy(shiftSeek);
        return;
      }
    }
    // All remaining shortcuts (tag marks, zoom, etc.) are for the main
    // player only; swallow everything else while the preview modal is open.
    if (previewingClip) return;
    // All other shortcuts pause on typing targets.
    if (isTypingTarget(e.target)) return;

    if (e.key === ' ') {
      e.preventDefault();
      togglePlayPause();
      return;
    }
    if (e.key === 'w' || e.key === 'W') {
      e.preventDefault();
      await toggleWontPlayAndAdvance();
      return;
    }
    if (e.key === 'd' || e.key === 'D') {
      e.preventDefault();
      if (markActionBusy === null && !video.markedForDeletion) await performMarkDelete();
      return;
    }
    if (e.key === 'u' || e.key === 'U') { e.preventDefault(); await performUndo(); return; }
    if (e.key === 'r' || e.key === 'R') { e.preventDefault(); await clearNeedsReview(); return; }
    // T — toggle Edit Tags panel. Host owns the panel state.
    if (e.key === 't' || e.key === 'T') { e.preventDefault(); onToggleTags?.(); return; }
    // F — toggle the structural IsFavorite flag (★).
    if (e.key === 'f' || e.key === 'F') { e.preventDefault(); await toggleFavorite(); return; }
    // K — drop a bookmark at the current time. Default label is the
    // timestamp; user can rename it inline under the video.
    if (e.key === 'k' || e.key === 'K') { e.preventDefault(); addBookmark(); return; }
    // C — clip start/end. First press captures the in-point, second press
    // creates a new clip Video row on the server covering the range.
    if (e.key === 'c' || e.key === 'C') { e.preventDefault(); await markClipPoint(); return; }
    // Block editing: [ starts a do-not-play block at the current time,
    // ] closes it. M toggles mark mode so existing blocks can be scrubbed
    // through for review.
    if (e.key === '[') { e.preventDefault(); startBlock(); return; }
    if (e.key === ']') { e.preventDefault(); endBlock(); return; }
    if (e.key === 'm' || e.key === 'M') { e.preventDefault(); toggleMarkMode(); return; }
    // \ snaps the player back to fit-to-column size.
    if (e.key === '\\') { e.preventDefault(); fitSize(); return; }
    switch (e.key) {
      case '1': e.preventDefault(); seekBy(-playbackSettings.key1Seconds); break;
      case '3': e.preventDefault(); seekBy(playbackSettings.key3Seconds); break;
      case '4': e.preventDefault(); seekBy(-playbackSettings.key4Seconds); break;
      case '6': e.preventDefault(); seekBy(playbackSettings.key6Seconds); break;
      case '7': e.preventDefault(); seekBy(-playbackSettings.key7Seconds); break;
      case '9': e.preventDefault(); seekBy(playbackSettings.key9Seconds); break;
    }
  }
</script>

<!-- Capture phase so we beat input-level onkeydown handlers to the event.
     Without this, numpad digits had already been consumed / character
     insertion was in-flight by the time our bubble-phase listener ran. -->
<svelte:window onkeydowncapture={onWindowKeyDown} />

{#if video}
  {#if video.isClip && video.clipStartSeconds !== null && video.clipEndSeconds !== null}
    <!-- Banner that surfaces the clip's context: range within the source and
         a link back to the parent for further editing. -->
    <div class="mb-2 px-3 py-1.5 rounded bg-success/15 border border-success/30 text-sm flex items-center gap-2">
      <span class="badge badge-tag-other">Clip</span>
      <span class="tabular-nums font-mono">
        {formatClock(video.clipStartSeconds)} – {formatClock(video.clipEndSeconds)}
      </span>
      {#if video.parentVideoId}
        <span class="text-base-content/60">of</span>
        <a class="link link-hover" href="/browse?id={video.parentVideoId}">Source</a>
      {/if}
      <span class="ml-auto text-base-content/60">Loops automatically</span>
    </div>
  {/if}
  {#if isMarkedDelete}
    <div class="alert alert-error gap-3 mb-2">
      <span class="font-semibold uppercase tracking-wide text-sm">Marked to Delete</span>
      <span class="text-sm opacity-80">Press <kbd class="kbd kbd-sm">U</kbd> to undo.</span>
      <button
        type="button"
        class="btn btn-sm ml-auto"
        disabled={markActionBusy !== null}
        onclick={performUndo}
      >
        {#if markActionBusy !== null}<span class="loading loading-spinner loading-xs"></span>{/if}
        Undo
      </button>
    </div>
  {/if}

  <div
    class="transition-opacity duration-100 {isNavigating ? 'opacity-0 pointer-events-none' : 'opacity-100'}"
    class:grayscale={isMarkedDelete}
    class:opacity-60={isMarkedDelete && !isNavigating}
  >
    <!-- Fit mode (videoWidthPx === null): w-full + 70vh cap, with max-width
         clamped to 2x native so a low-res file doesn't balloon. Explicit mode
         (videoWidthPx number): drop the fit classes and set width in px.
         Scrubber is absolutely positioned at the bottom of this wrapper and
         fades in on hover so it doesn't steal vertical space while watching. -->
    <!-- svelte-ignore a11y_click_events_have_key_events a11y_no_noninteractive_element_interactions -->
    <div
      role="presentation"
      class="relative group/player inline-block align-top {videoWidthPx === null ? 'w-full' : ''}"
      onmouseenter={() => (playerHovered = true)}
      onmouseleave={() => (playerHovered = false)}
    >
    <video
      bind:this={videoEl}
      autoplay
      muted
      loop
      class="bg-black rounded cursor-pointer block {videoWidthPx === null ? 'w-full' : ''}"
      style={[
        videoWidthPx !== null
          ? `width: ${videoWidthPx}px;`
          : videoNativeWidth !== null ? `max-width: ${videoNativeWidth * MAX_ZOOM}px;` : '',
        `max-height: ${maxVideoHeightPx !== null ? maxVideoHeightPx + 'px' : '70vh'};`
      ].join(' ')}
      ontimeupdate={onVideoTimeUpdate}
      onloadedmetadata={onVideoLoadedMetadata}
      onclick={togglePlayPause}
    >
      <source src={videoUrl} type="video/mp4" />
      <track kind="captions" />
      Your browser does not support the video tag.
    </video>

    <!-- Scrubber overlay: fades in on hover, stays visible while the user is
         actively scrubbing (scrubHoverX !== null) so the mouse doesn't have
         to stay perfectly inside the bar. -->
    <div
      class="absolute inset-x-2 bottom-2 pointer-events-none transition-opacity duration-150
             {playerHovered || scrubHoverX !== null ? 'opacity-100' : 'opacity-0'}"
    >
    <div class="relative pointer-events-auto">
      <!-- svelte-ignore a11y_click_events_have_key_events -->
      <div
        bind:this={scrubBarEl}
        class="relative h-6 bg-base-300/70 rounded cursor-pointer overflow-hidden backdrop-blur-sm"
        onmousemove={onScrubMove}
        onmouseleave={onScrubLeave}
        onclick={onScrubClick}
        role="slider"
        tabindex="-1"
        aria-label="Scrubber"
        aria-valuemin="0"
        aria-valuemax="100"
        aria-valuenow={Math.round(videoProgress * 100)}
      >
        <div class="absolute inset-y-0 left-0 bg-primary/60" style="width: {videoProgress * 100}%"></div>
        <!-- DoNotShow blocks overlay the scrubber as red bands. pointer-events
             stay off so scrubber clicks still seek; deletion lives in the
             list below when mark mode is on. -->
        {#if videoDuration > 0 && video?.videoBlocks?.length}
          {#each video.videoBlocks as b, i (i)}
            {#if b.videoBlockType === 'hide'}
              <div
                class="absolute inset-y-0 bg-error/60 pointer-events-none"
                style="left: {(b.offsetInSeconds / videoDuration) * 100}%; width: {(b.lengthInSeconds / videoDuration) * 100}%"
              ></div>
            {/if}
          {/each}
        {/if}
        <!-- Pending block start: the first B-press landmark while waiting for
             the second B-press to close the region. -->
        {#if pendingBlockStart !== null && videoDuration > 0}
          <div
            class="absolute inset-y-0 w-0.5 bg-warning pointer-events-none"
            style="left: {(pendingBlockStart / videoDuration) * 100}%"
          ></div>
        {/if}
        <!-- When watching a clip, paint its in/out range as a soft overlay so
             the viewer sees "the highlighted bit" against the full source. -->
        {#if video?.isClip
             && video.clipStartSeconds !== null && video.clipStartSeconds !== undefined
             && video.clipEndSeconds !== null && video.clipEndSeconds !== undefined
             && videoDuration > 0}
          <div
            class="absolute inset-y-0 bg-success/30 pointer-events-none"
            style="left: {(video.clipStartSeconds / videoDuration) * 100}%; width: {((video.clipEndSeconds - video.clipStartSeconds) / videoDuration) * 100}%"
          ></div>
        {/if}
        <!-- Pending clip start while the user is defining a new clip. -->
        {#if pendingClipStart !== null && videoDuration > 0}
          <div
            class="absolute inset-y-0 w-0.5 bg-success pointer-events-none"
            style="left: {(pendingClipStart / videoDuration) * 100}%"
          ></div>
        {/if}
        <!-- Bookmark markers — thin blue pins along the scrubber. Pointer
             events off so scrubber clicks still seek normally; jumping is
             handled by the bookmark list below. -->
        {#if videoDuration > 0 && video?.chapterMarkers?.length}
          {#each video.chapterMarkers as bm, i (i)}
            <div
              class="absolute inset-y-0 w-0.5 bg-info pointer-events-none"
              style="left: {(bm.offset / videoDuration) * 100}%"
              title={bm.comment}
            ></div>
          {/each}
        {/if}
        {#if scrubHoverX !== null}
          <div class="absolute inset-y-0 w-px bg-primary-content/80 pointer-events-none" style="left: {scrubHoverX}px"></div>
        {/if}
      </div>

      {#if scrubHoverX !== null && scrubHoverIdx !== null && scrubFrames.length > 0}
        <div
          class="absolute pointer-events-none shadow-lg border border-base-300 rounded overflow-hidden bg-base-300"
          style="bottom: calc(100% + 6px); left: {scrubHoverX}px; transform: translateX(-50%); width: {SCRUB_PREVIEW_W}px; aspect-ratio: 16 / 9;"
        >
          <div class="absolute inset-0" style={scrubPreviewStyle(scrubHoverIdx)}></div>
          {#if scrubHoverTime !== null}
            <div class="absolute bottom-0 right-0 bg-black/75 text-white text-xs px-1.5 py-0.5 font-mono tabular-nums rounded-tl">
              {formatClock(scrubHoverTime)}
            </div>
          {/if}
        </div>
      {:else if scrubHoverX !== null && scrubHoverTime !== null}
        <div
          class="absolute pointer-events-none bg-black/75 text-white text-xs px-1.5 py-0.5 rounded font-mono tabular-nums"
          style="bottom: calc(100% + 6px); left: {scrubHoverX}px; transform: translateX(-50%);"
        >
          {formatClock(scrubHoverTime)}
        </div>
      {/if}
    </div>
    </div>
    </div>

    <div class="mt-1 text-xs text-base-content/70 tabular-nums font-mono flex items-center gap-2">
      <span>{formatClock(videoCurrentTime)} / {formatClock(videoDuration)}</span>
      {#if pendingClipStart !== null}
        <span class="badge badge-success badge-sm uppercase tracking-wide">Clip</span>
        <span class="text-success">
          In @ {formatClock(pendingClipStart)} — press <kbd class="kbd kbd-xs">C</kbd> to close
        </span>
        <button type="button" class="btn btn-ghost btn-xs" onclick={cancelPendingClip}>Cancel</button>
      {:else if clipCreating}
        <span class="text-base-content/60">
          <span class="loading loading-spinner loading-xs"></span> Saving clip…
        </span>
      {/if}
      {#if clipError}
        <span class="text-error">{clipError}</span>
      {/if}
      {#if markMode}
        <span class="badge badge-warning badge-sm uppercase tracking-wide">Mark Mode</span>
        {#if pendingBlockStart !== null}
          <span class="text-warning">
            Block start @ {formatClock(pendingBlockStart)} — press <kbd class="kbd kbd-xs">]</kbd> to close
          </span>
          <button type="button" class="btn btn-ghost btn-xs" onclick={cancelPendingBlock}>Cancel</button>
        {:else}
          <span class="text-base-content/60">
            Press <kbd class="kbd kbd-xs">[</kbd> to start a block, <kbd class="kbd kbd-xs">M</kbd> to exit
          </span>
        {/if}
      {/if}
      <!-- Right-hand cluster: zoom on the left, Download icon flush to the
           far right. Kept as one flex group so the Download stays pinned
           right regardless of whether the zoom controls are rendered. -->
      <div class="ml-auto flex items-center gap-4">
        {#if sizePercent !== null}
          <!-- Size controls: −/+ zoom, % indicator resets to fit-to-column.
               Dimmed until the user actually zooms so the cluster recedes. -->
          <div class="flex items-center {videoWidthPx === null ? 'opacity-60' : ''}">
            <button
              type="button"
              class="btn btn-ghost btn-sm text-base"
              onclick={zoomOut}
              title="Smaller"
              aria-label="Smaller"
            >−</button>
            <button
              type="button"
              class="btn btn-ghost btn-sm text-base tabular-nums"
              onclick={fitSize}
              title={videoWidthPx === null ? 'Already fit to column' : 'Reset to fit-to-column'}
            >{sizePercent}%</button>
            <button
              type="button"
              class="btn btn-ghost btn-sm text-base"
              onclick={zoomIn}
              title="Larger"
              aria-label="Larger"
            >+</button>
          </div>
        {/if}
        <a
          class="btn btn-ghost btn-sm"
          href={api.streamUrl(video.id)}
          download={video.fileName}
          title="Download this video file"
          aria-label="Download"
        >
          <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" class="h-6 w-6 fill-current">
            <path d="M19 9h-4V3H9v6H5l7 7 7-7zM5 18v2h14v-2H5z" />
          </svg>
        </a>
      </div>
    </div>

    {#if markMode && video && sortedBlocks.length > 0}
      <div class="mt-2 border border-base-300 rounded p-2 space-y-1 text-xs">
        <div class="text-base-content/70 uppercase tracking-wide">Blocks (always skipped on playback)</div>
        {#each sortedBlocks as row (row.idx)}
          <div class="flex items-center gap-2 tabular-nums font-mono">
            <button
              type="button"
              class="link link-hover"
              title="Jump to block start"
              onclick={() => { if (videoEl) videoEl.currentTime = row.block.offsetInSeconds; }}
            >{formatClock(row.block.offsetInSeconds)}</button>
            <span class="text-base-content/50">→</span>
            <span>{formatClock(row.block.offsetInSeconds + row.block.lengthInSeconds)}</span>
            <span class="text-base-content/50">({formatClock(row.block.lengthInSeconds)})</span>
            <button
              type="button"
              class="btn btn-ghost btn-xs ml-auto"
              onclick={() => removeBlock(row.idx)}
              aria-label="Delete block"
            >×</button>
          </div>
        {/each}
      </div>
    {/if}

    {#if video && sortedBookmarks.length > 0}
      <div class="mt-2 border border-base-300 rounded p-2 space-y-1 text-xs">
        <div class="flex items-center text-base-content/70 uppercase tracking-wide">
          <span>Bookmarks</span>
          <span class="ml-2 text-[10px] normal-case tracking-normal text-base-content/50">
            Press <kbd class="kbd kbd-xs">K</kbd> to add
          </span>
        </div>
        {#each sortedBookmarks as row (row.idx)}
          <div class="flex items-center gap-2">
            <button
              type="button"
              class="link link-hover tabular-nums font-mono shrink-0"
              title="Jump to {formatClock(row.bookmark.offset)}"
              onclick={() => jumpToBookmark(row.bookmark.offset)}
            >{formatClock(row.bookmark.offset)}</button>
            <input
              type="text"
              class="input input-xs input-ghost flex-1 min-w-0"
              bind:value={video.chapterMarkers[row.idx].comment}
              placeholder={formatClock(row.bookmark.offset)}
            />
            <button
              type="button"
              class="btn btn-ghost btn-xs shrink-0"
              onclick={() => removeBookmark(row.idx)}
              aria-label="Delete bookmark"
            >×</button>
          </div>
        {/each}
      </div>
    {/if}

    <!-- Title row: filename + ★ Favorite toggle (F shortcut also flips it).
         Inline SVG (not a Unicode glyph) so we control fill vs hairline
         outline and the size scales cleanly. -->
    <div class="mt-1 text-xs text-base-content/80 flex items-center gap-1">
      <button
        type="button"
        class="leading-none cursor-pointer flex items-center"
        onclick={toggleFavorite}
        title={video.isFavorite ? 'Unfavorite (F)' : 'Favorite (F)'}
        aria-label={video.isFavorite ? 'Unfavorite' : 'Favorite'}
      >
        <svg viewBox="0 0 24 24" class="h-5 w-5"
          fill={video.isFavorite ? 'rgb(234 179 8)' : 'none'}
          stroke="rgb(255 255 255 / 0.85)" stroke-width="1.25" stroke-linejoin="round">
          <path d="M12 2.5 L14.6 8.9 L21.5 9.5 L16.2 14.1 L17.8 20.9 L12 17.3 L6.2 20.9 L7.8 14.1 L2.5 9.5 L9.4 8.9 Z" />
        </svg>
      </button>
      <span class="text-base-content/60">File:</span>
      <span class="text-base-content/70 break-all">{video.fileName}</span>
    </div>

    <!-- Tags grouped by tagGroupName + structural status pills. -->
    {#if video.tags.length > 0 || hasAnyStatus(video)}
      <div class="flex flex-wrap gap-0.5 mt-0.5">
        {#each video.tags as t (t.id)}
          <span class="badge {pillClass(t.id, t.tagGroupName)} gap-1">
            <button
              type="button"
              class="cursor-pointer"
              onclick={() => filterStore.requestAdd({
                type: 'tag',
                value: t.id,
                label: t.name,
                tagGroupName: t.tagGroupName
              })}
              title="Filter by {t.tagGroupName}: {t.name}"
            >{t.name}</button>
            <button
              type="button"
              class="opacity-70 hover:opacity-100"
              onclick={(e) => { e.stopPropagation(); openEditTagModal(t.id); }}
              title="Edit tag"
              aria-label="Edit {t.name}"
            >✎</button>
          </span>
        {/each}
        {#if video.needsReview}
          <span class="inline-block px-2 py-0.5 rounded-full text-xs border" style="background-color: rgb(168 162 158 / 0.20); border-color: rgb(168 162 158 / 0.45); color: rgb(214 211 209);">Needs Review</span>
        {/if}
        {#if video.wontPlay}
          <span class="inline-block px-2 py-0.5 rounded-full text-xs border" style="background-color: rgb(249 115 22 / 0.20); border-color: rgb(249 115 22 / 0.45); color: rgb(253 186 116);">Won't Play</span>
        {/if}
        {#if video.markedForDeletion}
          <span class="inline-block px-2 py-0.5 rounded-full text-xs border" style="background-color: rgb(239 68 68 / 0.20); border-color: rgb(239 68 68 / 0.45); color: rgb(252 165 165);">To Delete</span>
        {/if}
      </div>
    {/if}
  </div>

  <TagEditModal
    bind:show={editTagModalShow}
    tag={editingTag}
    onSaved={onTagSavedFromPlayer}
  />

  {#if errorMessage}
    <div class="alert alert-error text-sm mt-2 flex items-start gap-2">
      <span class="flex-1">{errorMessage}</span>
      <button class="btn btn-xs btn-ghost" onclick={() => (errorMessage = null)}>Dismiss</button>
    </div>
  {/if}
{/if}

{#if previewingClip}
  <!-- Post-create preview: auto-plays the new clip in a loop so the user can
       decide whether to Keep it (close) or Discard it (DELETE the row).
       Escape / Enter / backdrop click all fall through to Keep — the clip is
       already saved, so "do nothing" is the safe default. -->
  <!-- svelte-ignore a11y_click_events_have_key_events a11y_no_static_element_interactions -->
  <div class="modal modal-open" role="dialog" aria-modal="true" aria-labelledby="clip-preview-title">
    <div class="modal-box max-w-4xl w-full">
      <h3 id="clip-preview-title" class="font-semibold text-lg mb-2 flex items-center gap-2">
        <span class="badge badge-tag-other">Clip</span>
        Preview new clip
      </h3>
      <!-- Custom controls: native <video controls> renders the full parent
           timeline (this clip row just references the parent's file with a
           time range), so we render our own scrubber scoped to
           [clipStart, clipEnd]. Clicking the video toggles play/pause. -->
      <!-- svelte-ignore a11y_click_events_have_key_events a11y_no_noninteractive_element_interactions a11y_media_has_caption -->
      <video
        bind:this={previewVideoEl}
        src={api.streamUrl(previewingClip.id)}
        preload="metadata"
        muted
        class="w-full bg-black rounded cursor-pointer"
        onloadedmetadata={onPreviewLoaded}
        ontimeupdate={onPreviewTimeUpdate}
        onclick={togglePreviewPlay}
        onplay={onPreviewPlay}
        onpause={onPreviewPause}
      ></video>

      <!-- Scrubber constrained to the clip range. 0 % is clipStart, 100 % is
           clipEnd; clicking anywhere seeks within that range. -->
      <!-- svelte-ignore a11y_click_events_have_key_events a11y_no_static_element_interactions -->
      <div
        class="relative h-2 bg-base-300 rounded mt-2 cursor-pointer overflow-hidden"
        onclick={onPreviewScrubClick}
        role="slider"
        aria-label="Clip scrubber"
        tabindex="-1"
        aria-valuemin="0"
        aria-valuemax="100"
        aria-valuenow={Math.round(previewProgress * 100)}
      >
        <div class="absolute inset-y-0 left-0 bg-primary/70" style="width: {previewProgress * 100}%"></div>
      </div>

      <div class="mt-2 flex items-center gap-2 text-xs">
        <button
          type="button"
          class="btn btn-ghost btn-xs px-2"
          onclick={togglePreviewPlay}
          aria-label={previewPaused ? 'Play' : 'Pause'}
          title={previewPaused ? 'Play' : 'Pause'}
        >
          {#if previewPaused}▶{:else}❚❚{/if}
        </button>
        <span class="tabular-nums font-mono text-base-content/70">
          {formatClock(previewRelativeTime)} / {formatClock(previewClipLength)}
        </span>
        <span class="text-base-content/50">(loops)</span>
        <button
          type="button"
          class="btn btn-ghost btn-xs px-2 ml-auto"
          onclick={togglePreviewMute}
          aria-label={previewMuted ? 'Unmute' : 'Mute'}
          title={previewMuted ? 'Unmute' : 'Mute'}
        >
          {#if previewMuted}🔇{:else}🔊{/if}
        </button>
        <span class="text-base-content/50 break-all font-sans">{previewingClip.fileName}</span>
      </div>

      {#if previewError}
        <div class="alert alert-error text-sm mt-2">{previewError}</div>
      {/if}
      <div class="modal-action mt-4">
        <button
          type="button"
          class="btn btn-error"
          onclick={discardPreviewClip}
          disabled={previewDiscarding}
        >
          {#if previewDiscarding}<span class="loading loading-spinner loading-xs"></span>{/if}
          Reject
        </button>
        <button
          type="button"
          class="btn btn-primary"
          onclick={keepPreviewClip}
          disabled={previewDiscarding}
        >Approve</button>
      </div>
    </div>
    <!-- Backdrop is intentionally non-interactive — use the Keep / Discard
         buttons or Escape / Enter to close the modal. Avoids an a11y warning
         about bare click handlers on a static element. -->
    <div class="modal-backdrop"></div>
  </div>
{/if}

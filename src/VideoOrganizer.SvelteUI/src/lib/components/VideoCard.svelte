<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import type { Tag, Video } from '$lib/types';
  import { enqueueWarm } from '$lib/warmingQueue';
  import { filterStore } from '$lib/filterStore.svelte';
  import { pillClass } from '$lib/tagColors';
  import TagEditModal from './TagEditModal.svelte';

  let editTagModalShow = $state(false);
  let editingTag = $state<Tag | null>(null);
  async function openEditTag(tagId: string, e: Event) {
    e.stopPropagation();
    try {
      editingTag = await api.getTag(tagId);
      editTagModalShow = true;
    } catch { /* swallow — non-fatal */ }
  }

  interface Frame { x: number; y: number; w: number; h: number; }

  interface Props {
    video: Video;
    onopen?: (video: Video) => void;
    active?: boolean;
  }
  let { video, onopen, active = false }: Props = $props();

  let cardEl: HTMLDivElement | null = $state(null);
  let imgWrapEl: HTMLDivElement | null = $state(null);
  let imgWidth = $state(0);
  let hovering = $state(false);
  let frames = $state<Frame[]>([]);
  let spriteSize = $state<{ w: number; h: number }>({ w: 0, h: 0 });
  let currentFrame = $state<Frame | null>(null);
  let warmState = $state<'pending' | 'warming' | 'ready' | 'failed'>('pending');
  let vttRequested = false;

  const posterUrl = $derived(api.posterUrl(video.id));
  const spriteUrl = $derived(api.spriteUrl(video.id));
  const displayTitle = $derived(video.fileName);

  // ".NET TimeSpan: [d.]hh:mm:ss[.fffffff]" -> "M:SS" or "H:MM:SS". Returns
  // an empty string if the value isn't parseable so the overlay just hides.
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

  const durationLabel = $derived(formatDuration(video.duration));
  const resolutionLabel = $derived(
    video.width > 0 && video.height > 0 ? `${video.width}\u00d7${video.height}` : ''
  );

  function parseVtt(text: string): { frames: Frame[]; w: number; h: number } {
    const out: Frame[] = [];
    let maxRight = 0;
    let maxBottom = 0;
    const re = /sprite\.jpg#xywh=(\d+),(\d+),(\d+),(\d+)/g;
    let m: RegExpExecArray | null;
    while ((m = re.exec(text)) !== null) {
      const x = +m[1], y = +m[2], w = +m[3], h = +m[4];
      out.push({ x, y, w, h });
      if (x + w > maxRight) maxRight = x + w;
      if (y + h > maxBottom) maxBottom = y + h;
    }
    return { frames: out, w: maxRight, h: maxBottom };
  }

  async function doFetchVtt(): Promise<void> {
    warmState = 'warming';
    try {
      const res = await fetch(api.thumbnailsVttUrl(video.id));
      if (!res.ok) {
        warmState = 'failed';
        return;
      }
      const text = await res.text();
      const parsed = parseVtt(text);
      if (parsed.frames.length === 0) {
        warmState = 'failed';
        return;
      }
      frames = parsed.frames;
      spriteSize = { w: parsed.w, h: parsed.h };
      warmState = 'ready';
    } catch {
      warmState = 'failed';
    }
  }

  async function ensureVtt() {
    if (vttRequested) return;
    vttRequested = true;
    await enqueueWarm(doFetchVtt);
  }

  onMount(() => {
    // Kick off pre-warming in the background. The queue caps concurrency
    // so we don't stampede the API with one request per card on mount.
    void ensureVtt();
  });

  // When the currently-playing video changes (Shift+←/→ nav, auto-
  // advance, programmatic open), scroll the new active card into the
  // viewport so the user can see the highlight ring. `nearest` block
  // alignment + `smooth` behavior keeps small adjustments tidy and
  // doesn't yank the page on hover-to-play within an already-visible
  // row.
  $effect(() => {
    if (!active || !cardEl) return;
    cardEl.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'nearest' });
  });

  async function onEnter() {
    hovering = true;
    // Pre-warm has usually fired already; this is a no-op unless the card
    // was never seen before or the queue is still behind it.
    await ensureVtt();
  }

  function onMove(e: MouseEvent) {
    if (!hovering || frames.length === 0 || !cardEl) return;
    const rect = cardEl.getBoundingClientRect();
    const rel = (e.clientX - rect.left) / rect.width;
    const idx = Math.max(0, Math.min(frames.length - 1, Math.floor(rel * frames.length)));
    currentFrame = frames[idx];
  }

  function onLeave() {
    hovering = false;
    currentFrame = null;
  }

  function onClick() {
    onopen?.(video);
  }

  // Scale the sprite from whatever native frame width was generated (varies by
  // backend settings) to the card's rendered width — no hard-coded frame size.
  const frameW = $derived(frames.length > 0 ? frames[0].w : 0);
  const scale = $derived(imgWidth > 0 && frameW > 0 ? imgWidth / frameW : 0);

  const scrubStyle = $derived(() => {
    if (!currentFrame || spriteSize.w === 0 || scale === 0) return '';
    return [
      `background-image: url("${spriteUrl}")`,
      `background-size: ${spriteSize.w * scale}px ${spriteSize.h * scale}px`,
      `background-position: ${-currentFrame.x * scale}px ${-currentFrame.y * scale}px`,
      'background-repeat: no-repeat'
    ].join('; ');
  });
</script>

<div
  bind:this={cardEl}
  class="group text-left overflow-hidden rounded bg-base-200 transition w-full
         {active
            ? 'ring-2 ring-primary ring-offset-2 ring-offset-base-100 shadow-lg shadow-primary/30'
            : 'hover:ring-2 hover:ring-primary'}"
  onmouseenter={onEnter}
  onmousemove={onMove}
  onmouseleave={onLeave}
  role="article"
>
  <!-- Clickable thumbnail area -->
  <button
    type="button"
    class="block w-full text-left"
    onclick={onClick}
    aria-label="Play {displayTitle}"
  >
    <div
      bind:this={imgWrapEl}
      bind:clientWidth={imgWidth}
      class="relative bg-base-300 aspect-video w-full"
    >
      <img
        src={posterUrl}
        loading="lazy"
        alt=""
        class="absolute inset-0 w-full h-full object-cover transition-opacity"
        class:opacity-0={hovering && currentFrame !== null}
        onerror={(e) => ((e.currentTarget as HTMLImageElement).style.visibility = 'hidden')}
      />
      {#if hovering && currentFrame !== null}
        <div class="absolute inset-0" style={scrubStyle()}></div>
      {/if}
      {#if warmState === 'warming' || warmState === 'pending'}
        <div
          class="absolute top-1.5 left-1.5 flex items-center gap-1 px-2 py-0.5 rounded bg-black/70 text-warning text-[10px] font-medium uppercase tracking-wide"
          title="Preparing scrub thumbnails..."
        >
          <span class="w-1.5 h-1.5 rounded-full bg-warning animate-pulse"></span>
          preparing
        </div>
      {:else if warmState === 'failed'}
        <div
          class="absolute top-1.5 left-1.5 px-2 py-0.5 rounded bg-black/70 text-base-content/70 text-[10px] font-medium uppercase tracking-wide"
          title="No scrub preview available"
        >
          no preview
        </div>
      {/if}
      {#if video.thumbnailsFailed || video.md5Failed}
        <!-- Stack of system-failure overlays: thumbnail generation failed
             and/or MD5 hashing failed for this video. Both are persistent
             flags set by the background workers when they hit a hang/error
             on this row. -->
        <div class="absolute bottom-1.5 left-1.5 flex flex-col items-start gap-0.5">
          {#if video.thumbnailsFailed}
            <span
              class="px-2 py-0.5 rounded bg-error/85 text-error-content text-[10px] font-medium uppercase tracking-wide"
              title="Thumbnail generation failed or was skipped"
            >Thumbnail failed</span>
          {/if}
          {#if video.md5Failed}
            <span
              class="px-2 py-0.5 rounded bg-error/85 text-error-content text-[10px] font-medium uppercase tracking-wide"
              title="MD5 hashing failed or was skipped"
            >MD5 failed</span>
          {/if}
        </div>
      {/if}
      {#if video.isClip || video.isFavorite || video.needsReview}
        <!-- Top-right indicator stack: scissor for clip rows, eye for
             needs-review, ★ for favorites. All three can apply to the
             same video, so render in a flex row. Eye sits between the
             clip and favorite icons so it falls in a stable position
             regardless of which neighbors are present. -->
        <div class="absolute top-1 right-1 flex items-start gap-1 pointer-events-none drop-shadow-[0_1px_2px_rgba(0,0,0,0.8)]">
          {#if video.isClip}
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 24 24"
              class="h-3.5 w-3.5 text-yellow-400 fill-current"
              aria-label="Clip"
            >
              <title>Clip</title>
              <path d="M9.64 7.64c.23-.5.36-1.05.36-1.64 0-2.21-1.79-4-4-4S2 3.79 2 6s1.79 4 4 4c.59 0 1.14-.13 1.64-.36L10 12l-2.36 2.36C7.14 14.13 6.59 14 6 14c-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4c0-.59-.13-1.14-.36-1.64L12 14l7 7h3v-1L9.64 7.64zM6 8c-1.1 0-2-.89-2-2s.9-2 2-2 2 .89 2 2-.9 2-2 2zm0 12c-1.1 0-2-.89-2-2s.9-2 2-2 2 .89 2 2-.9 2-2 2zm6-7.5c-.28 0-.5-.22-.5-.5s.22-.5.5-.5.5.22.5.5-.22.5-.5.5zM19 3l-6 6 2 2 7-7V3z" />
            </svg>
          {/if}
          {#if video.needsReview}
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 24 24"
              class="h-4 w-4 text-sky-400 fill-current"
              aria-label="Needs Review"
            >
              <title>Needs Review</title>
              <!-- Material-style eye icon. Sky-400 sits opposite the
                   yellow-400 clip and the warning-gold star so each
                   status reads as its own thing at a glance. -->
              <path d="M12 4.5C7 4.5 2.73 7.61 1 12c1.73 4.39 6 7.5 11 7.5s9.27-3.11 11-7.5c-1.73-4.39-6-7.5-11-7.5zM12 17a5 5 0 1 1 0-10 5 5 0 0 1 0 10zm0-8a3 3 0 1 0 0 6 3 3 0 0 0 0-6z" />
            </svg>
          {/if}
          {#if video.isFavorite}
            <div class="text-warning text-lg leading-none" title="Favorite" aria-label="Favorite">★</div>
          {/if}
        </div>
      {/if}
      {#if resolutionLabel}
        <div class="absolute bottom-0 left-0 px-1.5 py-0.5 rounded-tr bg-black/70 text-white text-[10px] font-medium tabular-nums">
          {resolutionLabel}
        </div>
      {/if}
      {#if durationLabel}
        <div class="absolute bottom-0 right-0 px-1.5 py-0.5 rounded-tl bg-black/70 text-white text-[10px] font-medium tabular-nums">
          {durationLabel}
        </div>
      {/if}
    </div>
  </button>

  <div class="p-2 text-xs space-y-1">
    <button
      type="button"
      class="font-medium truncate block w-full text-left"
      onclick={onClick}
      title={displayTitle}
    >
      {displayTitle}
    </button>

    <!-- Clickable tag badges: body click filters; ✎ opens edit modal. -->
    {#if video.tags.length > 0}
      <div class="flex flex-wrap gap-1">
        {#each video.tags as t (t.id)}
          <span class="badge badge-sm {pillClass(t.id, t.tagGroupName)} gap-1">
            <button
              type="button"
              class="cursor-pointer"
              onclick={(e) => {
                e.stopPropagation();
                filterStore.requestAdd({
                  type: 'tag',
                  value: t.id,
                  label: t.name,
                  tagGroupName: t.tagGroupName
                });
              }}
              title="Filter by {t.tagGroupName}: {t.name}"
            >{t.name}</button>
            <button
              type="button"
              class="opacity-70 hover:opacity-100"
              onclick={(e) => openEditTag(t.id, e)}
              title="Edit tag"
              aria-label="Edit {t.name}"
            >✎</button>
          </span>
        {/each}
      </div>
    {/if}
  </div>
</div>

<TagEditModal bind:show={editTagModalShow} tag={editingTag} />

<script lang="ts">
  // Renders the guided tour (issue #170): a dimming spotlight cut around
  // the current step's target element plus a callout box with the step
  // copy and Back / Next / Skip controls. Mounted once in the root layout
  // so it can overlay any route and anchor to nav elements too.
  //
  // The spotlight is the classic "huge box-shadow" trick: a transparent
  // box sized to the target with a 9999px spread shadow dims everything
  // around it, with no four-panel geometry to get wrong. If the target
  // can't be resolved (hidden sidebar, wrong page, missing element) we
  // fall back to a centered callout with no spotlight.
  import { tour } from '$lib/tour.svelte';

  const PAD = 8;            // spotlight padding around the target
  const GAP = 12;           // gap between spotlight and callout
  const CALLOUT_W = 320;

  let rect = $state<DOMRect | null>(null);
  let calloutEl = $state<HTMLElement | null>(null);
  let calloutH = $state(160);

  // Re-resolve + measure the current target. Called on step change and on
  // scroll/resize so the spotlight stays glued while the page moves.
  function measure() {
    const sel = tour.current?.selector;
    if (!sel) { rect = null; return; }
    const el = document.querySelector(sel) as HTMLElement | null;
    if (!el || el.offsetParent === null) { rect = null; return; }   // missing or display:none
    const r = el.getBoundingClientRect();
    rect = r.width === 0 && r.height === 0 ? null : r;
  }

  // On each step, scroll the target into view, then measure across a few
  // frames so the spotlight settles after a smooth scroll.
  $effect(() => {
    if (!tour.active) return;
    void tour.index;
    const sel = tour.current?.selector;
    const el = sel ? (document.querySelector(sel) as HTMLElement | null) : null;
    el?.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'smooth' });
    let frames = 0;
    let raf = 0;
    const tick = () => { measure(); if (++frames < 30) raf = requestAnimationFrame(tick); };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  });

  $effect(() => {
    if (calloutEl) calloutH = calloutEl.offsetHeight;
  });

  // Callout placement: below the target if it fits, otherwise above; with
  // no target, dead center. Kept within the viewport horizontally.
  const pos = $derived.by(() => {
    if (typeof window === 'undefined') return { top: 0, left: 0, centered: true };
    const vw = window.innerWidth, vh = window.innerHeight;
    if (!rect) {
      return { top: Math.max(GAP, (vh - calloutH) / 2), left: (vw - CALLOUT_W) / 2, centered: true };
    }
    const below = rect.bottom + GAP;
    const fitsBelow = below + calloutH <= vh - GAP;
    const top = fitsBelow ? below : Math.max(GAP, rect.top - GAP - calloutH);
    const left = Math.min(Math.max(GAP, rect.left), vw - CALLOUT_W - GAP);
    return { top, left, centered: false };
  });

  function onKey(e: KeyboardEvent) {
    if (!tour.active) return;
    // Capture-phase + stopPropagation so the page's own shortcut handlers
    // (Browse keys, player) don't also fire while the tour is up.
    if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); tour.stop(); }
    else if (e.key === 'ArrowRight' || e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); tour.next(); }
    else if (e.key === 'ArrowLeft') { e.preventDefault(); e.stopPropagation(); tour.prev(); }
  }
</script>

<svelte:window onresize={measure} onscroll={measure} onkeydowncapture={onKey} />

{#if tour.active && tour.current}
  <!-- Spotlight. When there's no target rect we still render a full-screen
       dim (no cut-out) so the centered callout reads against a backdrop. -->
  {#if rect}
    <div
      class="fixed z-[60] rounded-lg pointer-events-none transition-all duration-150"
      style="
        top: {rect.top - PAD}px; left: {rect.left - PAD}px;
        width: {rect.width + PAD * 2}px; height: {rect.height + PAD * 2}px;
        box-shadow: 0 0 0 9999px rgba(0,0,0,0.55);
        outline: 2px solid var(--fallback-p, oklch(var(--p)/1)); outline-offset: 2px;"
    ></div>
  {:else}
    <div class="fixed inset-0 z-[60] bg-black/55 pointer-events-none"></div>
  {/if}

  <!-- Click-catcher so clicking outside the callout dismisses the tour. -->
  <button
    class="fixed inset-0 z-[61] cursor-default"
    aria-label="Dismiss tour"
    onclick={() => tour.stop()}
  ></button>

  <!-- Callout -->
  <div
    bind:this={calloutEl}
    class="fixed z-[62] card bg-base-100 shadow-xl border border-base-300 p-4"
    style="top: {pos.top}px; left: {pos.left}px; width: {CALLOUT_W}px;"
    role="dialog"
    aria-modal="true"
    aria-label="Guided tour"
  >
    <div class="flex items-start justify-between gap-2">
      <h3 class="font-semibold text-base">{tour.current.title}</h3>
      <span class="text-xs text-base-content/50 tabular-nums shrink-0 mt-0.5">
        {tour.index + 1} / {tour.total}
      </span>
    </div>
    <p class="text-sm text-base-content/80 mt-1.5">{tour.current.body}</p>
    <div class="flex items-center justify-between mt-4">
      <button class="btn btn-ghost btn-xs" onclick={() => tour.stop()}>Skip</button>
      <div class="flex gap-2">
        <button class="btn btn-sm" onclick={() => tour.prev()} disabled={tour.isFirst}>Back</button>
        <button class="btn btn-sm btn-primary" onclick={() => tour.next()}>
          {tour.isLast ? 'Done' : 'Next'}
        </button>
      </div>
    </div>
  </div>
{/if}

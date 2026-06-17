// Pure helpers for the Browse player's auto-sizing rules (issue #171):
//
//   · The queue thumbnail strip is ALWAYS fully visible.
//   · The video fills the space left above it.
//   · The tag area / controls below the video stay inside the player card —
//     they never overlap the header buttons or the strip.
//
// The DOM measurement (viewport height, card top, element heights) lives in
// the component; the arithmetic that's easy to get subtly wrong lives here so
// it can be unit-tested without a layout engine.

/**
 * Vertical budget for the whole player card: everything left in the viewport
 * once the header row and the always-visible queue strip below it are
 * reserved. Floored at `minPlayer` so the card never collapses.
 */
export function playerBudget(
  viewportHeight: number,
  cardTop: number,
  headerHeight: number,
  stripHeight: number,
  gaps: number,
  minPlayer: number,
): number {
  const raw = Math.round(viewportHeight - cardTop - headerHeight - stripHeight - gaps);
  return Math.max(minPlayer, raw);
}

/**
 * Height ceiling for the video picture itself: the card budget minus the
 * player's own chrome below the video (scrubber, controls, tag badges,
 * bookmark/block lists, card padding).
 *
 * `cardScrollHeight` is the card's full natural content height (video + chrome
 * + padding) and `videoBoxHeight` is the current rendered video box, so their
 * difference is the chrome — which is independent of how tall the video is.
 * Capping the video to `budget - chrome` therefore makes `video + chrome`
 * equal the budget exactly: the tag area sits inside the card rather than
 * spilling over what's below it. Floored at `minVideo`.
 */
export function videoHeightCap(
  budget: number,
  cardScrollHeight: number,
  videoBoxHeight: number,
  minVideo: number,
): number {
  const chrome = Math.max(0, cardScrollHeight - videoBoxHeight);
  return Math.max(minVideo, budget - chrome);
}

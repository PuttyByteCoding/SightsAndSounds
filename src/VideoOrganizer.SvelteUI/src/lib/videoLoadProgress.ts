// Pure helper for the video player's loading bar (issue #26).
//
// Given the furthest-buffered time (the end of the last `buffered`
// TimeRange) and the clip's duration, returns the 0..1 fraction of the
// file that has been retrieved — or null when the duration isn't known
// yet (pre-`loadedmetadata`), which the UI renders as an indeterminate
// bar. Clamped to [0,1] so an over-eager browser report can't push the
// bar past full.
export function loadProgressFraction(
  bufferedEnd: number,
  duration: number
): number | null {
  if (!Number.isFinite(duration) || duration <= 0) return null;
  const f = bufferedEnd / duration;
  if (!Number.isFinite(f)) return null;
  return Math.max(0, Math.min(1, f));
}

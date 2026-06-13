// Pure helper for the Browse player-mode thumbnail strip (issue #23).
//
// The strip is a single horizontal row of the queue's thumbnails. The
// two videos immediately BEFORE the current one pin to the left edge
// (CSS position: sticky) so the user can always step back as the strip
// scrolls forward — a rolling "previous 2" window that moves with the
// current video.
//
// Given the current video's index and a cell's index, returns the sticky
// `left` offset in px for that cell, or null when the cell isn't one of
// the two immediately-previous videos (so it scrolls normally). The
// pinned cells pack from the left edge: when both previous videos exist
// the earlier one sits at 0 and the nearer one at cellWidth+gap; when
// only one previous video exists (current is at index 1) it sits at 0.

export function stripStickyLeft(
  currentIndex: number,
  cellIndex: number,
  cellWidth: number,
  gap: number
): number | null {
  if (currentIndex < 0 || cellIndex < 0) return null;
  // Earlier of the two (current - 2): leftmost pinned slot.
  if (cellIndex === currentIndex - 2) return 0;
  // Nearer of the two (current - 1): second slot, unless it's the only
  // previous video (current at index 1), in which case it takes slot 0.
  if (cellIndex === currentIndex - 1) {
    const hasEarlier = currentIndex - 2 >= 0;
    return hasEarlier ? cellWidth + gap : 0;
  }
  return null;
}

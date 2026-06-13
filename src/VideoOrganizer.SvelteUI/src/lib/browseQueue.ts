// Pure decision core for the Browse page's filter → queue refresh.
//
// Extracted from browse/+page.svelte so the ordering + playback rules
// (issue #21) are unit-testable without mounting the whole page. The
// component owns the side effects (fetch, $state writes); this function
// just decides, given the freshly-fetched results, what the next queue
// and playing video should be.
//
// Issue #21 behavior: applying or changing a filter restarts the queue.
// The currently-playing video stops and position 0 of the newly filtered
// queue starts playing (or nothing, when the filter matched no videos).
// A ?id= deep-link opening a specific video overrides this for that one
// refresh (the caller passes resetPlayback=false), and refreshes that
// aren't filter-driven (post-edit re-fetch, "show all") leave playback
// alone.

export interface QueuePlanInput<T extends { id: string }> {
  // Results from /videos/filter, in server order.
  fetched: T[];
  // The currently-playing video, or null when nothing is playing. Kept
  // as-is unless resetPlayback is true.
  playing: T | null;
  // True for the first successful load this session. The first load may
  // shuffle into a random playlist; later filter changes keep server
  // order in Shuffle mode so the grid stays stable.
  firstLoad: boolean;
  isShuffle: boolean;
  // True for ?searchQuery= loads — the server already ranked by
  // relevance, so search results are never shuffled.
  hasSearchQuery: boolean;
  // True when this refresh should restart the queue from the top: a
  // filter/search change not superseded by a ?id= deep-link. False for
  // deep-link opens and non-filter refreshes.
  resetPlayback: boolean;
  // Orders a result set per the active sort mode (shuffles in shuffle
  // mode). Injected so callers stay in control and tests stay
  // deterministic.
  order: (list: T[]) => T[];
}

export interface QueuePlan<T extends { id: string }> {
  videos: T[];
  playing: T | null;
}

export function planFilteredQueue<T extends { id: string }>(
  input: QueuePlanInput<T>
): QueuePlan<T> {
  const { fetched, playing, firstLoad, isShuffle, hasSearchQuery, resetPlayback, order } = input;

  // Ordering. Search results are relevance-ranked, so never shuffle
  // them. Otherwise the first load shuffles (random playlist); later
  // filter changes keep server order in Shuffle mode and apply an
  // explicit sort otherwise.
  const ordered = firstLoad
    ? hasSearchQuery && isShuffle
      ? fetched
      : order(fetched)
    : isShuffle
      ? fetched
      : order(fetched);

  // Playback. A filter/search change restarts the queue from position 0
  // (issue #21) — stopping the current video and starting the new
  // queue's first entry, or nothing when the filter matched no videos.
  // Otherwise keep whatever is playing.
  const nextPlaying = resetPlayback ? (ordered[0] ?? null) : playing;

  return { videos: ordered, playing: nextPlaying };
}

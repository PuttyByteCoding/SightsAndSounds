// Regression tests for planFilteredQueue — the Browse page's filter →
// queue decision core. Focus is issue #21: applying or changing a filter
// restarts the queue from position 0 (the playing video stops and the
// new queue's first entry plays), while ?id= deep-links and non-filter
// refreshes leave playback alone.

import { describe, expect, test } from 'vitest';
import { planFilteredQueue } from './browseQueue';

interface V {
  id: string;
}
const v = (id: string): V => ({ id });

// Deterministic stand-in for orderVideos: reverse, so tests can tell
// whether `order` was applied without depending on the random shuffle.
const reverse = <T,>(list: T[]): T[] => [...list].reverse();
// Identity order, for cases where ordering isn't the point.
const asis = <T,>(list: T[]): T[] => list;

describe('planFilteredQueue — ordering', () => {
  test('first load applies the sort order (random playlist in shuffle)', () => {
    const plan = planFilteredQueue({
      fetched: [v('a'), v('b'), v('c')],
      playing: null,
      firstLoad: true,
      isShuffle: false,
      hasSearchQuery: false,
      resetPlayback: true,
      order: reverse
    });
    expect(plan.videos.map((x) => x.id)).toEqual(['c', 'b', 'a']);
  });

  test('first load with search + shuffle keeps server relevance order', () => {
    const plan = planFilteredQueue({
      fetched: [v('a'), v('b'), v('c')],
      playing: null,
      firstLoad: true,
      isShuffle: true,
      hasSearchQuery: true,
      resetPlayback: true,
      order: reverse // would reverse if applied; must NOT be applied here
    });
    expect(plan.videos.map((x) => x.id)).toEqual(['a', 'b', 'c']);
  });

  test('later filter change in shuffle keeps server order (no reshuffle)', () => {
    const plan = planFilteredQueue({
      fetched: [v('a'), v('b'), v('c')],
      playing: v('a'),
      firstLoad: false,
      isShuffle: true,
      hasSearchQuery: false,
      resetPlayback: true,
      order: reverse // not applied in shuffle mode
    });
    expect(plan.videos.map((x) => x.id)).toEqual(['a', 'b', 'c']);
  });

  test('later filter change with explicit sort applies the sort', () => {
    const plan = planFilteredQueue({
      fetched: [v('a'), v('b'), v('c')],
      playing: v('a'),
      firstLoad: false,
      isShuffle: false,
      hasSearchQuery: false,
      resetPlayback: true,
      order: reverse
    });
    expect(plan.videos.map((x) => x.id)).toEqual(['c', 'b', 'a']);
  });
});

describe('planFilteredQueue — filter change restarts the queue (issue #21)', () => {
  test('playing video stops; position 0 of the new queue plays', () => {
    const plan = planFilteredQueue({
      fetched: [v('x'), v('y'), v('z')],
      playing: v('was-playing'),
      firstLoad: false,
      isShuffle: true,
      hasSearchQuery: false,
      resetPlayback: true,
      order: asis
    });
    expect(plan.playing?.id).toBe('x'); // first of the filtered queue
    expect(plan.videos.map((x) => x.id)).toEqual(['x', 'y', 'z']);
  });

  test('resets to the first ENTRY even when the playing video still matches', () => {
    // The playing video is still present but no longer first → playback
    // moves to position 0, it is not preserved at its old spot.
    const plan = planFilteredQueue({
      fetched: [v('a'), v('playing'), v('c')],
      playing: v('playing'),
      firstLoad: false,
      isShuffle: true,
      hasSearchQuery: false,
      resetPlayback: true,
      order: asis
    });
    expect(plan.playing?.id).toBe('a');
  });

  test('filter matching nothing stops playback (null)', () => {
    const plan = planFilteredQueue({
      fetched: [],
      playing: v('was-playing'),
      firstLoad: false,
      isShuffle: true,
      hasSearchQuery: false,
      resetPlayback: true,
      order: asis
    });
    expect(plan.videos).toEqual([]);
    expect(plan.playing).toBeNull();
  });
});

describe('planFilteredQueue — playback preserved when not filter-driven', () => {
  test('deep-link override (resetPlayback false) keeps the current pick', () => {
    const playing = v('deep-linked');
    const plan = planFilteredQueue({
      fetched: [v('a'), v('b')],
      playing,
      firstLoad: true,
      isShuffle: true,
      hasSearchQuery: false,
      resetPlayback: false,
      order: asis
    });
    expect(plan.playing).toBe(playing); // not reset to videos[0]
    expect(plan.videos.map((x) => x.id)).toEqual(['a', 'b']);
  });

  test('post-edit / show-all refresh (resetPlayback false) keeps playback', () => {
    const playing = v('b');
    const plan = planFilteredQueue({
      fetched: [v('a'), v('b'), v('c')],
      playing,
      firstLoad: false,
      isShuffle: true,
      hasSearchQuery: false,
      resetPlayback: false,
      order: asis
    });
    expect(plan.playing).toBe(playing);
  });
});

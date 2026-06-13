// Tests for stripStickyLeft — the rolling "previous 2 pinned" offset
// math for the Browse player-mode thumbnail strip (issue #23).

import { describe, expect, test } from 'vitest';
import { stripStickyLeft } from './browseStrip';

const W = 200;
const GAP = 12;

describe('stripStickyLeft', () => {
  test('no current video (index -1) → nothing pinned', () => {
    expect(stripStickyLeft(-1, 0, W, GAP)).toBeNull();
    expect(stripStickyLeft(-1, 5, W, GAP)).toBeNull();
  });

  test('two previous videos pin at 0 and cellWidth+gap', () => {
    // current at index 5 → cells 3 and 4 pin.
    expect(stripStickyLeft(5, 3, W, GAP)).toBe(0); // current-2, leftmost
    expect(stripStickyLeft(5, 4, W, GAP)).toBe(W + GAP); // current-1, second
  });

  test('current cell and other cells are not pinned', () => {
    expect(stripStickyLeft(5, 5, W, GAP)).toBeNull(); // the current video
    expect(stripStickyLeft(5, 6, W, GAP)).toBeNull(); // upcoming
    expect(stripStickyLeft(5, 2, W, GAP)).toBeNull(); // older than the window
  });

  test('only one previous video (current at index 1) pins it at 0', () => {
    expect(stripStickyLeft(1, 0, W, GAP)).toBe(0); // current-1 takes slot 0
    expect(stripStickyLeft(1, -1, W, GAP)).toBeNull(); // current-2 doesn't exist
  });

  test('first video (index 0) → no previous to pin', () => {
    expect(stripStickyLeft(0, 0, W, GAP)).toBeNull();
  });

  test('offsets track the live cell width', () => {
    expect(stripStickyLeft(3, 2, 120, GAP)).toBe(120 + GAP);
    expect(stripStickyLeft(3, 2, 360, GAP)).toBe(360 + GAP);
  });
});

// Tests for loadProgressFraction — the video loading bar's buffered
// fraction (issue #26).

import { describe, expect, test } from 'vitest';
import { loadProgressFraction } from './videoLoadProgress';

describe('loadProgressFraction', () => {
  test('null (indeterminate) when duration is unknown', () => {
    expect(loadProgressFraction(0, 0)).toBeNull();
    expect(loadProgressFraction(10, NaN)).toBeNull();
    expect(loadProgressFraction(10, Infinity)).toBeNull(); // live/unknown duration
    expect(loadProgressFraction(10, -5)).toBeNull();
  });

  test('fraction of the file buffered', () => {
    expect(loadProgressFraction(30, 120)).toBe(0.25);
    expect(loadProgressFraction(60, 120)).toBe(0.5);
    expect(loadProgressFraction(0, 120)).toBe(0);
  });

  test('clamps to [0,1]', () => {
    expect(loadProgressFraction(130, 120)).toBe(1); // browser over-reports
    expect(loadProgressFraction(-3, 120)).toBe(0);
  });

  test('fully buffered → 1', () => {
    expect(loadProgressFraction(120, 120)).toBe(1);
  });
});

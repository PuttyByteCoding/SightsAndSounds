// Unit tests for the pure helpers in tableUtils.svelte.ts.
//
// First Vitest file in the repo — proves the wiring works end-to-end
// (vitest + jsdom + $lib alias + tsconfig). Covers:
//
//   · applySortClick       — the click → next-sort-stack state machine.
//                            Three modes (plain new, plain cycle, shift).
//   · compareBySortStack   — the comparator factory. Multi-column
//                            ordering + nulls-last semantics.
//   · loadColumnWidths /   — the localStorage round-trip. Asserts the
//     saveColumnWidths       persistence works AND that corrupt /
//                            below-floor data falls back to defaults.

import { afterEach, beforeEach, describe, expect, test } from 'vitest';
import {
  applySortClick,
  compareBySortStack,
  loadColumnWidths,
  saveColumnWidths,
  type SortEntry,
} from './tableUtils.svelte';

// ---------- applySortClick -----------------------------------------

describe('applySortClick', () => {
  test('plain click on empty stack → asc primary', () => {
    expect(applySortClick([], 'name', false)).toEqual([{ col: 'name', dir: 'asc' }]);
  });

  test('plain click on different column → replace with new asc primary', () => {
    const before: SortEntry[] = [
      { col: 'name', dir: 'asc' },
      { col: 'size', dir: 'desc' },
    ];
    // Shift-less click on a column NOT at the head of the stack
    // collapses the whole stack to just that column (the
    // "switch primary" UX). The previous secondary is discarded.
    expect(applySortClick(before, 'size', false)).toEqual([{ col: 'size', dir: 'asc' }]);
  });

  test('plain click cycles asc → desc → cleared (on a single-entry stack)', () => {
    let stack: SortEntry[] = [{ col: 'name', dir: 'asc' }];
    stack = applySortClick(stack, 'name', false);
    expect(stack).toEqual([{ col: 'name', dir: 'desc' }]);
    stack = applySortClick(stack, 'name', false);
    expect(stack).toEqual([]); // third click clears
  });

  test('shift-click on column not in stack → append as asc', () => {
    const before: SortEntry[] = [{ col: 'name', dir: 'asc' }];
    expect(applySortClick(before, 'size', true)).toEqual([
      { col: 'name', dir: 'asc' },
      { col: 'size', dir: 'asc' },
    ]);
  });

  test('shift-click on column already asc → flip to desc in place', () => {
    const before: SortEntry[] = [
      { col: 'name', dir: 'asc' },
      { col: 'size', dir: 'asc' },
    ];
    expect(applySortClick(before, 'size', true)).toEqual([
      { col: 'name', dir: 'asc' },
      { col: 'size', dir: 'desc' },
    ]);
  });

  test('shift-click on column already desc → remove from stack', () => {
    const before: SortEntry[] = [
      { col: 'name', dir: 'asc' },
      { col: 'size', dir: 'desc' },
    ];
    expect(applySortClick(before, 'size', true)).toEqual([{ col: 'name', dir: 'asc' }]);
  });
});

// ---------- compareBySortStack -------------------------------------

interface Row {
  name: string;
  size: number;
}

describe('compareBySortStack', () => {
  const rows: Row[] = [
    { name: 'banana', size: 10 },
    { name: 'apple',  size: 30 },
    { name: 'cherry', size: 20 },
  ];

  test('single asc sort by string', () => {
    const cmp = compareBySortStack<Row, 'name' | 'size'>(
      { name: (r) => r.name, size: (r) => r.size },
      [{ col: 'name', dir: 'asc' }]
    );
    expect([...rows].sort(cmp).map((r) => r.name))
      .toEqual(['apple', 'banana', 'cherry']);
  });

  test('single desc sort by number', () => {
    const cmp = compareBySortStack<Row, 'name' | 'size'>(
      { name: (r) => r.name, size: (r) => r.size },
      [{ col: 'size', dir: 'desc' }]
    );
    expect([...rows].sort(cmp).map((r) => r.size)).toEqual([30, 20, 10]);
  });

  test('two-column sort: primary asc by size, ties broken by name asc', () => {
    const ties: Row[] = [
      { name: 'banana', size: 10 },
      { name: 'apple',  size: 10 }, // tied with banana on size
      { name: 'cherry', size: 20 },
    ];
    const cmp = compareBySortStack<Row, 'name' | 'size'>(
      { name: (r) => r.name, size: (r) => r.size },
      [
        { col: 'size', dir: 'asc' },
        { col: 'name', dir: 'asc' },
      ]
    );
    // Apple comes before Banana within the size=10 tier.
    expect([...ties].sort(cmp).map((r) => r.name))
      .toEqual(['apple', 'banana', 'cherry']);
  });

  test('null sort position: last on asc, first on desc (current behavior)', () => {
    interface MaybeRow { v: number | null; }
    const data: MaybeRow[] = [{ v: 2 }, { v: null }, { v: 1 }];

    // On asc, nulls land at the bottom — what most users expect
    // ("show me real data first, gaps at the end").
    const asc = compareBySortStack<MaybeRow, 'v'>(
      { v: (r) => r.v },
      [{ col: 'v', dir: 'asc' }]
    );
    expect([...data].sort(asc).map((r) => r.v)).toEqual([1, 2, null]);

    // On desc, the implementation negates the comparator's return
    // value — which also flips the null position to the top. The
    // source code's comment claims "nulls sort last regardless of
    // direction," but the negate is unconditional and the comment
    // is misleading.
    //
    // This test documents the ACTUAL behavior so any future "fix"
    // (forcing nulls to always sort last by short-circuiting before
    // the negate) is a deliberate decision, not a regression. If
    // you change the source, update this expectation too.
    const desc = compareBySortStack<MaybeRow, 'v'>(
      { v: (r) => r.v },
      [{ col: 'v', dir: 'desc' }]
    );
    expect([...data].sort(desc).map((r) => r.v)).toEqual([null, 2, 1]);
  });

  test('numeric-aware string compare orders "video2" before "video10"', () => {
    interface S { s: string; }
    const data: S[] = [{ s: 'video10' }, { s: 'video2' }, { s: 'video1' }];
    const cmp = compareBySortStack<S, 's'>(
      { s: (r) => r.s },
      [{ col: 's', dir: 'asc' }]
    );
    expect([...data].sort(cmp).map((r) => r.s))
      .toEqual(['video1', 'video2', 'video10']);
  });
});

// ---------- loadColumnWidths / saveColumnWidths --------------------

describe('column width persistence', () => {
  // jsdom gives us a real localStorage. Clean between tests so one
  // test's writes can't leak into another's reads.
  beforeEach(() => {
    localStorage.clear();
  });
  afterEach(() => {
    localStorage.clear();
  });

  test('load returns defaults when nothing persisted', () => {
    const got = loadColumnWidths('test.fresh', { plays: 96, title: 480 });
    expect(got).toEqual({ plays: 96, title: 480 });
  });

  test('save → load round-trip preserves widths', () => {
    saveColumnWidths('test.roundtrip', { plays: 120, title: 600 });
    const got = loadColumnWidths('test.roundtrip', { plays: 96, title: 480 });
    expect(got).toEqual({ plays: 120, title: 600 });
  });

  test('persisted width below 30px floor falls back to default', () => {
    // Simulate stale persisted data from a session that violated
    // the floor (or an old version of the helper that used a
    // smaller minimum). The load path should reject it and fall
    // back to the caller's default.
    localStorage.setItem(
      'dataTableWidths.test.floor',
      JSON.stringify({ plays: 10, title: 480 })
    );
    const got = loadColumnWidths('test.floor', { plays: 96, title: 480 });
    expect(got.plays).toBe(96);
    expect(got.title).toBe(480);
  });

  test('corrupt JSON falls back to defaults instead of throwing', () => {
    localStorage.setItem('dataTableWidths.test.corrupt', '{not-json');
    // The load helper has a try/catch around JSON.parse; this
    // verifies a corrupt entry doesn't crash callers.
    const got = loadColumnWidths('test.corrupt', { plays: 96 });
    expect(got).toEqual({ plays: 96 });
  });

  test('save only stores keys; extra persisted keys are ignored on load', () => {
    // The current contract: load returns widths for the keys in
    // `defaults`, nothing more. If a future test/migration writes
    // an extra key, we won't accidentally return it.
    localStorage.setItem(
      'dataTableWidths.test.extras',
      JSON.stringify({ plays: 120, title: 600, ghost: 999 })
    );
    const got = loadColumnWidths('test.extras', { plays: 96, title: 480 });
    expect(got).toEqual({ plays: 120, title: 600 });
    expect((got as Record<string, number>).ghost).toBeUndefined();
  });
});

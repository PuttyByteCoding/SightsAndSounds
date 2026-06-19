import { beforeEach, describe, expect, test } from 'vitest';
import { filterStore } from './filterStore.svelte';
import type { FilterTag } from './types';

// filterStore is a $state-backed singleton, so it lives in the components
// project (the unit project has no Svelte plugin to compile runes).

const tag: FilterTag = { type: 'tag', value: 't1', label: 'T1' };
const slotOf = () =>
  filterStore.required.some(t => t.value === 't1') ? 'required'
  : filterStore.optional.some(t => t.value === 't1') ? 'optional'
  : filterStore.excluded.some(t => t.value === 't1') ? 'excluded'
  : 'off';

beforeEach(() => filterStore.clear());

describe('filterStore.cycleOrClear (issue #192)', () => {
  test('walks Off → Required → Optional → Excluded → Off', () => {
    expect(slotOf()).toBe('off');
    filterStore.cycleOrClear(tag); expect(slotOf()).toBe('required');
    filterStore.cycleOrClear(tag); expect(slotOf()).toBe('optional');
    filterStore.cycleOrClear(tag); expect(slotOf()).toBe('excluded');
    filterStore.cycleOrClear(tag); expect(slotOf()).toBe('off');
  });

  test('the tag lives in exactly one bucket at a time', () => {
    filterStore.cycleOrClear(tag); // required
    filterStore.cycleOrClear(tag); // optional
    const total =
      filterStore.required.length + filterStore.optional.length + filterStore.excluded.length;
    expect(total).toBe(1);
    expect(filterStore.optional.some(t => t.value === 't1')).toBe(true);
  });
});

describe('filterStore.cycle (chips, unchanged) never reaches Off', () => {
  test('Excluded wraps back to Required, not Off', () => {
    filterStore.cycle(tag); // required
    filterStore.cycle(tag); // optional
    filterStore.cycle(tag); // excluded
    filterStore.cycle(tag); // -> required (wrap), NOT off
    expect(slotOf()).toBe('required');
  });
});

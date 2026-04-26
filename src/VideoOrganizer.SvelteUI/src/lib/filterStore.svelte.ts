// Module-level state for the three-box tag filter (Required / Optional /
// Excluded) driving the /browse grid. Components call requestAdd(tag) to
// add a tag — the picker dialog opens, the user routes it to one of the
// three lists.

import type { FilterRefType } from './types';

export interface FilterTag {
  type: FilterRefType;
  value: string;
  label: string;
  // Optional display hint for tag-type refs.
  tagGroupName?: string;
}

function keyOf(t: FilterTag) {
  return `${t.type}::${t.value.toLowerCase()}`;
}

function _FilterStore() {
  let required = $state<FilterTag[]>([]);
  let optional = $state<FilterTag[]>([]);
  let excluded = $state<FilterTag[]>([]);
  let pending = $state<FilterTag | null>(null);

  function isEmpty() {
    return required.length + optional.length + excluded.length === 0;
  }

  function has(tag: FilterTag): boolean {
    const k = keyOf(tag);
    return required.some((t) => keyOf(t) === k)
        || optional.some((t) => keyOf(t) === k)
        || excluded.some((t) => keyOf(t) === k);
  }

  function requestAdd(tag: FilterTag) {
    pending = tag;
  }

  function applyPending(kind: 'required' | 'optional' | 'excluded') {
    if (!pending) return;
    const tag = pending;
    pending = null;
    const k = keyOf(tag);
    required = required.filter((t) => keyOf(t) !== k);
    optional = optional.filter((t) => keyOf(t) !== k);
    excluded = excluded.filter((t) => keyOf(t) !== k);
    if (kind === 'required') required = [...required, tag];
    else if (kind === 'optional') optional = [...optional, tag];
    else excluded = [...excluded, tag];
  }

  function cancelPending() { pending = null; }

  function remove(tag: FilterTag) {
    const k = keyOf(tag);
    required = required.filter((t) => keyOf(t) !== k);
    optional = optional.filter((t) => keyOf(t) !== k);
    excluded = excluded.filter((t) => keyOf(t) !== k);
  }

  function clear() {
    required = [];
    optional = [];
    excluded = [];
    pending = null;
  }

  return {
    get required() { return required; },
    get optional() { return optional; },
    get excluded() { return excluded; },
    get pending() { return pending; },
    isEmpty,
    has,
    requestAdd,
    applyPending,
    cancelPending,
    remove,
    clear
  };
}

export const filterStore = _FilterStore();

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
  // Set when the tag click originated from a specific video's tag
  // pill (VideoCard / VideoPlayer). Lets FilterDialog offer the
  // "Remove from this video" action — without it the dialog can
  // only filter / rename / delete the tag itself. Untouched on tag
  // clicks from the browse sidebar / search box (no video context).
  videoId?: string;
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

  // Direct route into a specific bucket. Used by surfaces that
  // already know which bucket they want (e.g. the Flags tree's
  // True/False sub-items, which map to Required/Excluded without
  // needing the picker dialog). Idempotent: existing entries with
  // the same key are removed from every bucket first so the tag
  // ends up in exactly one place.
  function apply(tag: FilterTag, kind: 'required' | 'optional' | 'excluded') {
    const k = keyOf(tag);
    required = required.filter((t) => keyOf(t) !== k);
    optional = optional.filter((t) => keyOf(t) !== k);
    excluded = excluded.filter((t) => keyOf(t) !== k);
    if (kind === 'required') required = [...required, tag];
    else if (kind === 'optional') optional = [...optional, tag];
    else excluded = [...excluded, tag];
  }

  // Advance a tag to the next filter bucket, wrapping around:
  //   Required → Optional → Excluded → Required
  // Backs the "click a filter-bar chip to cycle its slot" gesture (issue
  // #80). A tag not currently in any bucket lands in Required. Removal is
  // still the chip's separate × button.
  function cycle(tag: FilterTag) {
    const k = keyOf(tag);
    if (required.some((t) => keyOf(t) === k)) apply(tag, 'optional');
    else if (optional.some((t) => keyOf(t) === k)) apply(tag, 'excluded');
    else if (excluded.some((t) => keyOf(t) === k)) apply(tag, 'required');
    else apply(tag, 'required');
  }

  function applyPending(kind: 'required' | 'optional' | 'excluded') {
    if (!pending) return;
    const tag = pending;
    pending = null;
    apply(tag, kind);
  }

  // Replace the entire active filter with just the pending item in the
  // chosen bucket — backs the FilterDialog "Clear Existing Filter & Set"
  // action (vs applyPending, which adds to the current filter).
  function applyPendingReplacingAll(kind: 'required' | 'optional' | 'excluded') {
    if (!pending) return;
    const tag = pending;
    pending = null;
    required = kind === 'required' ? [tag] : [];
    optional = kind === 'optional' ? [tag] : [];
    excluded = kind === 'excluded' ? [tag] : [];
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
    applyPendingReplacingAll,
    apply,
    cycle,
    cancelPending,
    remove,
    clear
  };
}

export const filterStore = _FilterStore();

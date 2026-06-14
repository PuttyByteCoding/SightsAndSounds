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

// Persist the active filter so it survives reload / restart (issue #89). Only
// the three buckets are stored; `pending` is transient (an in-flight picker
// choice) and never persisted.
const STORAGE_KEY = 'browseFilterV1';
type Persisted = { required: FilterTag[]; optional: FilterTag[]; excluded: FilterTag[] };

function loadPersisted(): Persisted {
  const empty: Persisted = { required: [], optional: [], excluded: [] };
  if (typeof localStorage === 'undefined') return empty;
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return empty;
    const p = JSON.parse(raw);
    // Defensive shape guard — a corrupt / outdated blob must not break the page.
    const arr = (x: unknown): FilterTag[] =>
      Array.isArray(x)
        ? x.filter(
            (t): t is FilterTag =>
              !!t && typeof t.type === 'string' && typeof t.value === 'string' && typeof t.label === 'string'
          )
        : [];
    return { required: arr(p?.required), optional: arr(p?.optional), excluded: arr(p?.excluded) };
  } catch {
    return empty;
  }
}

function _FilterStore() {
  const initial = loadPersisted();
  let required = $state<FilterTag[]>(initial.required);
  let optional = $state<FilterTag[]>(initial.optional);
  let excluded = $state<FilterTag[]>(initial.excluded);
  let pending = $state<FilterTag | null>(null);

  // Write the current buckets back to localStorage. Called after every
  // mutation. videoId is dropped — it's a transient "remove from this video"
  // context that would be stale on the next load.
  function persist() {
    if (typeof localStorage === 'undefined') return;
    try {
      const strip = (list: FilterTag[]) =>
        list.map((t) => ({ type: t.type, value: t.value, label: t.label, tagGroupName: t.tagGroupName }));
      localStorage.setItem(
        STORAGE_KEY,
        JSON.stringify({ required: strip(required), optional: strip(optional), excluded: strip(excluded) })
      );
    } catch {
      /* storage full / disabled — non-fatal, filter just won't persist */
    }
  }

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
    persist();
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
    persist();
  }

  function cancelPending() { pending = null; }

  function remove(tag: FilterTag) {
    const k = keyOf(tag);
    required = required.filter((t) => keyOf(t) !== k);
    optional = optional.filter((t) => keyOf(t) !== k);
    excluded = excluded.filter((t) => keyOf(t) !== k);
    persist();
  }

  function clear() {
    required = [];
    optional = [];
    excluded = [];
    pending = null;
    persist();
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

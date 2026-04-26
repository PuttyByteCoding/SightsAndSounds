// Shared helpers for the project's tables. Two pieces:
//   1. `applySortClick(stack, key, shift)` — produces the next stack for a
//      multi-column sort UX. Plain click replaces with the clicked column
//      (cycling asc → desc → cleared on the primary). Shift-click appends
//      / toggles direction / removes the column from the existing stack.
//   2. `compareBySortStack(getters, stack)` — builds a `(a, b) => number`
//      comparator for `Array.sort()` that walks the stack until a non-zero
//      result.
//
// Tables also need column resize. That's a separate `useColumnResize`
// Svelte 5 attachment helper — caller wires up state and renders the
// drag handle, this just owns the mouse math + persistence.

export type SortDir = 'asc' | 'desc';
export interface SortEntry<K extends string = string> {
  col: K;
  dir: SortDir;
}

// Compute the next sort stack after a header click. Pure — caller assigns
// the result back into reactive state.
//
// Plain click (shift=false):
//   - If the clicked col is the only/primary entry: cycle asc → desc →
//     cleared. So a third click on the same column removes the sort.
//   - Otherwise: replace the stack with just `[{col, asc}]`.
// Shift click (shift=true):
//   - Not in stack: append as asc (becomes secondary/tertiary).
//   - In stack asc: flip to desc in place.
//   - In stack desc: remove from stack (preserves positions of others).
export function applySortClick<K extends string>(
  stack: SortEntry<K>[],
  col: K,
  shift: boolean
): SortEntry<K>[] {
  const idx = stack.findIndex((e) => e.col === col);
  if (shift) {
    if (idx === -1) return [...stack, { col, dir: 'asc' }];
    if (stack[idx].dir === 'asc')
      return stack.map((e, i) => (i === idx ? { col, dir: 'desc' as const } : e));
    return stack.filter((_, i) => i !== idx);
  }
  if (idx === 0 && stack.length === 1) {
    return stack[0].dir === 'asc' ? [{ col, dir: 'desc' }] : [];
  }
  return [{ col, dir: 'asc' }];
}

// 1-based position the col occupies in the stack, or 0 if not sorted.
// Tables render this as a small superscript next to the column header so
// the user can see which key is primary/secondary/tertiary.
export function sortPosition<K extends string>(stack: SortEntry<K>[], col: K): number {
  const idx = stack.findIndex((e) => e.col === col);
  return idx < 0 ? 0 : idx + 1;
}

export function sortDir<K extends string>(stack: SortEntry<K>[], col: K): SortDir | null {
  return stack.find((e) => e.col === col)?.dir ?? null;
}

// Build a comparator from the sort stack and a per-column getter map.
// Each getter pulls the comparable value (string or number) for one row
// and one column. The comparator walks the stack and returns the first
// non-zero comparison.
export function compareBySortStack<T, K extends string>(
  getters: Record<K, (row: T) => string | number | null | undefined>,
  stack: SortEntry<K>[]
): (a: T, b: T) => number {
  return (a, b) => {
    for (const { col, dir } of stack) {
      const get = getters[col];
      if (!get) continue;
      const av = get(a);
      const bv = get(b);
      const cmp = compareValues(av, bv);
      if (cmp !== 0) return dir === 'asc' ? cmp : -cmp;
    }
    return 0;
  };
}

function compareValues(a: unknown, b: unknown): number {
  // Nulls sort last (regardless of direction — flipping happens after).
  const aNull = a == null || a === '';
  const bNull = b == null || b === '';
  if (aNull && bNull) return 0;
  if (aNull) return 1;
  if (bNull) return -1;
  if (typeof a === 'number' && typeof b === 'number') return a - b;
  // Numeric/locale-aware string compare so "video10" sorts after "video2".
  return String(a).localeCompare(String(b), undefined, {
    sensitivity: 'base',
    numeric: true,
  });
}

// ---- Column resize ----------------------------------------------------

// localStorage layout: dataTableWidths.${storageKey} → { [colKey]: px }
//
// Implementation note: callers do `let widths = $state(loadColumnWidths(...))`
// to get a reactive map, then mutate it directly (`widths[col] = w`). We
// intentionally don't wrap an object-with-methods in $state — Svelte 5's
// proxying only sees property writes done via the proxy, and method
// closures over the *factory's* local state would update the original,
// not the proxy, breaking reactivity. Plain functions sidestep that.

// Loads persisted widths from localStorage, falling back to defaults for
// any missing or implausible values.
export function loadColumnWidths(
  storageKey: string,
  defaults: Record<string, number>
): Record<string, number> {
  const out = { ...defaults };
  try {
    const raw = localStorage.getItem(`dataTableWidths.${storageKey}`);
    if (raw) {
      const parsed = JSON.parse(raw) as Record<string, number>;
      for (const k of Object.keys(defaults)) {
        if (typeof parsed[k] === 'number' && parsed[k] >= 40) {
          out[k] = parsed[k];
        }
      }
    }
  } catch { /* corrupt or quota — non-fatal */ }
  return out;
}

export function saveColumnWidths(
  storageKey: string,
  widths: Record<string, number>
): void {
  try {
    // $state proxies show up as objects with the right keys, but JSON.stringify
    // on the proxy still serializes the underlying values, so this is safe.
    localStorage.setItem(`dataTableWidths.${storageKey}`, JSON.stringify(widths));
  } catch { /* ignore */ }
}

// Mouse-drag helper for column resize handles. Call from onmousedown on
// the handle. Min width 60px so a column doesn't disappear.
export function startColumnResize(
  e: MouseEvent,
  startWidth: number,
  onChange: (next: number) => void
) {
  e.preventDefault();
  e.stopPropagation();
  const startX = e.clientX;
  function onMove(ev: MouseEvent) {
    onChange(Math.max(60, startWidth + (ev.clientX - startX)));
  }
  function onUp() {
    window.removeEventListener('mousemove', onMove);
    window.removeEventListener('mouseup', onUp);
  }
  window.addEventListener('mousemove', onMove);
  window.addEventListener('mouseup', onUp);
}

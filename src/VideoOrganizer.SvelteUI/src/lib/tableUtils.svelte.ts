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

// ARIA value for the `aria-sort` attribute on a sortable <th>. Maps
// the internal SortDir to the spec-mandated tokens 'ascending' /
// 'descending', and falls back to 'none' for columns not currently
// in the sort stack. Apply via `aria-sort={ariaSort(stack, col)}` on
// every sortable <th> — screen readers announce it as part of the
// column header so users can tell at a glance whether the column is
// participating in the sort.
export function ariaSort<K extends string>(
  stack: SortEntry<K>[],
  col: K
): 'ascending' | 'descending' | 'none' {
  const dir = sortDir(stack, col);
  return dir === 'asc' ? 'ascending' : dir === 'desc' ? 'descending' : 'none';
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
// any missing or implausible values. The 30px floor here matches the
// default min in startColumnResize so a narrow column the user shrank
// in a prior session round-trips cleanly.
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
        if (typeof parsed[k] === 'number' && parsed[k] >= 30) {
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
// the handle. Defaults: 30px min so a column can shrink past tiny icon
// columns (favorite ★ defaults to 48, merge checkbox to 40) without
// being clamped back up the instant the drag starts. Optional max cap
// for callers that want to prevent runaway widening.
export interface ResizeOptions {
  /** Minimum width in px. Default 30. */
  minWidth?: number;
  /** Maximum width in px. Default unlimited. */
  maxWidth?: number;
}
export function startColumnResize(
  e: MouseEvent,
  startWidth: number,
  onChange: (next: number) => void,
  opts: ResizeOptions = {}
) {
  e.preventDefault();
  e.stopPropagation();
  const startX = e.clientX;
  const minW = opts.minWidth ?? 30;
  const maxW = opts.maxWidth ?? Number.POSITIVE_INFINITY;
  // Body-level resize cursor + selection lock so the user gets the
  // col-resize cursor as long as the drag is active, even after the
  // pointer leaves the 8px handle. Paired with `html[data-table-resizing]`
  // rules in app.css.
  try { document.documentElement.dataset.tableResizing = 'true'; } catch {}
  function onMove(ev: MouseEvent) {
    const next = startWidth + (ev.clientX - startX);
    onChange(Math.min(maxW, Math.max(minW, next)));
  }
  function onUp() {
    window.removeEventListener('mousemove', onMove);
    window.removeEventListener('mouseup', onUp);
    try { delete document.documentElement.dataset.tableResizing; } catch {}
  }
  window.addEventListener('mousemove', onMove);
  window.addEventListener('mouseup', onUp);
}

// Auto-fit a column to its widest cell content.
//
// Approach: temporarily flip the table to table-layout: auto and remove
// the explicit width on the target <col> so the browser sizes the column
// to fit the widest cell. Measure the rendered column width, restore the
// original styles, then call `onChange` with the measured value (clamped
// to min/max).
//
// Why not measure cells with scrollWidth in place? Under table-layout:
// fixed the <td> is forced to the colgroup width and scrollWidth reflects
// the overflowed content — but cells that *wrap* (e.g. break-words) don't
// produce horizontal overflow at all, so scrollWidth equals clientWidth
// and we'd never find a width bigger than the current one. The
// auto-layout dance is the only reliable way to learn "what width would
// this column take if it had room to breathe".
export function autoFitColumn(
  tableEl: HTMLTableElement,
  colIndex: number,
  onChange: (next: number) => void,
  opts: ResizeOptions = {}
) {
  const cols = tableEl.querySelectorAll(':scope > colgroup > col');
  const colEl = cols[colIndex] as HTMLTableColElement | undefined;
  if (!colEl) return;

  // Save styles we're about to overwrite.
  const prevColWidth   = colEl.style.width;
  const prevTableLayout = tableEl.style.tableLayout;
  const prevTableWidth = tableEl.style.width;

  // Switch to auto layout so the column is content-driven.
  colEl.style.width = 'auto';
  tableEl.style.tableLayout = 'auto';
  tableEl.style.width = 'auto';
  // Force layout to settle.
  void tableEl.offsetHeight;

  // Measure the th in the requested column. Under auto layout its
  // offsetWidth is the natural column width (the th and every td in
  // that column share the same width).
  let target = tableEl.querySelector<HTMLElement>(
    `:scope > thead > tr > *:nth-child(${colIndex + 1})`
  );
  if (!target) {
    target = tableEl.querySelector<HTMLElement>(
      `:scope > tbody > tr > *:nth-child(${colIndex + 1})`
    );
  }
  const natural = target ? target.offsetWidth : 0;

  // Restore.
  colEl.style.width = prevColWidth;
  tableEl.style.tableLayout = prevTableLayout;
  tableEl.style.width = prevTableWidth;

  const minW = opts.minWidth ?? 30;
  const maxW = opts.maxWidth ?? Number.POSITIVE_INFINITY;
  // Small fudge so content doesn't kiss the right edge.
  onChange(Math.min(maxW, Math.max(minW, natural + 4)));
}

// Auto-fit a single column of a CSS-grid-based "table".
//
// Layout convention (matches DataTableModal's grid):
//   <div data-resizable-grid>
//     <div data-grid-header style="grid-template-columns: …">
//       <div data-col-index="0">…</div>
//       <div data-col-index="1">…</div>
//       …
//     </div>
//     <div data-grid-row style="grid-template-columns: …">
//       <div>…</div>  <!-- column 0 cell -->
//       <div>…</div>  <!-- column 1 cell -->
//       …
//     </div>
//     … more rows
//   </div>
//
// Each row carries its own inline `grid-template-columns` (so col widths
// stay aligned across rows). To measure the natural width of column N
// we temporarily replace track N with `max-content` on every row, force
// a reflow, read the rendered cell width, and pick the largest.
export function autoFitGridColumn(
  gridEl: HTMLElement,
  colIndex: number,
  onChange: (next: number) => void,
  opts: ResizeOptions = {}
) {
  const rows = gridEl.querySelectorAll<HTMLElement>(
    ':scope > [data-grid-header], :scope > [data-grid-row]'
  );
  if (rows.length === 0) return;

  let measured = 0;
  for (const row of rows) {
    const prevTpl = row.style.gridTemplateColumns;
    const tracks = prevTpl.split(/\s+/);
    if (colIndex >= tracks.length) continue;
    tracks[colIndex] = 'max-content';
    row.style.gridTemplateColumns = tracks.join(' ');
    // Force layout before reading offsetWidth.
    void row.offsetHeight;
    const cell = row.children[colIndex] as HTMLElement | undefined;
    if (cell) measured = Math.max(measured, cell.offsetWidth);
    row.style.gridTemplateColumns = prevTpl;
  }

  const minW = opts.minWidth ?? 30;
  const maxW = opts.maxWidth ?? Number.POSITIVE_INFINITY;
  onChange(Math.min(maxW, Math.max(minW, measured + 4)));
}

// ---- Svelte 5 action: use:resizable -----------------------------------
//
// Reusable action that owns the mousedown-to-drag + dblclick-to-auto-fit
// wiring for a single column's resize handle. The action's bound element
// (an absolutely-positioned <button> inside a <th>) is the drag surface;
// the action figures out which column it belongs to by walking up to the
// nearest <th> and counting siblings — so callers don't need to track
// column index or bind:this to the table.
//
// Usage (replaces the older inline onmousedown={(e) => startColumnResize(...)}
// pattern):
//
//   <button
//     use:resizable={{
//       getWidth: () => widths.plays ?? 96,
//       setWidth: (w) => setWidth('plays', w),
//       maxWidth: 400,         // optional cap
//     }}
//     class="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize ..."
//     aria-label="Resize Plays"
//   ></button>
//
// Double-click the same handle to auto-fit. Single-click does nothing
// (mousedown without movement leaves the width unchanged).

export interface ResizableParams {
  /** Read the column's current width in px. Called fresh on each mousedown. */
  getWidth: () => number;
  /** Persist a new width. Called repeatedly during drag and once after auto-fit. */
  setWidth: (next: number) => void;
  /** Minimum width in px. Default 30. */
  minWidth?: number;
  /** Maximum width in px. Default unlimited. */
  maxWidth?: number;
}

export function resizable(node: HTMLElement, initialParams: ResizableParams) {
  let params = initialParams;

  // Tag the node so app.css can give every handle a discoverable
  // baseline tint + a stronger hover state. Without this every
  // call-site has to remember the same set of Tailwind classes for
  // hover/active backgrounds, and any divergence makes some
  // columns' handles invisibly different from the rest.
  node.classList.add('resize-handle');

  // The owning cell + its grid/table. Two layouts are supported:
  //   1. <table> with <th>: walk up to the th, count siblings for index.
  //   2. CSS-grid layout: handle (or its container) carries a
  //      `data-col-index="N"` attribute; the grid container is marked
  //      with `data-resizable-grid`.
  function ownerTable(): HTMLTableElement | null {
    return node.closest('table');
  }
  function ownerGrid(): HTMLElement | null {
    return node.closest<HTMLElement>('[data-resizable-grid]');
  }
  function columnIndex(): number {
    // Table path: handle inside <th>, find sibling position.
    const th = node.closest('th');
    if (th && th.parentElement) {
      return Array.from(th.parentElement.children).indexOf(th);
    }
    // Grid path: handle (or some ancestor up to the grid) declares its
    // column index explicitly.
    const tagged = node.closest<HTMLElement>('[data-col-index]');
    if (tagged?.dataset.colIndex !== undefined) {
      const idx = Number.parseInt(tagged.dataset.colIndex, 10);
      return Number.isNaN(idx) ? -1 : idx;
    }
    return -1;
  }

  function onMouseDown(e: MouseEvent) {
    // Skip the SECOND mousedown of a dblclick — without this guard
    // the browser fires mousedown twice and we'd both auto-fit AND
    // start a phantom drag from the just-applied auto-fit width.
    // Tripled/quadrupled rapid clicks (detail >= 3) DO start a
    // drag — they're not a dblclick boundary, and previously this
    // path was the only way to "release" a column that got stuck
    // in a wider-than-current auto-fit cycle.
    if (e.detail === 2) return;
    startColumnResize(e, params.getWidth(), params.setWidth, {
      minWidth: params.minWidth,
      maxWidth: params.maxWidth,
    });
  }
  function onDblClick(e: MouseEvent) {
    e.preventDefault();
    e.stopPropagation();
    const idx = columnIndex();
    if (idx < 0) return;
    const table = ownerTable();
    if (table) {
      autoFitColumn(table, idx, params.setWidth, {
        minWidth: params.minWidth,
        maxWidth: params.maxWidth,
      });
      return;
    }
    const grid = ownerGrid();
    if (grid) {
      autoFitGridColumn(grid, idx, params.setWidth, {
        minWidth: params.minWidth,
        maxWidth: params.maxWidth,
      });
    }
  }

  node.addEventListener('mousedown', onMouseDown);
  node.addEventListener('dblclick', onDblClick);

  return {
    update(newParams: ResizableParams) {
      params = newParams;
    },
    destroy() {
      node.removeEventListener('mousedown', onMouseDown);
      node.removeEventListener('dblclick', onDblClick);
      node.classList.remove('resize-handle');
    },
  };
}

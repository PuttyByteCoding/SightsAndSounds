<!--
  Generic searchable-table modal used by the "Show Failed" / "Show Queue"
  buttons on the Background Tasks page.

  Caller hands in:
    - title       — modal heading (also keys persisted column widths)
    - columns     — list of { key, label, mono?, wide?, align?, defaultWidth? }
    - rows        — already-fetched data (each row is a Record<string, unknown>)
    - searchKeys  — which row fields the live filter searches
    - onClose     — close handler
    - emptyText?  — message shown when rows is empty
    - onRefresh?  — optional refresh callback (renders a ↻ button)
    - loading?    — drives a spinner overlay during refresh
    - error?      — error string shown above the table

  Layout: table-layout:fixed + a <colgroup> so explicit per-column widths
  apply. Each <th> has a drag handle on its right edge for resizing; the
  resulting widths persist to localStorage keyed by `dataTableWidths.${title}`.
-->
<script lang="ts">
  import {
    applySortClick,
    compareBySortStack,
    sortDir,
    sortPosition,
    type SortEntry,
  } from '$lib/tableUtils.svelte';

  interface Column {
    key: string;
    label: string;
    mono?: boolean;
    wide?: boolean;
    align?: 'left' | 'right';
    defaultWidth?: number;
  }

  type Row = Record<string, unknown>;

  let {
    title,
    columns,
    rows,
    searchKeys,
    emptyText = 'Nothing here.',
    loading = false,
    error = null,
    onRefresh,
    onClose
  }: {
    title: string;
    columns: Column[];
    rows: Row[];
    searchKeys: string[];
    emptyText?: string;
    loading?: boolean;
    error?: string | null;
    onRefresh?: () => void;
    onClose: () => void;
  } = $props();

  let search = $state('');

  // ---- Column widths -----------------------------------------------------

  // Default width per column key. Keep widths conservative so the table
  // fits without horizontal scroll on a normal screen by default; users
  // can drag handles to widen.
  function defaultWidthFor(col: Column): number {
    if (col.defaultWidth) return col.defaultWidth;
    if (col.key === 'fileSizeBytes') return 100;
    if (col.key === 'status') return 120;
    if (col.key === 'fileName') return 240;
    if (col.key === 'filePath') return 380;
    if (col.key === 'error') return 360;
    return 200;
  }

  // Derived so it picks up if `title` changes (it normally doesn't — the
  // modal is destroyed and recreated per kind — but Svelte 5 warns when a
  // prop is captured into a const at top level).
  const storageKey = $derived(`dataTableWidths.${title}`);

  // Initialize from localStorage if available; otherwise defaults.
  let widths = $state<Record<string, number>>(loadWidths());

  function loadWidths(): Record<string, number> {
    try {
      const raw = localStorage.getItem(storageKey);
      if (raw) {
        const parsed = JSON.parse(raw) as Record<string, number>;
        const out: Record<string, number> = {};
        for (const c of columns) {
          out[c.key] = typeof parsed[c.key] === 'number' && parsed[c.key] > 20
            ? parsed[c.key]
            : defaultWidthFor(c);
        }
        return out;
      }
    } catch { /* ignore */ }
    const out: Record<string, number> = {};
    for (const c of columns) out[c.key] = defaultWidthFor(c);
    return out;
  }

  function saveWidths() {
    try { localStorage.setItem(storageKey, JSON.stringify(widths)); }
    catch { /* quota / privacy mode — non-fatal */ }
  }

  // Resize: mousedown on a column's right edge, track movement, commit on
  // mouseup. Min width 60px so a column doesn't disappear entirely.
  let resizing: { key: string; startX: number; startWidth: number } | null = null;

  function startResize(e: MouseEvent, key: string) {
    e.preventDefault();
    e.stopPropagation();
    resizing = { key, startX: e.clientX, startWidth: widths[key] ?? defaultWidthFor(columns.find((c) => c.key === key)!) };
    window.addEventListener('mousemove', onResize);
    window.addEventListener('mouseup', endResize);
  }

  function onResize(e: MouseEvent) {
    if (!resizing) return;
    const next = { ...widths };
    next[resizing.key] = Math.max(60, resizing.startWidth + (e.clientX - resizing.startX));
    widths = next;
  }

  function endResize() {
    window.removeEventListener('mousemove', onResize);
    window.removeEventListener('mouseup', endResize);
    if (resizing) {
      saveWidths();
      resizing = null;
    }
  }

  // ---- Sort + filter ----------------------------------------------------

  // Multi-column sort stack. Click a header → primary sort (cycles
  // asc → desc → cleared). Shift-click → append/toggle/remove additional
  // levels. Persisted to localStorage so the user's preferred sort
  // sticks across modal reopens.
  const sortStorageKey = $derived(`dataTableSort.${title}`);
  let sortStack = $state<SortEntry<string>[]>([]);

  // Restore on title change (= modal kind changed).
  $effect(() => {
    void title;
    try {
      const raw = localStorage.getItem(sortStorageKey);
      sortStack = raw ? (JSON.parse(raw) as SortEntry<string>[]) : [];
    } catch {
      sortStack = [];
    }
  });

  function onHeaderClick(colKey: string, e: MouseEvent) {
    sortStack = applySortClick(sortStack, colKey, e.shiftKey);
    try { localStorage.setItem(sortStorageKey, JSON.stringify(sortStack)); }
    catch { /* ignore */ }
  }

  // Build per-column getters from the actual row shape — values come back
  // as numbers when looked-up keys hit *Bytes / *Count fields, strings
  // otherwise. compareBySortStack normalizes for comparison.
  const sortGetters = $derived.by(() => {
    const map: Record<string, (r: Row) => string | number | null | undefined> = {};
    for (const c of columns) {
      const k = c.key;
      map[k] = (r) => r[k] as string | number | null | undefined;
    }
    return map;
  });

  // Live filter applied across the caller-named search keys. Case-insensitive
  // substring match — small enough rows that we can do this in-memory.
  const filtered = $derived.by(() => {
    const q = search.trim().toLowerCase();
    let out = q
      ? rows.filter((r) =>
          searchKeys.some((k) => {
            const v = r[k];
            return v != null && String(v).toLowerCase().includes(q);
          })
        )
      : rows;
    if (sortStack.length > 0) {
      const cmp = compareBySortStack(sortGetters, sortStack);
      out = [...out].sort(cmp);
    }
    return out;
  });

  // ---- Cell rendering ----------------------------------------------------

  function formatBytes(bytes: unknown): string {
    const n = Number(bytes);
    if (!Number.isFinite(n) || n <= 0) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0, x = n;
    while (x >= 1024 && i < units.length - 1) { x /= 1024; i++; }
    return `${x.toFixed(i === 0 ? 0 : 2)} ${units[i]}`;
  }

  function cellValue(row: Row, col: Column): string {
    const v = row[col.key];
    if (v == null || v === '') return '—';
    if (/size|bytes/i.test(col.key)) return formatBytes(v);
    return String(v);
  }

  // True if the cell holds a "no value" placeholder — used to render
  // the empty marker in dimmed italic so an actually-empty Error column
  // is visually obvious vs a populated one.
  function isEmpty(row: Row, col: Column): boolean {
    const v = row[col.key];
    return v == null || v === '';
  }

  // ---- Modal resize ------------------------------------------------------

  // Native CSS `resize: both` lets the user drag the bottom-right corner.
  // We observe the resulting size changes via ResizeObserver and persist
  // them to localStorage so the modal opens at the user's preferred size
  // next time. Keyed by title so each modal kind (Failed/Queue/Dups for
  // each worker) has its own remembered size.
  const sizeKey = $derived(`dataTableModalSize.${title}`);
  let modalEl = $state<HTMLDivElement | null>(null);
  let modalWidth = $state<number | null>(null);
  let modalHeight = $state<number | null>(null);

  // Load persisted size on mount (and again when title changes).
  $effect(() => {
    void title;
    try {
      const raw = localStorage.getItem(sizeKey);
      if (raw) {
        const parsed = JSON.parse(raw) as { w?: number; h?: number };
        // Validation thresholds match the CSS min-width / min-height so
        // any garbage / corrupted / pre-fix tiny saves fall back to the
        // 95vw × 80vh defaults instead of producing an unusably small
        // modal.
        if (typeof parsed.w === 'number' && parsed.w >= 480) modalWidth = parsed.w;
        else modalWidth = null;
        if (typeof parsed.h === 'number' && parsed.h >= 320) modalHeight = parsed.h;
        else modalHeight = null;
      } else {
        modalWidth = null;
        modalHeight = null;
      }
    } catch {
      modalWidth = null;
      modalHeight = null;
    }
  });

  // ResizeObserver fires after the user drags the corner. We MUST read
  // the border-box size, not contentRect — daisyUI's `.modal-box` has
  // ~24px padding on each side, and `box-sizing: border-box` (Tailwind
  // default) means our inline `width: Npx` sets the BORDER-box width,
  // not the content width. If we wrote contentRect.width back to the
  // inline style, each cycle would shrink the element by 2*padding (the
  // "starts good, then shrinks" symptom). borderBoxSize matches the CSS
  // box and stays stable.
  $effect(() => {
    if (!modalEl) return;
    const el = modalEl;
    const ro = new ResizeObserver((entries) => {
      const entry = entries[0];
      if (!entry) return;
      let w: number, h: number;
      if (entry.borderBoxSize && entry.borderBoxSize.length > 0) {
        w = Math.round(entry.borderBoxSize[0].inlineSize);
        h = Math.round(entry.borderBoxSize[0].blockSize);
      } else {
        // Fallback for browsers that don't populate borderBoxSize.
        const rect = el.getBoundingClientRect();
        w = Math.round(rect.width);
        h = Math.round(rect.height);
      }
      // Skip no-op writes to avoid extra render/observer cycles when
      // the size didn't actually change.
      if (w === modalWidth && h === modalHeight) return;
      modalWidth = w;
      modalHeight = h;
      try { localStorage.setItem(sizeKey, JSON.stringify({ w, h })); }
      catch { /* quota — non-fatal */ }
    });
    ro.observe(el);
    return () => ro.disconnect();
  });

  // Inline style covering: enable resize, clamp min/max, apply the
  // user's persisted size, and force flex-column layout. The `display:
  // flex` is critical — daisyUI's `.modal-box` defaults to `display:
  // grid`, which would make our `flex-1` on the table area no-op (the
  // table would shrink to its natural content size, looking "tiny" even
  // when the modal is large). Inline style beats class specificity.
  const modalStyle = $derived(
    [
      'display: flex',
      'flex-direction: column',
      'resize: both',
      'overflow: hidden',
      'min-width: 480px',
      'min-height: 320px',
      'max-width: 99vw',
      'max-height: 99vh',
      modalWidth ? `width: ${modalWidth}px` : 'width: 95vw',
      modalHeight ? `height: ${modalHeight}px` : 'height: 80vh',
    ].join('; ')
  );

  // ---- Keyboard ----------------------------------------------------------

  function onKey(e: KeyboardEvent) {
    if (e.key === 'Escape') {
      e.preventDefault();
      onClose();
    }
  }
</script>

<svelte:window onkeydown={onKey} />

<div class="modal modal-open" role="presentation" onclick={onClose}>
  <!-- Modal box is a flex column so the table area can flex-1 into the
       remaining height. `resize: both` (in modalStyle) gives the user a
       drag handle in the bottom-right corner — captured by the
       ResizeObserver above and persisted per-title to localStorage. -->
  <div
    bind:this={modalEl}
    class="modal-box"
    style={modalStyle}
    role="presentation"
    onclick={(e) => e.stopPropagation()}
  >
    <div class="flex items-center justify-between gap-3 mb-3 shrink-0">
      <h3 class="font-bold text-lg">{title}</h3>
      <div class="flex items-center gap-2">
        {#if onRefresh}
          <button type="button" class="btn btn-sm btn-ghost" onclick={onRefresh} disabled={loading} title="Refresh">
            {#if loading}<span class="loading loading-spinner loading-xs"></span>{/if}
            ↻
          </button>
        {/if}
        <button type="button" class="btn btn-sm btn-ghost" onclick={onClose}>×</button>
      </div>
    </div>

    <div class="flex items-center gap-2 mb-3 shrink-0">
      <input
        type="text"
        class="input input-bordered input-sm flex-1"
        placeholder="Search..."
        bind:value={search}
        autocomplete="off"
      />
      <span class="text-xs text-base-content/60 tabular-nums whitespace-nowrap">
        {filtered.length} of {rows.length}
      </span>
      <span class="text-[10px] text-base-content/40 hidden md:inline">
        Drag column edges or modal corner to resize
      </span>
    </div>

    {#if error}
      <div class="alert alert-error text-sm mb-3 shrink-0">{error}</div>
    {/if}

    {#if rows.length === 0 && !loading}
      <div class="text-sm text-base-content/60 italic p-4">{emptyText}</div>
    {:else}
      <!-- flex-1 + overflow-auto: takes whatever vertical space is left
           after header/search rows, scrolls internally so the modal box's
           own resize boundary stays where the user dragged it. -->
      <div class="flex-1 min-h-0 overflow-auto border border-base-300 rounded">
        <!-- See note in /purge: min-width:100% triggers space
             redistribution that prevents the first column from
             shrinking. Plain width:max-content keeps the table sized to
             exactly the sum of col widths. -->
        <table class="table table-xs table-pin-rows" style="table-layout: fixed; width: max-content;">
          <colgroup>
            {#each columns as col (col.key)}
              <col style="width: {widths[col.key] ?? defaultWidthFor(col)}px" />
            {/each}
          </colgroup>
          <thead>
            <tr>
              {#each columns as col (col.key)}
                {@const dir = sortDir(sortStack, col.key)}
                {@const pos = sortPosition(sortStack, col.key)}
                <th class="text-left whitespace-nowrap relative select-none p-0">
                  <!-- Header text doubles as a sort button. Click cycles
                       primary asc → desc → cleared; shift-click adds the
                       column as a secondary/tertiary sort. -->
                  <button
                    type="button"
                    class="w-full text-left px-3 py-2 hover:bg-base-200 cursor-pointer flex items-center gap-1"
                    title="Click to sort. Shift-click for multi-column sort."
                    onclick={(e) => onHeaderClick(col.key, e)}
                  >
                    <span class="overflow-hidden text-ellipsis flex-1">{col.label}</span>
                    {#if dir}
                      <span class="text-[10px] tabular-nums text-base-content/60 shrink-0">
                        {dir === 'asc' ? '▲' : '▼'}{pos > 1 ? pos : ''}
                      </span>
                    {/if}
                  </button>
                  <!-- Resize handle. Wider hit target than visible width so
                       it's easy to grab. Visible only on hover. -->
                  <button
                    type="button"
                    aria-label={`Resize ${col.label}`}
                    class="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/40 active:bg-primary/60 z-10"
                    onmousedown={(e) => startResize(e, col.key)}
                  ></button>
                </th>
              {/each}
            </tr>
          </thead>
          <tbody>
            {#each filtered as row, i (i)}
              <tr>
                {#each columns as col (col.key)}
                  <td
                    class:font-mono={col.mono}
                    class:break-all={col.wide && col.mono}
                    class:break-words={col.wide && !col.mono}
                    class:whitespace-nowrap={!col.wide}
                    class:overflow-hidden={!col.wide}
                    class:text-ellipsis={!col.wide}
                    class:text-right={col.align === 'right'}
                    class:tabular-nums={col.align === 'right'}
                    class:italic={isEmpty(row, col)}
                    class:opacity-50={isEmpty(row, col)}
                    title={!col.wide && !isEmpty(row, col) ? cellValue(row, col) : undefined}
                  >
                    {cellValue(row, col)}
                  </td>
                {/each}
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
    {/if}
  </div>
</div>

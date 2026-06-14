<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import type { VideoSet } from '$lib/types';
  import {
    applySortClick,
    ariaSort,
    compareBySortStack,
    resizable,
    sortDir,
    sortPosition,
    loadColumnWidths,
    saveColumnWidths,
    type SortEntry,
  } from '$lib/tableUtils.svelte';

  // Sort + width state for the Sources table at the bottom of this page.
  type SourceCol = 'name' | 'path' | 'enabled' | 'status';
  let sourcesSort = $state<SortEntry<SourceCol>[]>([{ col: 'name', dir: 'asc' }]);
  function onSourcesSortClick(col: SourceCol, e: MouseEvent) {
    sourcesSort = applySortClick(sourcesSort, col, e.shiftKey);
  }
  const SOURCES_WIDTHS_KEY = 'config.sources';
  let sourcesWidths = $state<Record<string, number>>(loadColumnWidths(SOURCES_WIDTHS_KEY, {
    name: 200,
    path: 480,
    enabled: 90,
    status: 110,
    actions: 200,
  }));
  // Reassign rather than mutate — see note in /purge: helper-wrapped
  // reads break $state's per-property dependency tracking.
  function setSourcesWidth(col: string, w: number) {
    sourcesWidths = { ...sourcesWidths, [col]: w };
    saveColumnWidths(SOURCES_WIDTHS_KEY, sourcesWidths);
  }
  function getSourcesWidth(col: string, fallback: number): number {
    return sourcesWidths[col] ?? fallback;
  }
  // Explicit table width = sum of column widths. See DataTableModal for
  // why this is required: `width: max-content` resolves from cell
  // min-content, not from the colgroup, and would let column 1 refuse
  // to shrink past its content's natural width.
  const sourcesTotalWidth = $derived(
    getSourcesWidth('name', 200)
    + getSourcesWidth('path', 480)
    + getSourcesWidth('enabled', 90)
    + getSourcesWidth('status', 110)
    + getSourcesWidth('actions', 200)
  );

  // --- Sources (stored as VideoSet records) ---

  let sets = $state<VideoSet[]>([]);
  // Sort applied to the Sources table. Default is name asc; user can
  // override via header clicks.
  const sortedSets = $derived.by(() => {
    if (sourcesSort.length === 0) return sets;
    const cmp = compareBySortStack<VideoSet, SourceCol>(
      {
        name: (s) => s.name,
        path: (s) => s.path,
        enabled: (s) => (s.enabled ? 1 : 0),
        status: (s) => s.path, // No reachable status flag on VideoSet — sort by path as a stable proxy.
      },
      sourcesSort
    );
    return [...sets].sort(cmp);
  });
  let setsLoading = $state(true);
  let setsError = $state<string | null>(null);

  let editing = $state<VideoSet | null>(null);
  let dialogOpen = $state(false);
  let dialogError = $state<string | null>(null);

  // "Copy as path" from Windows Explorer wraps the path in double quotes and
  // uses backslashes. Backslashes are a pain in JSON + URLs, and .NET Path APIs
  // accept forward slashes fine on Windows, so we normalize.
  function normalizePathInput(raw: string): string {
    let s = raw.trim();
    if ((s.startsWith('"') && s.endsWith('"')) || (s.startsWith("'") && s.endsWith("'"))) {
      s = s.slice(1, -1).trim();
    }
    return s.replace(/\\/g, '/');
  }

  // Last folder name from a path: "S:/DMB/Live/2024" → "2024".
  function lastPathSegment(path: string): string {
    const trimmed = path.trim().replace(/[\\/]+$/, '');
    if (!trimmed) return '';
    const parts = trimmed.split(/[\\/]/);
    return parts[parts.length - 1] ?? '';
  }

  function onPathPaste(e: ClipboardEvent) {
    const text = e.clipboardData?.getData('text');
    if (text === undefined || text === null || !editing) return;
    e.preventDefault();
    editing.path = normalizePathInput(text);
    // Auto-fill Name from the final path segment if the user hasn't set one
    // yet. Editing an existing source won't clobber its current name since
    // that field is already populated.
    if (!editing.name.trim()) {
      editing.name = lastPathSegment(editing.path);
    }
  }

  // Flags a Name that collides with another existing source (ignores the
  // source currently being edited, since saving it again is fine).
  const editingNameConflict = $derived(
    editing !== null &&
    editing.name.trim().length > 0 &&
    sets.some(
      (s) => s.id !== editing!.id && s.name.toLowerCase() === editing!.name.trim().toLowerCase()
    )
  );

  let confirmDelete = $state<{ set: VideoSet; orphans: number } | null>(null);

  // Inline rename
  let renamingId = $state<string | null>(null);
  let renameValue = $state('');
  let renameSaving = $state(false);
  let renameError = $state<string | null>(null);
  let renameInput: HTMLInputElement | null = $state(null);

  // Inline path edit. Same shape as the name rename above so the
  // template can mirror the pencil-toggles-input pattern. Path
  // changes are higher-stakes than name changes — every Video row
  // under the old prefix becomes an orphan — so commitPathEdit
  // first probes the orphan count and routes through a confirm
  // modal before actually saving.
  let pathEditingId = $state<string | null>(null);
  let pathEditValue = $state('');
  let pathEditSaving = $state(false);
  let pathEditError = $state<string | null>(null);
  let pathEditInput: HTMLInputElement | null = $state(null);
  // When set, a confirmation modal asks the user whether to
  // proceed with a path change that would orphan N videos. Only
  // shown when count > 0 — a no-orphan rename saves silently.
  let pathChangeConfirm = $state<
    { set: VideoSet; oldPath: string; newPath: string; orphans: number } | null
  >(null);

  function startPathEdit(s: VideoSet) {
    pathEditingId = s.id;
    pathEditValue = s.path;
    pathEditError = null;
    queueMicrotask(() => {
      pathEditInput?.focus();
      pathEditInput?.select();
    });
  }

  function cancelPathEdit() {
    pathEditingId = null;
    pathEditValue = '';
    pathEditError = null;
  }

  // Normalize "Copy as path" pasted from Windows Explorer the
  // same way the create / edit dialog does — strip surrounding
  // quotes, replace backslashes. Inline edit reads the same input
  // shapes the user might paste.
  function onInlinePathPaste(e: ClipboardEvent) {
    const text = e.clipboardData?.getData('text');
    if (text === undefined || text === null) return;
    e.preventDefault();
    pathEditValue = normalizePathInput(text);
  }

  async function commitPathEdit(s: VideoSet) {
    const next = normalizePathInput(pathEditValue).trim();
    if (!next) {
      pathEditError = 'Path is required.';
      return;
    }
    if (next === s.path) {
      cancelPathEdit();
      return;
    }
    pathEditSaving = true;
    pathEditError = null;
    try {
      // Probe orphan count so the confirm modal can quote a
      // number. The endpoint counts Video rows whose FilePath
      // starts with the source's *current* (old) path.
      const { count } = await api.getVideoSetOrphanCount(s.id);
      if (count > 0) {
        pathChangeConfirm = { set: s, oldPath: s.path, newPath: next, orphans: count };
        return; // doSavePath fires from the modal's Confirm button
      }
      // No orphans — save silently.
      await doSavePath(s, next);
    } catch (e) {
      pathEditError = e instanceof Error ? e.message : String(e);
    } finally {
      pathEditSaving = false;
    }
  }

  // Actual API write. Split out so the no-orphan fast path and
  // the confirmed-orphan path can both reach it.
  async function doSavePath(s: VideoSet, newPath: string) {
    pathEditSaving = true;
    pathEditError = null;
    try {
      await api.updateVideoSet(s.id, {
        id: s.id,
        name: s.name,
        path: newPath,
        enabled: s.enabled,
        sortOrder: s.sortOrder
      });
      await loadSets();
      cancelPathEdit();
      pathChangeConfirm = null;
    } catch (e) {
      pathEditError = e instanceof Error ? e.message : String(e);
    } finally {
      pathEditSaving = false;
    }
  }

  async function confirmPathChange() {
    const c = pathChangeConfirm;
    if (!c) return;
    await doSavePath(c.set, c.newPath);
  }
  function cancelPathChangeConfirm() {
    if (pathEditSaving) return;
    pathChangeConfirm = null;
  }

  function onPathEditKey(e: KeyboardEvent, s: VideoSet) {
    if (e.key === 'Enter') {
      e.preventDefault();
      void commitPathEdit(s);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      cancelPathEdit();
    }
  }

  function startRename(s: VideoSet) {
    renamingId = s.id;
    renameValue = s.name;
    renameError = null;
    // Focus + select after the input is in the DOM.
    queueMicrotask(() => {
      renameInput?.focus();
      renameInput?.select();
    });
  }

  function cancelRename() {
    renamingId = null;
    renameValue = '';
    renameError = null;
  }

  async function commitRename(s: VideoSet) {
    const next = renameValue.trim();
    if (!next) {
      renameError = 'Name is required.';
      return;
    }
    if (next === s.name) {
      cancelRename();
      return;
    }
    renameSaving = true;
    renameError = null;
    try {
      await api.updateVideoSet(s.id, {
        id: s.id,
        name: next,
        path: s.path,
        enabled: s.enabled,
        sortOrder: s.sortOrder
      });
      await loadSets();
      cancelRename();
    } catch (e) {
      renameError = e instanceof Error ? e.message : String(e);
    } finally {
      renameSaving = false;
    }
  }

  function onRenameKey(e: KeyboardEvent, s: VideoSet) {
    if (e.key === 'Enter') {
      e.preventDefault();
      void commitRename(s);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      cancelRename();
    }
  }

  function emptyDraft(): VideoSet {
    return {
      id: crypto.randomUUID(),
      name: '',
      path: '',
      enabled: true,
      sortOrder: sets.length
    };
  }

  async function loadSets() {
    setsLoading = true;
    setsError = null;
    try {
      sets = await api.listVideoSets();
    } catch (e) {
      setsError = e instanceof Error ? e.message : String(e);
    } finally {
      setsLoading = false;
    }
  }

  onMount(() => {
    loadSets();
  });

  function openAdd() {
    editing = emptyDraft();
    dialogError = null;
    dialogOpen = true;
  }

  function openEdit(s: VideoSet) {
    editing = { ...s };
    dialogError = null;
    dialogOpen = true;
  }

  function closeDialog() {
    dialogOpen = false;
    editing = null;
    dialogError = null;
  }

  async function saveSet() {
    if (!editing) return;
    dialogError = null;

    if (editingNameConflict) {
      dialogError = `A source named "${editing.name.trim()}" already exists. Please choose a different name.`;
      return;
    }

    if (!editing.name.trim()) {
      dialogError = 'Name is required.';
      return;
    }
    if (!editing.path.trim()) {
      dialogError = 'Path is required.';
      return;
    }

    const body = {
      id: editing.id,
      name: editing.name.trim(),
      path: editing.path.trim(),
      enabled: editing.enabled,
      sortOrder: editing.sortOrder
    };

    try {
      const existing = sets.find((s) => s.id === editing!.id);
      if (existing) {
        await api.updateVideoSet(editing.id, body);
      } else {
        await api.createVideoSet(body);
      }
      await loadSets();
      closeDialog();
    } catch (e) {
      dialogError = e instanceof Error ? e.message : String(e);
    }
  }

  async function requestDelete(s: VideoSet) {
    try {
      const { count } = await api.getVideoSetOrphanCount(s.id);
      confirmDelete = { set: s, orphans: count };
    } catch (e) {
      setsError = e instanceof Error ? e.message : String(e);
    }
  }

  async function doDelete() {
    if (!confirmDelete) return;
    try {
      await api.deleteVideoSet(confirmDelete.set.id, confirmDelete.orphans > 0);
      confirmDelete = null;
      await loadSets();
    } catch (e) {
      setsError = e instanceof Error ? e.message : String(e);
      confirmDelete = null;
    }
  }

  async function toggleEnabled(s: VideoSet) {
    try {
      await api.updateVideoSet(s.id, {
        id: s.id,
        name: s.name,
        path: s.path,
        enabled: !s.enabled,
        sortOrder: s.sortOrder
      });
      await loadSets();
    } catch (e) {
      setsError = e instanceof Error ? e.message : String(e);
    }
  }
</script>

<div class="max-w-4xl p-6 space-y-10">
  <!-- Sources — the configured VideoSet roots the library imports from.
       Split out of the old Configuration page into its own page (issue #67). -->
  <section>
    <div class="flex items-center justify-between mb-4">
      <h1 class="text-2xl font-semibold">Sources</h1>
      <button type="button" class="btn btn-sm btn-soft btn-primary btn-cta" onclick={openAdd}>Add Source</button>
    </div>

    <p class="text-sm text-base-content/70 mb-4">
      Each set maps a host directory to a Docker path. The Docker path must be mounted into the
      container via <code>docker-compose.yml</code>. Disabled sets are hidden from the player
      and cannot be browsed or streamed.
    </p>

    {#if setsError}<div class="alert alert-error text-sm mb-3">{setsError}</div>{/if}

    {#if setsLoading}
      <div class="flex items-center gap-2 text-base-content/70">
        <span class="loading loading-spinner loading-sm"></span> Loading...
      </div>
    {:else if sets.length === 0}
      <div class="text-base-content/60 italic">No sources configured.</div>
    {:else}
      <div class="overflow-x-auto">
        <!-- See note in /purge: dropping min-width:100% prevents the
             browser from redistributing leftover space and making the
             first column appear unresizable to the left. -->
        <table class="table table-sm resizable-table" style="table-layout: fixed; width: {sourcesTotalWidth}px;">
          <colgroup>
            <col style="width: {getSourcesWidth('name', 200)}px" />
            <col style="width: {getSourcesWidth('path', 480)}px" />
            <col style="width: {getSourcesWidth('enabled', 90)}px" />
            <col style="width: {getSourcesWidth('status', 110)}px" />
            <col style="width: {getSourcesWidth('actions', 200)}px" />
          </colgroup>
          <thead>
            <tr>
              {#each [
                { key: 'name' as const, label: 'Name', sortable: true, def: 200 },
                { key: 'path' as const, label: 'Path', sortable: true, def: 480 },
                { key: 'enabled' as const, label: 'Enabled', sortable: true, def: 90 },
                { key: 'status' as const, label: 'Status', sortable: true, def: 110 },
              ] as col (col.key)}
                <th
                  class="relative select-none p-0"
                  style="width: {getSourcesWidth(col.key, col.def)}px;"
                  aria-sort={ariaSort(sourcesSort, col.key)}
                >
                  <button
                    type="button"
                    class="w-full text-left px-3 py-2 hover:bg-base-200 cursor-pointer flex items-center gap-1"
                    onclick={(e) => onSourcesSortClick(col.key, e)}
                    title="Click to sort. Shift-click for multi-column sort. Double-click the right edge to auto-fit."
                  >
                    <span class="overflow-hidden text-ellipsis flex-1">{col.label}</span>
                    {#if sortDir(sourcesSort, col.key)}
                      <span class="text-[10px] tabular-nums text-base-content/60">
                        {sortDir(sourcesSort, col.key) === 'asc' ? '▲' : '▼'}{sortPosition(sourcesSort, col.key) > 1 ? sortPosition(sourcesSort, col.key) : ''}
                      </span>
                    {/if}
                  </button>
                  <button
                    type="button"
                    aria-label={`Resize ${col.label} (double-click to auto-fit)`}
                    class="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/40 active:bg-primary/60 z-10"
                    use:resizable={{
                      getWidth: () => getSourcesWidth(col.key, col.def),
                      setWidth: (w) => setSourcesWidth(col.key, w),
                    }}
                  ></button>
                </th>
              {/each}
              <th
                class="relative select-none text-right"
                style="width: {getSourcesWidth('actions', 200)}px;"
              >
                Actions
                <button
                  type="button"
                  aria-label="Resize Actions (double-click to auto-fit)"
                  class="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/40 active:bg-primary/60 z-10"
                  use:resizable={{
                    getWidth: () => getSourcesWidth('actions', 200),
                    setWidth: (w) => setSourcesWidth('actions', w),
                  }}
                ></button>
              </th>
            </tr>
          </thead>
          <tbody>
            {#each sortedSets as s (s.id)}
              <tr>
                <td class="font-medium">
                  {#if renamingId === s.id}
                    <div class="flex items-center gap-1">
                      <input
                        bind:this={renameInput}
                        class="input input-bordered input-xs w-40"
                        bind:value={renameValue}
                        onkeydown={(e) => onRenameKey(e, s)}
                        disabled={renameSaving}
                      />
                      <button
                        type="button"
                        class="btn btn-xs btn-soft btn-primary btn-cta"
                        onclick={() => commitRename(s)}
                        disabled={renameSaving}
                      >
                        {#if renameSaving}<span class="loading loading-spinner loading-xs"></span>{/if}
                        Save
                      </button>
                      <button
                        type="button"
                        class="btn btn-xs btn-cancel"
                        onclick={cancelRename}
                        disabled={renameSaving}
                      >Cancel</button>
                    </div>
                    {#if renameError}<div class="text-error text-xs mt-1">{renameError}</div>{/if}
                  {:else}
                    <button
                      type="button"
                      class="inline-flex items-center gap-1 hover:text-primary"
                      onclick={() => startRename(s)}
                      title="Rename"
                    >
                      <!-- Disabled sources read with strike-through +
                           italic "(Disabled)" suffix everywhere they
                           appear in the UI; this matches the
                           treatment in the browse Sources tree, the
                           data-validation tables, and the import
                           breadcrumbs. -->
                      <span class={s.enabled ? '' : 'line-through text-base-content/60'}>{s.name}</span>
                      {#if !s.enabled}
                        <span class="text-xs italic text-base-content/50 no-underline">(Disabled)</span>
                      {/if}
                      <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" class="w-3 h-3 fill-current opacity-60">
                        <path d="M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04a1 1 0 0 0 0-1.41l-2.34-2.34a1 1 0 0 0-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z" />
                      </svg>
                    </button>
                  {/if}
                </td>
                <td class="font-mono text-xs break-all">
                  {#if pathEditingId === s.id}
                    <div class="flex items-start gap-1">
                      <input
                        bind:this={pathEditInput}
                        class="input input-bordered input-xs flex-1 font-mono"
                        bind:value={pathEditValue}
                        onpaste={onInlinePathPaste}
                        onkeydown={(e) => onPathEditKey(e, s)}
                        disabled={pathEditSaving}
                        placeholder="C:/MyVideos or /mnt/videos"
                      />
                      <button
                        type="button"
                        class="btn btn-xs btn-soft btn-primary btn-cta shrink-0"
                        onclick={() => commitPathEdit(s)}
                        disabled={pathEditSaving}
                      >
                        {#if pathEditSaving}<span class="loading loading-spinner loading-xs"></span>{/if}
                        Save
                      </button>
                      <button
                        type="button"
                        class="btn btn-xs btn-cancel shrink-0"
                        onclick={cancelPathEdit}
                        disabled={pathEditSaving}
                      >Cancel</button>
                    </div>
                    {#if pathEditError}<div class="text-error text-xs mt-1">{pathEditError}</div>{/if}
                  {:else}
                    <!-- Same pencil-icon affordance as the name
                         column so users discover both fields are
                         editable inline without going through the
                         Edit dialog. -->
                    <button
                      type="button"
                      class="inline-flex items-center gap-1 hover:text-primary text-left"
                      onclick={() => startPathEdit(s)}
                      title="Edit path"
                    >
                      <span class="break-all">{s.path}</span>
                      <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" class="w-3 h-3 fill-current opacity-60 shrink-0">
                        <path d="M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04a1 1 0 0 0 0-1.41l-2.34-2.34a1 1 0 0 0-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z" />
                      </svg>
                    </button>
                  {/if}
                </td>
                <td>
                  <input
                    type="checkbox"
                    class="toggle toggle-sm toggle-primary"
                    checked={s.enabled}
                    onchange={() => toggleEnabled(s)}
                  />
                </td>
                <td>
                  {#if s.pathExists}
                    <span class="badge badge-success badge-sm">readable</span>
                  {:else}
                    <span class="badge badge-warning badge-sm" title="Directory not found in container">
                      not found
                    </span>
                  {/if}
                </td>
                <td class="text-right space-x-1">
                  <button type="button" class="btn btn-xs btn-soft btn-accent border border-accent/50" onclick={() => openEdit(s)}>Edit</button>
                  <button type="button" class="btn btn-xs btn-soft btn-error border border-error/50" onclick={() => requestDelete(s)}>
                    Delete
                  </button>
                </td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
    {/if}
  </section>
</div>

<!-- ========= Add/Edit Dialog ========= -->
{#if dialogOpen && editing}
  {@const editingSet = editing}
  <div class="modal modal-open">
    <div class="modal-box space-y-4">
      <h3 class="text-lg font-bold">
        {sets.some((s) => s.id === editingSet.id) ? 'Edit Source' : 'Add Source'}
      </h3>

      <div>
        <label class="label" for="vs-name">
          <span class="label-text">Name</span>
          {#if editingNameConflict}
            <span class="label-text-alt text-error">A source with this name already exists</span>
          {/if}
        </label>
        <input
          id="vs-name"
          class="input input-bordered w-full {editingNameConflict ? 'input-error text-error' : ''}"
          bind:value={editing.name}
          placeholder="vids1"
        />
      </div>

      <div>
        <label class="label" for="vs-path">
          <span class="label-text">Path</span>
          <span class="label-text-alt text-base-content/60">
            Absolute path the API reads video files from
          </span>
        </label>
        <input
          id="vs-path"
          class="input input-bordered w-full font-mono text-sm"
          bind:value={editing.path}
          onpaste={onPathPaste}
          placeholder="C:/MyVideos or /mnt/videos"
        />
      </div>

      <div class="flex items-center gap-4">
        <label class="label cursor-pointer gap-2">
          <input type="checkbox" class="checkbox" bind:checked={editing.enabled} />
          <span class="label-text">Enabled</span>
        </label>

        <label class="label gap-2" for="vs-sort">
          <span class="label-text">Sort</span>
          <input
            id="vs-sort"
            type="number"
            class="input input-bordered input-sm w-20"
            bind:value={editing.sortOrder}
          />
        </label>
      </div>

      {#if dialogError}<div class="alert alert-error text-sm">{dialogError}</div>{/if}

      <div class="modal-action">
        <button type="button" class="btn btn-soft btn-primary btn-cta" onclick={saveSet}>Save</button>
        <button type="button" class="btn btn-cancel" onclick={closeDialog}>Cancel</button>
      </div>
    </div>
  </div>
{/if}

<!-- ========= Delete Confirm ========= -->
{#if confirmDelete}
  <div class="modal modal-open">
    <div class="modal-box space-y-3">
      <h3 class="text-lg font-bold">Delete Source</h3>
      <p>
        Delete
        <span class="font-semibold {confirmDelete.set.enabled ? '' : 'line-through text-base-content/60'}"
        >{confirmDelete.set.name}</span>{#if !confirmDelete.set.enabled}<span class="text-xs italic text-base-content/50 ml-1">(Disabled)</span>{/if}?
      </p>
      {#if confirmDelete.orphans > 0}
        <div class="alert alert-warning text-sm">
          This will orphan <span class="font-semibold">{confirmDelete.orphans}</span>
          {confirmDelete.orphans === 1 ? 'video' : 'videos'} whose paths are under
          <code>{confirmDelete.set.path}</code>. The video records will remain in the
          database but will not stream or appear in listings until another set covers them.
        </div>
      {/if}
      <div class="modal-action">
        <button type="button" class="btn btn-soft btn-error border border-error/50" onclick={doDelete}>Delete</button>
        <button type="button" class="btn btn-cancel" onclick={() => (confirmDelete = null)}>Cancel</button>
      </div>
    </div>
  </div>
{/if}

<!-- ========= Path-change orphan warning =========
     Shown when an inline path edit would leave Video rows pointing
     at a prefix that no source covers anymore. The save still
     succeeds (the API doesn't block path changes), but the orphans
     stop being browsable / streamable until another source covers
     them or their FilePaths are rewritten. Surfacing the count up-
     front lets the user back out before the change lands. -->
{#if pathChangeConfirm}
  {@const c = pathChangeConfirm}
  <div class="modal modal-open" role="dialog" aria-modal="true" aria-labelledby="path-change-title">
    <div class="modal-box space-y-3">
      <h3 id="path-change-title" class="text-lg font-bold">Change source path?</h3>
      <p class="text-sm text-base-content/80">
        Update
        <span class="font-semibold {c.set.enabled ? '' : 'line-through text-base-content/60'}"
        >{c.set.name}</span>{#if !c.set.enabled}<span class="text-xs italic text-base-content/50 ml-1">(Disabled)</span>{/if}?
      </p>
      <div class="text-xs space-y-1">
        <div><span class="text-base-content/60">From:</span> <code class="font-mono">{c.oldPath}</code></div>
        <div><span class="text-base-content/60">To:</span> <code class="font-mono">{c.newPath}</code></div>
      </div>
      <div class="alert alert-warning text-sm">
        This will orphan <span class="font-semibold">{c.orphans}</span>
        {c.orphans === 1 ? 'video' : 'videos'} whose paths are under
        <code>{c.oldPath}</code>. The video records remain in the
        database but won't stream or appear in listings until another
        source covers them.
      </div>
      <div class="modal-action">
        <!-- Cancel autofocused so accidental Enter dismisses
             instead of orphaning N videos. -->
        <!-- svelte-ignore a11y_autofocus -->
        <button
          type="button"
          class="btn btn-cancel"
          onclick={cancelPathChangeConfirm}
          disabled={pathEditSaving}
          autofocus
        >Cancel</button>
        <button
          type="button"
          class="btn btn-soft btn-warning border border-warning/50"
          onclick={confirmPathChange}
          disabled={pathEditSaving}
        >
          {#if pathEditSaving}<span class="loading loading-spinner loading-xs"></span>{/if}
          Change path
        </button>
      </div>
    </div>
  </div>
{/if}

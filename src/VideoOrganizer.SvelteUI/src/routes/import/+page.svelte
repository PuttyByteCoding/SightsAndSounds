<script lang="ts">
  import { onMount } from 'svelte';
  import { api, ApiError } from '$lib/api';
  import type {
    ImportBrowseDirectory,
    DirectoryImportRequest,
    VideoSet
  } from '$lib/types';
  import { breadcrumbs } from '$lib/pathHelpers';

  // --- State ----------------------------------------------------------------

  let sets = $state<VideoSet[]>([]);
  let videoCount = $state<number | null>(null);
  let formError = $state<string | null>(null);
  let formSuccess = $state<string | null>(null);

  // Folder tree
  let treeRoots = $state<ImportBrowseDirectory[]>([]);
  let treeChildrenByPath = $state<Map<string, ImportBrowseDirectory[]>>(new Map());
  let treeExpandedPaths = $state<Set<string>>(new Set());
  let treeLoadingPaths = $state<Set<string>>(new Set());
  let treeInitialLoading = $state(true);

  // Form
  let selectedPath = $state('');
  let includeSubdirectories = $state(true);
  let importName = $state('');
  // Generic tag picker — IDs of tags to apply to every newly-imported video.
  let initialTagIds = $state<string[]>([]);
  let initialTagPickerInput = $state('');
  let initialTagSuggestions = $state<{ tagId: string; name: string; tagGroupName: string }[]>([]);
  let initialTagLabels = $state<Record<string, string>>({});
  let importNotes = $state('');

  // File list
  let fileList = $state<string[]>([]);
  let nonImportableFileList = $state<string[]>([]);
  let importedFilesSet = $state<Set<string>>(new Set());
  let folderSelected = $state(false);
  let filesLoading = $state(false);

  // File-list UX
  let fileSearch = $state('');
  let showThumbnails = $state(true);
  // Three-tab segmented view: which subset of the folder's files is shown.
  // 'pending' = video files not yet in the DB (the actionable bucket).
  // 'imported' = videos already imported (won't be re-imported).
  // 'other'    = non-video files (images, .nfo, etc.).
  let activeFileTab = $state<'pending' | 'imported' | 'other'>('pending');

  // Collapsibles
  let showTagsSection = $state(true);

  // Import
  // Just the in-flight POST flag — we no longer track per-file progress
  // here. Live status lives on the Background Tasks page.
  let importing = $state(false);

  // --- Derived --------------------------------------------------------------

  interface TreeRow {
    dir: ImportBrowseDirectory;
    depth: number;
    expanded: boolean;
    loading: boolean;
    selected: boolean;
  }

  const treeRows = $derived.by(() => {
    const rows: TreeRow[] = [];
    const walk = (dirs: ImportBrowseDirectory[], depth: number) => {
      for (const d of dirs) {
        const expanded = treeExpandedPaths.has(d.fullPath);
        rows.push({
          dir: d,
          depth,
          expanded,
          loading: treeLoadingPaths.has(d.fullPath),
          selected: selectedPath === d.fullPath
        });
        if (expanded) {
          const children = treeChildrenByPath.get(d.fullPath);
          if (children) walk(children, depth + 1);
        }
      }
    };
    walk(treeRoots, 0);
    return rows;
  });

  const crumbs = $derived(selectedPath ? breadcrumbs(selectedPath, sets) : []);

  // Three buckets, computed once and reused for tab counts + the active list.
  const pendingFiles = $derived(
    fileList.filter((f) => !importedFilesSet.has(f))
  );
  const alreadyImportedFiles = $derived(
    fileList.filter((f) => importedFilesSet.has(f))
  );
  // nonImportableFileList already holds non-video files as-is.

  // Active tab's filtered view. The search box filters the currently-active
  // bucket — type "trailer" while on Pending and you only see pending
  // videos with "trailer" in their name.
  const visibleFiles = $derived.by(() => {
    const q = fileSearch.trim().toLowerCase();
    const source = activeFileTab === 'pending'
      ? pendingFiles
      : activeFileTab === 'imported'
        ? alreadyImportedFiles
        : nonImportableFileList;
    if (q.length === 0) return source;
    return source.filter((f) => f.toLowerCase().includes(q));
  });

  const importableCount = $derived(pendingFiles.length);
  const hasImportableFiles = $derived(importableCount > 0);

  // --- Lifecycle ------------------------------------------------------------

  onMount(async () => {
    try {
      sets = await api.listVideoSets();
    } catch (e) {
      formError = toMessage('Failed to load video sets', e);
    }

    try {
      videoCount = await api.getVideoCount();
    } catch (e) {
      formError = toMessage('Failed to load video count', e);
    }

    await loadTreeRoot();
  });

  // --- Add-dataset modal ----------------------------------------------------

  let showAddSetDialog = $state(false);
  let addSetName = $state('');
  let addSetPath = $state('');
  let addSetSaving = $state(false);
  let addSetError = $state<string | null>(null);

  function openAddSetDialog() {
    addSetName = '';
    addSetPath = '';
    addSetError = null;
    showAddSetDialog = true;
  }
  function closeAddSetDialog() {
    showAddSetDialog = false;
  }

  // Matches the Config-page paste normalization: strips surrounding quotes
  // that Windows "Copy as path" adds, and converts backslashes to forward
  // slashes so the path lines up with VideoSet.Path canonicalization.
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

  function onAddSetPathPaste(e: ClipboardEvent) {
    const text = e.clipboardData?.getData('text');
    if (text === undefined || text === null) return;
    e.preventDefault();
    addSetPath = normalizePathInput(text);
    // Auto-fill Name from the final path segment if the user hasn't already
    // typed one. They can still overwrite it before saving.
    if (!addSetName.trim()) {
      addSetName = lastPathSegment(addSetPath);
    }
  }

  // True when the typed Name matches an existing source (case-insensitive).
  // Drives the red "already in use" hint so the user has to rename before
  // saving — the server enforces this too but the inline signal is friendlier.
  const addSetNameConflict = $derived(
    addSetName.trim().length > 0 &&
    sets.some((s) => s.name.toLowerCase() === addSetName.trim().toLowerCase())
  );

  async function saveAddSet() {
    addSetError = null;
    const name = addSetName.trim();
    const path = addSetPath.trim();
    if (!name) { addSetError = 'Name is required.'; return; }
    if (!path) { addSetError = 'Path is required.'; return; }
    if (addSetNameConflict) { addSetError = `A source named "${name}" already exists. Please choose a different name.`; return; }

    addSetSaving = true;
    try {
      // New sets come in at the end of the list. Enabled by default so they
      // show up in the tree immediately.
      const nextOrder = sets.length > 0 ? Math.max(...sets.map((s) => s.sortOrder)) + 1 : 0;
      await api.createVideoSet({
        id: crypto.randomUUID(),
        name,
        path,
        enabled: true,
        sortOrder: nextOrder
      });
      // Clear the form + close the dialog BEFORE the sets list refresh so
      // the conflict-check derived doesn't momentarily flag the just-created
      // source and turn the Name field red.
      closeAddSetDialog();
      sets = await api.listVideoSets();
      await loadTreeRoot();
    } catch (e) {
      addSetError = e instanceof Error ? e.message : String(e);
    } finally {
      addSetSaving = false;
    }
  }

  // --- Helpers --------------------------------------------------------------

  function toMessage(prefix: string, err: unknown): string {
    if (err instanceof ApiError) return `${prefix}: ${err.message}`;
    if (err instanceof Error) return `${prefix}: ${err.message}`;
    return `${prefix}: ${String(err)}`;
  }

  async function loadTreeRoot() {
    treeInitialLoading = true;
    formError = null;
    try {
      const res = await api.browseImport(null);
      treeRoots = res.directories;
    } catch (e) {
      formError = toMessage('Failed to load sets', e);
    } finally {
      treeInitialLoading = false;
    }
  }

  async function fetchChildren(path: string) {
    if (treeChildrenByPath.has(path)) return;
    treeLoadingPaths = new Set(treeLoadingPaths).add(path);
    try {
      const res = await api.browseImport(path);
      const next = new Map(treeChildrenByPath);
      next.set(path, res.directories);
      treeChildrenByPath = next;
    } catch (e) {
      formError = toMessage('Failed to browse', e);
    } finally {
      const next = new Set(treeLoadingPaths);
      next.delete(path);
      treeLoadingPaths = next;
    }
  }

  async function toggleExpand(dir: ImportBrowseDirectory) {
    const expanded = new Set(treeExpandedPaths);
    if (expanded.has(dir.fullPath)) {
      expanded.delete(dir.fullPath);
      treeExpandedPaths = expanded;
    } else {
      expanded.add(dir.fullPath);
      treeExpandedPaths = expanded;
      if (dir.hasSubdirectories) await fetchChildren(dir.fullPath);
    }
  }

  async function selectFolder(dir: ImportBrowseDirectory) {
    selectedPath = dir.fullPath;
    folderSelected = true;
    // Auto-fill the import name as "{source} - {folder leaf}" — best
    // first guess for what to call the job. The user can edit before
    // clicking Import.
    importName = computeDefaultImportName(dir.fullPath);

    // Auto-expand on select so the user sees descent structure.
    if (dir.hasSubdirectories && !treeExpandedPaths.has(dir.fullPath)) {
      const expanded = new Set(treeExpandedPaths);
      expanded.add(dir.fullPath);
      treeExpandedPaths = expanded;
      await fetchChildren(dir.fullPath);
    }

    await loadFiles();
  }

  // "{source name} - {folder leaf}" if the path is inside a known VideoSet;
  // otherwise just the folder leaf. Used to seed the Name input.
  function computeDefaultImportName(fullPath: string): string {
    const norm = fullPath.replace(/\\/g, '/').replace(/\/+$/, '');
    const leaf = norm.split('/').filter(Boolean).at(-1) ?? '';
    // Match the longest VideoSet path prefix so nested sets resolve correctly.
    const set = sets
      .slice()
      .sort((a, b) => b.path.length - a.path.length)
      .find((s) => {
        const sp = s.path.replace(/\\/g, '/').replace(/\/+$/, '');
        return norm === sp || norm.startsWith(sp + '/');
      });
    if (!set) return leaf;
    const setNorm = set.path.replace(/\\/g, '/').replace(/\/+$/, '');
    // If the user picked the source root itself, use just the source name.
    if (norm === setNorm) return set.name;
    return `${set.name} - ${leaf}`;
  }

  async function loadFiles() {
    if (!selectedPath) return;
    filesLoading = true;
    try {
      fileList = [];
      nonImportableFileList = [];
      importedFilesSet = new Set();
      formError = null;
      const res = await api.getImportFiles(selectedPath, includeSubdirectories);
      fileList = res.files;
      nonImportableFileList = res.nonImportableFiles;
      importedFilesSet = new Set(res.importedFiles);
    } catch (e) {
      formError = toMessage('Failed to list files', e);
    } finally {
      filesLoading = false;
    }
  }

  // Refresh the file list when the user toggles "Include subdirectories"
  // so the table immediately reflects what would actually be imported.
  // Skip while no folder is selected to avoid a needless 4xx on mount.
  $effect(() => {
    // Track both deps so $effect picks up the toggle.
    void includeSubdirectories;
    if (selectedPath) {
      void loadFiles();
    }
  });

  // (Alt+Numpad flag toggles removed — flags are now generic tags.)

  // Fire-and-forget import. Posts the request, surfaces a brief success
  // toast, then immediately re-enables the form so the user can pick
  // another folder and click Import again. Live progress lives on the
  // Background Tasks page — we don't poll here anymore.
  let successClearTimer: ReturnType<typeof setTimeout> | null = null;
  async function handleImport() {
    if (!selectedPath.trim()) {
      formError = 'Pick a folder from the tree first.';
      return;
    }

    importing = true;
    formError = null;
    formSuccess = null;
    if (successClearTimer) { clearTimeout(successClearTimer); successClearTimer = null; }

    const request: DirectoryImportRequest = {
      directoryPath: selectedPath,
      includeSubdirectories,
      name: importName.trim().length > 0 ? importName.trim() : null,
      initialTagIds: initialTagIds.length > 0 ? initialTagIds : null,
      notes: importNotes.trim().length > 0 ? importNotes.trim() : null
    };

    try {
      await api.startImport(request);
      formSuccess = `Import started: "${request.name ?? selectedPath}". Running in the background — view progress on Background Tasks.`;
      // Auto-clear so chained imports don't pile up stale messages.
      successClearTimer = setTimeout(() => { formSuccess = null; }, 6000);
      // Refresh the totals counter; the file list is intentionally NOT
      // reloaded since the import is still in flight server-side.
      void refreshVideoCount();
    } catch (e) {
      formError = toMessage('Import failed', e);
    } finally {
      importing = false;
    }
  }

  async function refreshVideoCount() {
    try {
      videoCount = await api.getVideoCount();
    } catch {
      /* non-fatal */
    }
  }
</script>

<div class="space-y-4">
  <h1 class="text-2xl font-semibold">
    Import Tool {videoCount !== null ? `(${videoCount} Videos)` : ''}
  </h1>

  <div class="grid gap-4 lg:grid-cols-[340px_1fr]">
    <!-- ============ Left pane: folder tree ============ -->
    <aside class="card bg-base-200 p-3 h-fit lg:sticky lg:top-4 lg:max-h-[calc(100vh-6rem)] lg:overflow-auto">
      <div class="text-sm font-medium mb-2 flex items-center justify-between">
        <span>Sources</span>
        <div class="flex items-center gap-1">
          <button type="button" class="btn btn-xs btn-soft btn-primary btn-cta" onclick={openAddSetDialog} title="Add source">+ Source</button>
          <button type="button" class="btn btn-ghost btn-xs" onclick={loadTreeRoot} title="Reload">↻</button>
        </div>
      </div>

      {#if treeInitialLoading}
        <div class="flex items-center gap-2 text-base-content/70 p-2">
          <span class="loading loading-spinner loading-sm"></span> Loading...
        </div>
      {:else if treeRoots.length === 0}
        <div class="text-base-content/60 italic text-sm p-2 space-y-2">
          <div>No enabled sources.</div>
          <button type="button" class="btn btn-sm btn-soft btn-primary btn-cta" onclick={openAddSetDialog}>
            + Add Source
          </button>
        </div>
      {:else}
        <ul class="space-y-0.5 text-sm">
          {#each treeRows as row (row.dir.fullPath)}
            {@const isFullyImported = row.dir.videoCount > 0 && row.dir.importedCount >= row.dir.videoCount}
            <li>
              <div
                class="flex items-center gap-1 rounded hover:bg-base-300 pr-1"
                class:bg-base-300={row.selected}
                style="padding-left: {row.depth * 12}px"
              >
                <button
                  type="button"
                  class="w-5 h-5 flex items-center justify-center text-base-content/60 shrink-0"
                  onclick={() => toggleExpand(row.dir)}
                  aria-label={row.expanded ? 'Collapse' : 'Expand'}
                >
                  {#if row.loading}
                    <span class="loading loading-spinner loading-xs"></span>
                  {:else if row.dir.hasSubdirectories}
                    <svg
                      xmlns="http://www.w3.org/2000/svg"
                      viewBox="0 0 24 24"
                      class="h-4 w-4 fill-current transition-transform duration-150 {row.expanded ? 'rotate-90' : ''}"
                      aria-hidden="true"
                    >
                      <path d="M9 6l6 6-6 6V6z" />
                    </svg>
                  {:else}
                    <span class="opacity-30">·</span>
                  {/if}
                </button>
                <button
                  type="button"
                  class="flex-1 min-w-0 text-left py-1 px-1"
                  class:opacity-60={isFullyImported}
                  onclick={() => selectFolder(row.dir)}
                  title={row.dir.fullPath}
                >
                  <div class="truncate">{row.dir.name}</div>
                  {#if row.depth === 0}
                    <div class="truncate text-xs text-base-content/55 font-normal leading-tight">
                      {row.dir.fullPath}
                    </div>
                  {/if}
                </button>
                {#if row.dir.videoCount > 0}
                  <span
                    class="badge badge-xs shrink-0 font-bold tabular-nums {isFullyImported
                      ? 'bg-success/25 text-success border-success/30'
                      : row.dir.importedCount > 0
                        ? 'badge-warning'
                        : 'badge-ghost'}"
                    title="{row.dir.importedCount} of {row.dir.videoCount} imported"
                  >
                    {row.dir.importedCount}/{row.dir.videoCount}
                  </span>
                {/if}
              </div>
            </li>
          {/each}
        </ul>
      {/if}
    </aside>

    <!-- ============ Right pane: details ============ -->
    <div class="space-y-4 min-w-0">
      <!-- Breadcrumbs + paths -->
      <section class="card bg-base-200 p-3 space-y-2">
        {#if crumbs.length > 0}
          <div class="text-sm">
            {#each crumbs as crumb, ci (ci)}
              {#if ci === crumbs.length - 1}
                <span class="font-semibold">{crumb.name}</span>
              {:else}
                <span class="text-base-content/60">{crumb.name}</span>
                <span class="text-base-content/50 mx-1">/</span>
              {/if}
            {/each}
          </div>
          <div class="text-xs text-base-content/70 font-mono break-all">{selectedPath}</div>
        {:else}
          <div class="text-sm text-base-content/60 italic">Pick a folder from the tree on the left.</div>
        {/if}
      </section>

      {#if formError}<div class="alert alert-error text-sm">{formError}</div>{/if}
      {#if formSuccess}<div class="alert alert-success text-sm">{formSuccess}</div>{/if}

      <!-- Import options. MD5 hashing happens in the background after import
           (see Md5BackfillService), so there's no Quick/Full split anymore. -->
      <section class="card bg-base-200 p-4 space-y-3">
        <div class="text-sm font-medium">Options</div>
        <!-- Display label for the job. Auto-filled as
             "{source name} - {folder name}" when the user picks a folder.
             Editable; shown on the Background Tasks Imports list. -->
        <div>
          <label for="import-name" class="label py-1">
            <span class="label-text text-sm">Import name</span>
          </label>
          <input
            id="import-name"
            type="text"
            class="input input-bordered input-sm w-full"
            placeholder="e.g. Source - Folder"
            bind:value={importName}
            autocomplete="off"
          />
        </div>
        <div class="flex gap-4 flex-wrap">
          <label class="label cursor-pointer gap-2">
            <input type="checkbox" class="checkbox checkbox-sm" bind:checked={includeSubdirectories} />
            <span class="label-text">Include subdirectories</span>
          </label>
        </div>
        <div class="text-xs text-base-content/60">
          MD5 hashes are computed in the background after import. Duplicate detection
          by MD5 happens once the hash is filled in. You can start another import
          immediately after clicking — jobs run in parallel.
        </div>
      </section>

      <!-- In-page progress section removed — Import is now fire-and-forget.
           handleImport surfaces a transient formSuccess toast and the
           Background Tasks page owns live progress for every job. -->

      <!-- File list — three buckets surfaced as tabs so the user can see at
           a glance what's actionable (Will Import) vs noise (Already Imported,
           Not a Video). Search filters the active tab. -->
      {#if folderSelected}
        <section class="card bg-base-200 p-4 space-y-3">
          {#if filesLoading}
            <div class="flex items-center gap-2 text-base-content/70">
              <span class="loading loading-spinner loading-sm"></span> Loading files...
            </div>
          {:else if fileList.length === 0 && nonImportableFileList.length === 0}
            <div class="text-base-content/60 text-sm italic">Folder is empty.</div>
          {:else}
            <div role="tablist" class="tabs tabs-boxed bg-base-100">
              <button
                type="button"
                role="tab"
                class="tab gap-2 {activeFileTab === 'pending' ? 'tab-active' : ''}"
                onclick={() => (activeFileTab = 'pending')}
              >
                Will Import
                <span class="badge badge-sm font-bold tabular-nums {pendingFiles.length > 0 ? 'badge-primary' : 'badge-ghost'}">
                  {pendingFiles.length}
                </span>
              </button>
              <button
                type="button"
                role="tab"
                class="tab gap-2 {activeFileTab === 'imported' ? 'tab-active' : ''}"
                onclick={() => (activeFileTab = 'imported')}
              >
                Already Imported
                <!-- Muted green: solid bg-success at full saturation was too
                     loud next to the other tabs; ~25% alpha tints it without
                     bleeding into the surrounding chrome. font-bold keeps the
                     numerals legible against the lighter fill. -->
                <span class="badge badge-sm font-bold tabular-nums {alreadyImportedFiles.length > 0 ? 'bg-success/25 text-success border-success/30' : 'badge-ghost'}">
                  {alreadyImportedFiles.length}
                </span>
              </button>
              <button
                type="button"
                role="tab"
                class="tab gap-2 {activeFileTab === 'other' ? 'tab-active' : ''}"
                onclick={() => (activeFileTab = 'other')}
              >
                Not a Video
                <span class="badge badge-sm font-bold tabular-nums {nonImportableFileList.length > 0 ? 'badge-warning' : 'badge-ghost'}">
                  {nonImportableFileList.length}
                </span>
              </button>
            </div>

            <div class="flex flex-wrap items-center gap-2">
              <input
                type="text"
                class="input input-bordered input-sm flex-1 min-w-48"
                placeholder="Filter files by name..."
                bind:value={fileSearch}
              />
              {#if activeFileTab !== 'other'}
                <label class="label cursor-pointer gap-2">
                  <input type="checkbox" class="checkbox checkbox-sm" bind:checked={showThumbnails} />
                  <span class="label-text text-sm">Thumbnails</span>
                </label>
              {/if}
              <span class="text-xs text-base-content/60 tabular-nums ml-auto">
                Showing {visibleFiles.length} of
                {activeFileTab === 'pending' ? pendingFiles.length
                 : activeFileTab === 'imported' ? alreadyImportedFiles.length
                 : nonImportableFileList.length}
              </span>
            </div>

            {#if visibleFiles.length === 0}
              <div class="text-base-content/60 text-sm italic p-2">
                {#if activeFileTab === 'pending'}
                  No new videos to import in this folder.
                {:else if activeFileTab === 'imported'}
                  Nothing here is already imported.
                {:else}
                  No non-video files in this folder.
                {/if}
              </div>
            {:else if activeFileTab === 'other'}
              <!-- Compact mono list — these files are skipped, no thumbnail. -->
              <div class="text-xs font-mono max-h-[32rem] overflow-auto space-y-0.5 text-base-content/70 px-1">
                {#each visibleFiles as f (f)}
                  <div class="break-all">{f}</div>
                {/each}
              </div>
            {:else}
              <!-- Pending or already-imported: show thumbnails when enabled. -->
              <div class="max-h-[32rem] overflow-auto space-y-1">
                {#each visibleFiles as f (f)}
                  <div
                    class="flex items-center gap-3 p-2 rounded hover:bg-base-300"
                    class:opacity-60={activeFileTab === 'imported'}
                  >
                    {#if showThumbnails}
                      <img
                        src={api.importThumbnailUrl(f)}
                        loading="lazy"
                        alt=""
                        width="80"
                        height="45"
                        class="w-[80px] h-[45px] bg-base-300 object-cover rounded shrink-0"
                        onerror={(e) => { (e.currentTarget as HTMLImageElement).style.visibility = 'hidden'; }}
                      />
                    {/if}
                    <div class="flex-1 min-w-0 text-xs font-mono break-all">{f}</div>
                    {#if activeFileTab === 'imported'}
                      <span class="badge badge-success badge-sm shrink-0">imported</span>
                    {/if}
                  </div>
                {/each}
              </div>
            {/if}
          {/if}
        </section>
      {/if}

      <!-- Tags -->
      {#if true}
        <section class="card bg-base-200 p-4">
          <button type="button" class="flex items-center gap-2" onclick={() => (showTagsSection = !showTagsSection)}>
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 24 24"
              class="h-4 w-4 fill-current transition-transform duration-150 {showTagsSection ? 'rotate-90' : ''}"
              aria-hidden="true"
            >
              <path d="M9 6l6 6-6 6V6z" />
            </svg>
            <span class="font-medium">Edit Tags</span>
          </button>
          {#if showTagsSection}
            <div class="mt-3 space-y-3">
              <!-- Generic tag picker — applies any tag from any group to every
                   imported video. Uses /api/tags/search for autocomplete. -->
              <div class="form-control">
                <span class="label-text font-medium">Tags to apply</span>
                <div class="relative mt-1">
                  <input
                    class="input input-bordered input-sm w-full"
                    placeholder="Type to search tags…"
                    value={initialTagPickerInput}
                    oninput={async (e) => {
                      initialTagPickerInput = (e.target as HTMLInputElement).value;
                      const q = initialTagPickerInput.trim();
                      initialTagSuggestions = q
                        ? (await api.searchTags(q)).filter(h => !initialTagIds.includes(h.tagId))
                        : [];
                    }}
                  />
                  {#if initialTagSuggestions.length > 0}
                    <div class="absolute z-10 mt-1 w-full bg-base-100 border border-base-300 rounded shadow max-h-60 overflow-y-auto">
                      {#each initialTagSuggestions as h (h.tagId)}
                        <button
                          type="button"
                          class="w-full text-left px-2 py-1 hover:bg-base-200 text-sm flex justify-between"
                          onmousedown={() => {
                            initialTagIds = [...initialTagIds, h.tagId];
                            initialTagLabels = { ...initialTagLabels, [h.tagId]: h.name };
                            initialTagPickerInput = '';
                            initialTagSuggestions = [];
                          }}
                        >
                          <span>{h.name}</span>
                          <span class="text-xs text-base-content/50">{h.tagGroupName}</span>
                        </button>
                      {/each}
                    </div>
                  {/if}
                </div>
                {#if initialTagIds.length > 0}
                  <div class="flex flex-wrap gap-1 mt-2">
                    {#each initialTagIds as tid (tid)}
                      <span class="badge badge-primary gap-1">
                        {initialTagLabels[tid] ?? tid}
                        <button onclick={() => {
                          initialTagIds = initialTagIds.filter(x => x !== tid);
                        }}>×</button>
                      </span>
                    {/each}
                  </div>
                {/if}
              </div>

              <label class="form-control">
                <span class="label-text font-medium">Notes</span>
                <textarea class="textarea textarea-bordered" rows="2" bind:value={importNotes}></textarea>
              </label>
            </div>
          {/if}
        </section>
      {/if}

      {#if folderSelected && !hasImportableFiles}
        <div class="text-sm text-base-content/60">
          {fileList.length === 0
            ? 'No video files in this folder.'
            : 'All video files are already imported.'}
        </div>
      {/if}
    </div>
  </div>
</div>

<!-- Floating Import button — always visible on the bottom-right of the
     viewport, regardless of scroll position. Disabled states explain in the
     tooltip why it isn't clickable yet. -->
<div class="fixed bottom-6 right-6 z-30">
  <button
    type="button"
    class="btn btn-lg btn-soft btn-primary btn-cta shadow-lg"
    disabled={!folderSelected || !hasImportableFiles || importing}
    onclick={handleImport}
    title={!folderSelected
      ? 'Pick a folder from the tree first'
      : !hasImportableFiles
        ? fileList.length === 0
          ? 'No video files in this folder'
          : 'All video files in this folder are already imported'
        : ''}
  >
    {#if importing}<span class="loading loading-spinner loading-sm"></span>{/if}
    Import{folderSelected && hasImportableFiles ? ` (${importableCount})` : ''}
  </button>
</div>

{#if showAddSetDialog}
  <div class="modal modal-open" role="presentation" onclick={closeAddSetDialog}>
    <div class="modal-box space-y-3" role="presentation" onclick={(e) => e.stopPropagation()}>
      <div class="flex items-center justify-between">
        <h3 class="font-bold text-lg">Add Source</h3>
        <button type="button" class="btn btn-sm btn-ghost" onclick={closeAddSetDialog}>×</button>
      </div>
      <!-- Path first because pasting one auto-fills Name from the leaf
           segment — so the user types/pastes the path, then optionally
           tweaks the proposed Name below. -->
      <div>
        <label class="label" for="add-set-path"><span class="label-text">Path</span></label>
        <input
          id="add-set-path"
          class="input input-bordered w-full"
          bind:value={addSetPath}
          onpaste={onAddSetPathPaste}
          placeholder="e.g. S:/Videos/Concerts"
          autocomplete="off"
        />
        <div class="label">
          <span class="label-text-alt text-base-content/60">
            Backslashes and surrounding quotes are stripped on paste.
          </span>
        </div>
      </div>
      <div>
        <label class="label" for="add-set-name">
          <span class="label-text">Name</span>
          {#if addSetNameConflict}
            <span class="label-text-alt text-error">A source with this name already exists</span>
          {/if}
        </label>
        <input
          id="add-set-name"
          class="input input-bordered w-full {addSetNameConflict ? 'input-error text-error' : ''}"
          bind:value={addSetName}
          placeholder="e.g. Concerts"
          autocomplete="off"
        />
      </div>
      {#if addSetError}<div class="alert alert-error text-sm">{addSetError}</div>{/if}
      <div class="modal-action">
        <button type="button" class="btn btn-soft btn-primary btn-cta" disabled={addSetSaving || addSetNameConflict} onclick={saveAddSet}>
          {addSetSaving ? 'Saving...' : 'Add'}
        </button>
        <button type="button" class="btn btn-cancel" disabled={addSetSaving} onclick={closeAddSetDialog}>
          Cancel
        </button>
      </div>
    </div>
  </div>
{/if}

<script lang="ts">
  import { onMount, onDestroy, untrack } from 'svelte';
  import { goto } from '$app/navigation';
  import { api, ApiError } from '$lib/api';
  import type {
    ImportBrowseDirectory,
    DirectoryImportRequest,
    Tag,
    VideoSet
  } from '$lib/types';
  import { breadcrumbs } from '$lib/pathHelpers';
  import TagEditModal from '$lib/components/TagEditModal.svelte';

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

  // Live "discovered video files" count while a browse scan runs (issue #27).
  // The /import/browse walk reports progress to a server-side counter; we
  // poll it so the user sees a climbing total instead of a blind spinner.
  let scanDiscovered = $state(0);
  const treeLoading = $derived(treeInitialLoading || treeLoadingPaths.size > 0);
  $effect(() => {
    if (!treeLoading) return;
    let cancelled = false;
    const poll = async () => {
      try {
        const p = await api.getImportScanProgress();
        if (!cancelled) scanDiscovered = p.discovered;
      } catch {
        /* transient — keep polling until the scan finishes */
      }
    };
    void poll();
    const id = setInterval(poll, 500);
    return () => {
      cancelled = true;
      clearInterval(id);
    };
  });

  // Form
  // Multi-select state.
  // - selectedPath is the "primary" — drives the right-pane file list,
  //   breadcrumbs, and default import name. Always the last clicked
  //   path (and a member of selectedPaths) when anything is selected.
  // - selectedPaths is the canonical selection set; size > 1 when the
  //   user Ctrl- or Shift-clicked.
  // - selectionAnchor is the Shift-click range origin. Reset to the
  //   clicked row on plain or Ctrl click.
  let selectedPath = $state('');
  let selectedPaths = $state<Set<string>>(new Set());
  let selectionAnchor: string | null = null;
  let includeSubdirectories = $state(true);
  let importName = $state('');
  // Generic tag picker — IDs of tags to apply to every newly-imported video.
  let initialTagIds = $state<string[]>([]);
  let initialTagPickerInput = $state('');
  let initialTagSuggestions = $state<{ tagId: string; name: string; tagGroupName: string }[]>([]);
  let initialTagLabels = $state<Record<string, string>>({});
  let importNotes = $state('');

  // Create-tag modal (TagEditModal in create mode, with its Group select)
  // launched from the picker's "+ New Tag" button or the dropdown's
  // "+ Create" row. On save the new tag is auto-applied to the import.
  let showCreateTagModal = $state(false);
  let createTagInitialName = $state('');

  function openCreateTag(prefill: string) {
    createTagInitialName = prefill;
    initialTagSuggestions = [];
    showCreateTagModal = true;
  }

  function onTagCreated(saved: Tag) {
    if (!initialTagIds.includes(saved.id)) {
      initialTagIds = [...initialTagIds, saved.id];
    }
    initialTagLabels = { ...initialTagLabels, [saved.id]: saved.name };
    initialTagPickerInput = '';
  }

  // File list — keyed by folder path so the right pane can aggregate
  // across the full multi-selection. Each entry is what the API
  // returned for that single folder; the table below derives totals
  // (Will Import / Already Imported / Other) by walking the map. A
  // single-folder selection collapses to one entry, behaving exactly
  // like the pre-multi-select implementation did.
  type FolderFiles = { files: string[]; nonImportable: string[]; imported: string[] };
  let filesByFolder = $state<Map<string, FolderFiles>>(new Map());
  // Aggregated views — what the existing template binds against.
  // Replaced direct assignments with deriveds so any flip in
  // selectedPaths or filesByFolder propagates automatically.
  const fileList = $derived.by<string[]>(() => {
    const out: string[] = [];
    for (const v of filesByFolder.values()) out.push(...v.files);
    return out;
  });
  const nonImportableFileList = $derived.by<string[]>(() => {
    const out: string[] = [];
    for (const v of filesByFolder.values()) out.push(...v.nonImportable);
    return out;
  });
  const importedFilesSet = $derived.by<Set<string>>(() => {
    const s = new Set<string>();
    for (const v of filesByFolder.values()) {
      for (const f of v.imported) s.add(f);
    }
    return s;
  });
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
    // Primary selection — the row that drives the right-pane
    // detail view. There is always at most one.
    selected: boolean;
    // Member of the multi-selection set (Ctrl/Shift-click). The
    // primary row is also `inSelection`; rows can be `inSelection`
    // without being `selected` when the user has multi-selected.
    inSelection: boolean;
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
          selected: selectedPath === d.fullPath,
          inSelection: selectedPaths.has(d.fullPath)
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

  // "Import More" checkbox under the floating Import button. Checked
  // = stay on the import tool after kicking off a job so the user
  // can queue up another folder. Unchecked (default) = navigate to
  // /background-tasks afterwards so they can watch progress. Stored
  // in localStorage so the choice persists across sessions — most
  // users settle into one of the two workflows.
  let importMore = $state(false);

  // Paths whose import job is still running (server-side). Polled
  // from /api/import/jobs so the tree can show a "this folder is
  // currently being imported" spinner on any row whose fullPath
  // matches an in-flight job — long-running visual feedback
  // beyond the brief in-flight click flash that the `importing`
  // flag tracks. Includes whatever the user just clicked Import on
  // (selectedPath, while `importing` is true) for the moment between
  // the click and the next poll picking the job up.
  let activeImportPaths = $state<Set<string>>(new Set());
  let importPollTimer: ReturnType<typeof setTimeout> | null = null;
  async function pollActiveImports() {
    try {
      const jobs = await api.listImportJobs();
      activeImportPaths = new Set(
        jobs.filter(j => !j.isCompleted).map(j => j.directoryPath)
      );
    } catch {
      // Non-fatal — keep the previous set so the spinners don't
      // flicker off on a transient failure.
    }
    // Tighter cadence while there's anything running so the
    // spinner clears promptly when the job finishes; relaxed when
    // idle so we don't hammer the API on a quiet page.
    const interval = activeImportPaths.size > 0 ? 3000 : 10000;
    importPollTimer = setTimeout(pollActiveImports, interval);
  }
  // True for rows whose fullPath has an active job, OR for the row
  // the user just clicked Import on (covers the gap between the
  // click and the poll picking it up).
  function isImportingPath(path: string): boolean {
    return activeImportPaths.has(path) || (importing && selectedPath === path);
  }

  // --- Lifecycle ------------------------------------------------------------

  onMount(async () => {
    importMore = localStorage.getItem('importMore') === '1';

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
    // Kick off the active-import poll. Self-reschedules; cleared
    // on destroy.
    void pollActiveImports();
  });

  onDestroy(() => {
    if (importPollTimer) {
      clearTimeout(importPollTimer);
      importPollTimer = null;
    }
  });

  // Cheap on every flip; keeps localStorage in sync without a save
  // gesture.
  $effect(() => {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem('importMore', importMore ? '1' : '0');
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

  // Per-id busy gate so the toggle can show a spinner without
  // disabling the whole sidebar, and so a rapid double-click can't
  // fire two updateVideoSet calls on the same source.
  let setEnabledBusy = $state<Set<string>>(new Set());

  // Flip a source's enabled flag from inside the import tree.
  // Optimistic update — the toggle visibly slides immediately, the
  // API call runs in the background, and we roll back on failure.
  // Saves a context-switch to the Configuration page for the very
  // common "I just disabled this and want to re-enable it" flow.
  async function toggleSourceEnabled(s: VideoSet) {
    if (setEnabledBusy.has(s.id)) return;
    setEnabledBusy.add(s.id);
    setEnabledBusy = new Set(setEnabledBusy);
    const next = !s.enabled;
    sets = sets.map(x => x.id === s.id ? { ...x, enabled: next } : x);
    try {
      await api.updateVideoSet(s.id, {
        id: s.id,
        name: s.name,
        path: s.path,
        enabled: next,
        sortOrder: s.sortOrder
      });
    } catch (e) {
      // Roll back the optimistic flip and surface the error.
      sets = sets.map(x => x.id === s.id ? { ...x, enabled: s.enabled } : x);
      formError = toMessage('Failed to update source', e);
    } finally {
      setEnabledBusy.delete(s.id);
      setEnabledBusy = new Set(setEnabledBusy);
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

  async function selectFolder(dir: ImportBrowseDirectory, e?: MouseEvent) {
    const path = dir.fullPath;
    const ctrl = !!(e && (e.ctrlKey || e.metaKey));
    const shift = !!(e?.shiftKey);

    if (shift && selectionAnchor) {
      // Shift-click: range-select between the anchor and the clicked
      // row, replacing the current selection. Walks `treeRows` so
      // the range follows the visible (expanded) rendering — invisible
      // children of collapsed parents are excluded, matching the
      // user's intent of "everything I can see between A and B".
      const rows = treeRows;
      const anchorIdx = rows.findIndex(r => r.dir.fullPath === selectionAnchor);
      const clickIdx = rows.findIndex(r => r.dir.fullPath === path);
      if (anchorIdx >= 0 && clickIdx >= 0) {
        const [lo, hi] = anchorIdx < clickIdx
          ? [anchorIdx, clickIdx]
          : [clickIdx, anchorIdx];
        const range = new Set<string>();
        for (let i = lo; i <= hi; i++) range.add(rows[i].dir.fullPath);
        selectedPaths = range;
      } else {
        // Anchor not visible (collapsed parent). Fall back to plain
        // click semantics.
        selectedPaths = new Set([path]);
        selectionAnchor = path;
      }
      selectedPath = path;
    } else if (ctrl) {
      // Ctrl/Cmd-click: toggle this row's membership in the selection
      // set. The primary follows the click (added) or falls back to
      // any remaining member (removed and was primary).
      const next = new Set(selectedPaths);
      if (next.has(path)) {
        next.delete(path);
        if (selectedPath === path) {
          selectedPath = next.size > 0 ? [...next][0] : '';
        }
      } else {
        next.add(path);
        selectedPath = path;
      }
      selectedPaths = next;
      selectionAnchor = path;
    } else {
      // Plain click: replace the selection. Same single-select behavior
      // the import tool has always had.
      selectedPaths = new Set([path]);
      selectedPath = path;
      selectionAnchor = path;
    }

    folderSelected = selectedPath !== '';

    if (selectedPath) {
      // Auto-fill the import name from the primary path. For multi-
      // selections, handleImport ignores this and generates a name per
      // path so the user doesn't have to disambiguate manually.
      importName = computeDefaultImportName(selectedPath);

      // Auto-expand only on plain click — Ctrl/Shift gestures are about
      // selection, not drilling into the tree, so we leave expansion
      // state alone.
      if (!ctrl && !shift && dir.hasSubdirectories && !treeExpandedPaths.has(selectedPath)) {
        const expanded = new Set(treeExpandedPaths);
        expanded.add(selectedPath);
        treeExpandedPaths = expanded;
        await fetchChildren(selectedPath);
      }

      await loadFiles();
    } else {
      // Selection emptied via Ctrl-deselect — clear the per-folder
      // map so the aggregated derived views drop back to empty.
      filesByFolder = new Map();
    }
  }

  function clearSelection() {
    selectedPaths = new Set();
    selectedPath = '';
    selectionAnchor = null;
    folderSelected = false;
    filesByFolder = new Map();
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

  // Sync filesByFolder with selectedPaths:
  //   · drop entries for paths that have been deselected,
  //   · fetch entries for paths that are newly in the set.
  // Cached entries are reused across selection changes so a Ctrl-
  // click that adds one folder doesn't re-fetch the four already
  // listed. The toggle of `includeSubdirectories` invalidates the
  // entire cache (separate $effect below) since recursion changes
  // every folder's file list, not just the primary's.
  async function loadFiles() {
    const paths = [...selectedPaths];
    if (paths.length === 0) {
      filesByFolder = new Map();
      return;
    }
    filesLoading = true;
    formError = null;
    try {
      const next = new Map<string, FolderFiles>();
      // Carry forward cached entries that are still selected.
      for (const [k, v] of filesByFolder.entries()) {
        if (selectedPaths.has(k)) next.set(k, v);
      }
      // Fetch missing ones. Per-folder try/catch so one slow / dead
      // network share doesn't kill the whole batch — partial results
      // are better than nothing for a user trying to figure out which
      // folder is broken.
      for (const p of paths) {
        if (next.has(p)) continue;
        try {
          const res = await api.getImportFiles(p, includeSubdirectories);
          next.set(p, {
            files: res.files,
            nonImportable: res.nonImportableFiles,
            imported: res.importedFiles
          });
        } catch (e) {
          formError = toMessage(`Failed to list files for ${p}`, e);
        }
      }
      filesByFolder = next;
    } finally {
      filesLoading = false;
    }
  }

  // Manual file-list refresh — invalidates the per-folder cache and
  // re-fetches every selected path. Useful when the user has just
  // dropped new files into a folder on disk and wants the Will
  // Import / Already Imported / Not a Video counts to reflect what's
  // actually there now without changing the selection. No-op when
  // nothing is selected; `loadFiles()` handles the empty case.
  async function refreshFiles() {
    filesByFolder = new Map();
    await loadFiles();
  }

  // Refresh the file list when the user toggles "Include subdirectories"
  // so the table immediately reflects what would actually be imported.
  // Invalidates the entire cache because recursion changes every
  // folder's listing (not just the primary's). Skip while nothing is
  // selected to avoid needless 4xx churn on mount.
  //
  // The check on `selectedPaths.size` lives inside `untrack` so the
  // effect's only reactive dep is `includeSubdirectories` — without
  // that wrap it would also fire on every selection change, racing
  // with the loadFiles() call in selectFolder.
  $effect(() => {
    void includeSubdirectories; // dependency
    untrack(() => {
      if (selectedPaths.size > 0) {
        filesByFolder = new Map();
        void loadFiles();
      }
    });
  });

  // (Alt+Numpad flag toggles removed — flags are now generic tags.)

  // Fire-and-forget import. Posts the request, surfaces a brief success
  // toast, then immediately re-enables the form so the user can pick
  // another folder and click Import again. Live progress lives on the
  // Background Tasks page — we don't poll here anymore.
  let successClearTimer: ReturnType<typeof setTimeout> | null = null;
  async function handleImport() {
    const paths = [...selectedPaths];
    if (paths.length === 0) {
      formError = 'Pick a folder from the tree first.';
      return;
    }

    importing = true;
    formError = null;
    formSuccess = null;
    if (successClearTimer) { clearTimeout(successClearTimer); successClearTimer = null; }

    // For a single-path selection the user's edited importName wins.
    // For a multi-path selection we ignore the field entirely and
    // generate "{source} - {leaf}" per folder, since the field can
    // only hold one value and forcing the user to edit it 5 times
    // before clicking Import isn't worth it.
    const isMulti = paths.length > 1;
    const userName = importName.trim();
    const failures: string[] = [];
    let started = 0;
    try {
      for (const path of paths) {
        const name = !isMulti && userName.length > 0
          ? userName
          : computeDefaultImportName(path);
        const request: DirectoryImportRequest = {
          directoryPath: path,
          includeSubdirectories,
          name,
          initialTagIds: initialTagIds.length > 0 ? initialTagIds : null,
          notes: importNotes.trim().length > 0 ? importNotes.trim() : null
        };
        try {
          await api.startImport(request);
          started++;
        } catch (e) {
          failures.push(`${name}: ${toMessage('start failed', e).replace(/^start failed: /, '')}`);
        }
      }

      if (failures.length === 0) {
        formSuccess = isMulti
          ? `Started ${started} import jobs. Running in the background — view progress on Background Tasks.`
          : `Import started: "${userName.length > 0 ? userName : computeDefaultImportName(paths[0])}". Running in the background — view progress on Background Tasks.`;
      } else {
        formError = `Started ${started} of ${paths.length}. Failures: ${failures.join('; ')}`;
      }
      // Auto-clear so chained imports don't pile up stale messages.
      successClearTimer = setTimeout(() => { formSuccess = null; }, 6000);
      // Refresh the totals counter; the file list is intentionally NOT
      // reloaded since the import is still in flight server-side.
      void refreshVideoCount();
      // "Import More" unchecked → user is done queuing jobs and wants
      // to watch them run. Hand them off to Background Tasks so they
      // don't have to navigate manually. Checked (default) keeps them
      // on this page to queue up another folder.
      if (!importMore && started > 0) {
        await goto('/background-tasks');
      }
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
      <!-- Hint + multi-selection summary. The hint stays muted on
           single-select; once the user has multi-selected the bar
           foregrounds with a count and a Clear-all link. Saves a
           "how do I deselect?" round of fiddling. -->
      {#if selectedPaths.size > 1}
        <div class="flex items-center justify-between gap-2 mb-2 px-1 py-1 rounded bg-primary/15 text-primary-content border border-primary/40 text-xs">
          <span class="tabular-nums font-medium">{selectedPaths.size} folders selected</span>
          <button
            type="button"
            class="link link-hover"
            onclick={clearSelection}
          >Clear</button>
        </div>
      {:else}
        <div class="text-[10px] text-base-content/50 mb-2 px-1">
          <kbd class="kbd kbd-xs">Ctrl</kbd>+click to add ·
          <kbd class="kbd kbd-xs">Shift</kbd>+click for range
        </div>
      {/if}

      {#if treeInitialLoading}
        <div class="flex items-center gap-2 text-base-content/70 p-2 tabular-nums">
          <span class="loading loading-spinner loading-sm"></span>
          {#if scanDiscovered > 0}
            Scanning sources… {scanDiscovered.toLocaleString()} video file{scanDiscovered === 1 ? '' : 's'} found
          {:else}
            Loading…
          {/if}
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
            {@const rowSet = row.depth === 0
              ? sets.find(s => s.path === row.dir.fullPath || s.name === row.dir.name)
              : undefined}
            {@const rowSetDisabled = rowSet ? !rowSet.enabled : false}
            <li>
              <!-- Three-tier row tint:
                     primary selected (drives detail pane) →
                       bg-primary/30 + ring-2 ring-primary inset
                     other multi-selected rows                 →
                       bg-primary/15
                     unselected                                →
                       hover:bg-base-300
                   ring-inset keeps the indicator inside the row's
                   bounds so layout doesn't shift between selected
                   and not-selected; same trick used elsewhere
                   throughout the app. -->
              <div
                class="flex items-center gap-1 rounded pr-1 transition-colors {row.selected
                  ? 'bg-primary/30 ring-2 ring-primary ring-inset'
                  : row.inSelection
                    ? 'bg-primary/15'
                    : 'hover:bg-base-300'}"
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
                  onclick={(e) => selectFolder(row.dir, e)}
                  title={rowSetDisabled
                    ? `${row.dir.fullPath} — disabled (videos hidden from the grid)`
                    : row.dir.fullPath}
                >
                  <div class="truncate">
                    <span class={rowSetDisabled ? 'line-through text-base-content/60' : ''}>{row.dir.name}</span>
                    {#if rowSetDisabled}
                      <span class="text-xs italic text-base-content/50 ml-1 no-underline">(Disabled)</span>
                    {/if}
                  </div>
                  {#if row.depth === 0}
                    <div class="truncate text-xs text-base-content/55 font-normal leading-tight">
                      {row.dir.fullPath}
                    </div>
                  {/if}
                </button>
                {#if isImportingPath(row.dir.fullPath)}
                  <!-- Long-running import in flight on this folder.
                       Replaces the count text so the user gets an
                       unmistakable "this is being processed" cue;
                       the count is stale during the import anyway. -->
                  <span
                    class="text-xs shrink-0 flex items-center gap-1.5 text-info italic"
                    title="Importing — track progress on Background Tasks"
                  >
                    <span class="loading loading-spinner loading-xs"></span>
                    Importing
                  </span>
                {:else if row.dir.videoCount > 0}
                  <!-- "5 / 10 imported" — compact slash form (eye
                       lands on the imported count first, then the
                       total) plus the verb so the meaning is
                       unambiguous without a tooltip. Color-tinted
                       by progress: success green when fully in the
                       library, warning orange while partial, muted
                       otherwise. -->
                  <span
                    class="text-xs shrink-0 tabular-nums {isFullyImported
                      ? 'text-success'
                      : row.dir.importedCount > 0
                        ? 'text-warning'
                        : 'text-base-content/55'}"
                  >
                    {row.dir.importedCount} / {row.dir.videoCount} imported
                  </span>
                {/if}
                {#if row.depth === 0 && rowSet}
                  <!-- Enable / disable toggle for source-root rows.
                       Lives inline so the user can flip a source's
                       state without leaving the Import Tool — the
                       common "I disabled this for testing and want
                       it back" workflow. Optimistic update; the
                       previous busy state shows a spinner over the
                       toggle's track to stop double-clicks. The
                       click is stopPropagation'd so flipping the
                       toggle doesn't also select the source folder
                       in the right-pane breadcrumb. -->
                  {@const setBusy = setEnabledBusy.has(rowSet.id)}
                  <span class="shrink-0 flex items-center gap-1">
                    {#if setBusy}<span class="loading loading-spinner loading-xs"></span>{/if}
                    <input
                      type="checkbox"
                      class="toggle toggle-xs toggle-primary"
                      checked={rowSet.enabled}
                      disabled={setBusy}
                      onchange={() => toggleSourceEnabled(rowSet)}
                      onclick={(e) => e.stopPropagation()}
                      title={rowSet.enabled
                        ? 'Disable this source — videos will be hidden from the player grid'
                        : 'Enable this source — videos will appear in the player grid'}
                      aria-label={rowSet.enabled ? `Disable ${rowSet.name}` : `Enable ${rowSet.name}`}
                    />
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
              {@const isLast = ci === crumbs.length - 1}
              <!-- crumb.disabled is only set on the source-root
                   crumb (index 1). For other crumbs it's
                   undefined and the strikethrough span just
                   collapses to plain text. -->
              <span class="{isLast ? 'font-semibold' : 'text-base-content/60'} {crumb.disabled ? 'line-through text-base-content/60' : ''}"
              >{crumb.name}</span>
              {#if crumb.disabled}
                <span class="text-xs italic text-base-content/50 ml-1 no-underline">(Disabled)</span>
              {/if}
              {#if !isLast}
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
            <div class="flex items-center justify-between gap-2">
              <span class="text-base-content/60 text-sm italic">Folder is empty.</span>
              <!-- Still expose the refresh affordance in the empty
                   state — common workflow is "I just dropped files
                   into the folder, re-scan." -->
              <button
                type="button"
                class="btn btn-ghost btn-xs"
                onclick={refreshFiles}
                title="Re-scan folder(s) for new or removed files"
                aria-label="Refresh file list"
              >↻ Refresh</button>
            </div>
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
              <!-- Manual refresh — invalidates the per-folder cache
                   and re-fetches every currently-selected path.
                   Useful when files have been added or removed on
                   disk since the selection was made. Disabled while
                   a load is already in flight so a frantic click
                   can't fire overlapping requests. -->
              <button
                type="button"
                class="btn btn-ghost btn-xs"
                onclick={refreshFiles}
                disabled={filesLoading}
                title="Re-scan folder(s) for new or removed files"
                aria-label="Refresh file list"
              >
                {#if filesLoading}<span class="loading loading-spinner loading-xs"></span>{:else}↻{/if}
                Refresh
              </button>
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
                <div class="mt-1 flex gap-2">
                  <div class="relative flex-1">
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
                    {#if initialTagSuggestions.length > 0 || initialTagPickerInput.trim().length > 0}
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
                        {#if initialTagPickerInput.trim().length > 0}
                          <!-- Create-from-search escape hatch: typed text
                               that doesn't exist yet (or that the user
                               wants in a different group) becomes a new
                               tag via TagEditModal, where the Group
                               select offers every tag group. -->
                          <button
                            type="button"
                            class="w-full text-left px-2 py-1 hover:bg-base-200 text-sm text-primary {initialTagSuggestions.length > 0 ? 'border-t border-base-300' : ''}"
                            onmousedown={() => openCreateTag(initialTagPickerInput.trim())}
                          >
                            + Create new tag “{initialTagPickerInput.trim()}”…
                          </button>
                        {/if}
                      </div>
                    {/if}
                  </div>
                  <button
                    type="button"
                    class="btn btn-sm btn-soft btn-primary btn-cta shrink-0"
                    onclick={() => openCreateTag(initialTagPickerInput.trim())}
                    title="Create a new tag in any tag group and apply it to this import"
                  >+ New Tag</button>
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
                <!-- Single-line input (was a 2-row textarea). Multi-line
                     notes via paste still flow through; the field just
                     scrolls horizontally to match the height of every
                     other one-line input in this form. -->
                <input
                  type="text"
                  class="input input-bordered input-sm w-full"
                  bind:value={importNotes}
                />
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

<!-- Floating CTA cluster — always visible on the bottom-right of the
     viewport, regardless of scroll position. Stacked column with the
     Import button on top and an "Import More" checkbox below.
     Checked (default) keeps the user on this page after kicking off
     a job so they can queue another folder; unchecked navigates to
     Background Tasks afterwards. Right-aligned (items-end) so the
     checkbox label and button trail share the same right edge. -->
<div class="fixed bottom-6 right-6 z-30 flex flex-col items-end gap-2">
  <!-- min-w sized empirically so the Import button is a touch wider
       than the "Import More" checkbox pill below it (which is
       content-sized around the label + checkbox). Keeps the two
       chrome elements visually anchored as a vertical stack rather
       than reading as two unrelated widths. -->
  <button
    type="button"
    class="btn btn-lg btn-soft btn-primary btn-cta shadow-lg min-w-56"
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
    {#if selectedPaths.size > 1}
      <!-- Multi-import branch: button operates on every selected
           folder. The right-pane file list still mirrors the primary
           selection's contents (single-folder count of importable
           files), so we don't quote a count here — the multi-import
           summary in the success toast covers it post-click. -->
      Import {selectedPaths.size} folders
    {:else}
      Import{folderSelected && hasImportableFiles ? ` (${importableCount})` : ''}
    {/if}
  </button>
  <!-- Solid-bg pill so the label is readable against any thumbnail
       grid that scrolls behind the floating cluster. -->
  <label
    class="flex items-center gap-2 cursor-pointer bg-base-100/95 border border-base-300 rounded-full px-3 py-1 shadow text-sm"
    title="Checked: stay on the import tool after the job starts. Unchecked: jump to Background Tasks to watch progress."
  >
    <input type="checkbox" class="checkbox checkbox-sm checkbox-primary" bind:checked={importMore} />
    <span>Import More</span>
  </label>
</div>

<!-- Create-tag modal — TagEditModal in create mode (tag=null, no fixed
     tagGroupId) shows a Group select over every tag group. onSaved
     auto-applies the new tag to this import's "Tags to apply" list. -->
<TagEditModal
  bind:show={showCreateTagModal}
  tag={null}
  initialName={createTagInitialName}
  onSaved={onTagCreated}
/>

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

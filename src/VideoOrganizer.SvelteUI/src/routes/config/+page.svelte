<script lang="ts">
  import { onMount } from 'svelte';
  import {
    playbackSettings,
    savePlaybackSettings,
    resetPlaybackSettings,
    defaultSettings
  } from '$lib/playbackSettings.svelte';
  import { api } from '$lib/api';
  import type { VideoSet } from '$lib/types';
  import {
    applySortClick,
    compareBySortStack,
    sortDir,
    sortPosition,
    loadColumnWidths,
    saveColumnWidths,
    startColumnResize,
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

  // --- Skip settings ---

  let status = $state<string | null>(null);
  let statusTimer: ReturnType<typeof setTimeout> | null = null;

  const rows = [
    { key: 'key1Seconds', label: 'Key 1 (backward)' },
    { key: 'key3Seconds', label: 'Key 3 (forward)' },
    { key: 'key4Seconds', label: 'Key 4 (backward)' },
    { key: 'key6Seconds', label: 'Key 6 (forward)' },
    { key: 'key7Seconds', label: 'Key 7 (backward)' },
    { key: 'key9Seconds', label: 'Key 9 (forward)' }
  ] as const;

  function flash(message: string) {
    status = message;
    if (statusTimer) clearTimeout(statusTimer);
    statusTimer = setTimeout(() => (status = null), 2500);
  }

  function normalize() {
    for (const { key } of rows) {
      const v = playbackSettings[key];
      playbackSettings[key] = Math.max(0, Number.isFinite(v) ? Math.floor(v) : 0);
    }
  }

  function handleSubmit(event: SubmitEvent) {
    event.preventDefault();
    normalize();
    savePlaybackSettings({ ...playbackSettings });
    flash('Saved');
  }

  function handleReset() {
    resetPlaybackSettings();
    Object.assign(playbackSettings, defaultSettings());
    flash('Reset to defaults');
  }

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

<div class="max-w-4xl space-y-10">
  <!-- ========= Keyboard Shortcuts ========= -->
  <section>
    <h1 class="text-2xl font-semibold mb-4">Keyboard Shortcuts</h1>
    <p class="text-sm text-base-content/70 mb-6">
      All shortcuts are active on the Video Player page. They're ignored while you're
      typing in any input, textarea, or select — so entering text into tag fields
      never triggers a skip or a file move.
    </p>

    <!-- Configurable seek times -->
    <h2 class="text-lg font-medium mb-3">Seek (configurable)</h2>
    <form onsubmit={handleSubmit} class="space-y-4 max-w-xl">
      {#each rows as row (row.key)}
        <div class="form-control">
          <label class="label" for={row.key}>
            <span class="label-text">{row.label}</span>
          </label>
          <div class="join">
            <input
              id={row.key}
              type="number"
              min="0"
              class="input input-bordered join-item w-32"
              bind:value={playbackSettings[row.key]}
            />
            <span class="join-item px-3 flex items-center bg-base-200 text-base-content/70 text-sm rounded-r">
              seconds
            </span>
          </div>
        </div>
      {/each}

      <div class="flex items-center gap-3 pt-2">
        <button type="submit" class="btn btn-soft btn-primary btn-cta">Save</button>
        <button type="button" class="btn btn-cancel" onclick={handleReset}>Reset Defaults</button>
        {#if status}<span class="text-success text-sm">{status}</span>{/if}
      </div>
    </form>

    <!-- Fixed shortcuts -->
    <h2 class="text-lg font-medium mt-8 mb-3">Playlist &amp; actions</h2>
    <div class="overflow-x-auto max-w-xl">
      <table class="table table-sm">
        <thead>
          <tr>
            <th class="w-24">Key</th>
            <th>Action</th>
          </tr>
        </thead>
        <tbody>
          <tr>
            <td><kbd class="kbd kbd-sm">←</kbd></td>
            <td>
              Save metadata (if changed) and go back to the previous video.
              Works even when a tag input has focus.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">→</kbd></td>
            <td>
              Save metadata (if changed) and advance to the next video. Works
              even when a tag input has focus — no need to Escape out first.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">Space</kbd></td>
            <td>Toggle <span class="font-semibold">play / pause</span>. Ignored while typing in a tag input.</td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">T</kbd> / <kbd class="kbd kbd-sm">1</kbd></td>
            <td>Toggle the Edit Tags panel</td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">W</kbd></td>
            <td>
              Toggle the <span class="font-semibold">Won't Play</span> tag and advance to the next video.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">D</kbd></td>
            <td>
              Mark <span class="font-semibold">to Delete</span> — moves the file to
              <code>&lt;set&gt;/_ToDelete</code> and advances to the next video.
              The app never actually deletes anything.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">U</kbd></td>
            <td>
              <span class="font-semibold">Undo</span> the mark on the current video —
              moves the file back to its original location and clears the flag.
              Typical review flow: press <kbd class="kbd kbd-xs">W</kbd>/<kbd class="kbd kbd-xs">D</kbd>,
              realize the mistake, press <kbd class="kbd kbd-xs">←</kbd> to go back,
              then <kbd class="kbd kbd-xs">U</kbd>.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">I</kbd></td>
            <td>
              Toggle the <span class="font-semibold">File Info</span> dialog — shows
              dimensions, codec, bitrate, frame rate, stream counts, and other
              technical metadata for the current video.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">R</kbd></td>
            <td>
              Clear the <span class="font-semibold">Needs Review</span> flag on the
              current video, save, and advance to the next video. No-op if the
              flag isn't set.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">F</kbd></td>
            <td>
              Toggle the <span class="font-semibold">Favorite</span> flag on the
              current video and save. Works on both the Video Player and the
              inline player in the Video Browser.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">\</kbd></td>
            <td>
              Snap the video back to <span class="font-semibold">fit-to-column</span>
              size. The percent indicator next to the filename shows current
              size vs. native and also clicks back to fit. Capped at 2x native
              by default so a low-res file doesn't balloon. Resets on every
              new video.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">K</kbd></td>
            <td>
              Drop a <span class="font-semibold">bookmark</span> at the current
              time. Default label is the timestamp; rename it inline in the
              Bookmarks list under the video. Bookmarks show as blue pins on
              the scrubber and save with the video on next navigation.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">C</kbd></td>
            <td>
              Start / end a <span class="font-semibold">clip</span>.
              First <kbd class="kbd kbd-xs">C</kbd> captures the clip's
              in-point at the current time; second <kbd class="kbd kbd-xs">C</kbd>
              closes the range and saves it as a new video row with
              <code>IsClip = true</code>. Tags are inherited from the parent.
              Playing the clip auto-seeks to the in-point and loops at the
              out-point. Filter your library by the Clip flag to see just
              your clips.
            </td>
          </tr>
          <tr>
            <td>
              <kbd class="kbd kbd-sm">[</kbd> /
              <kbd class="kbd kbd-sm">]</kbd> /
              <kbd class="kbd kbd-sm">M</kbd>
            </td>
            <td>
              <span class="font-semibold">Block editor.</span>
              <kbd class="kbd kbd-xs">[</kbd> marks the start of a do-not-play
              segment at the current time (auto-enters mark mode);
              <kbd class="kbd kbd-xs">]</kbd> closes it at the current time.
              Blocks show up as red strips on the scrubber and in a list
              below it for deletion. <kbd class="kbd kbd-xs">M</kbd> toggles
              mark mode without creating a block — useful for scrubbing
              through existing blocks to review them. In normal playback
              (mark mode off), the player auto-seeks past every block.
            </td>
          </tr>
          <tr>
            <td>Numpad <kbd class="kbd kbd-sm">1/3/4/6/7/9</kbd></td>
            <td>
              Seek, no matter where focus is. Always preventDefault'd so
              numpad digits don't land in tag inputs.
            </td>
          </tr>
          <tr>
            <td>Numpad <kbd class="kbd kbd-sm">0</kbd></td>
            <td>Jump to the start of the video (00:00).</td>
          </tr>
          <tr>
            <td>Numpad <kbd class="kbd kbd-sm">−</kbd></td>
            <td>Jump to 10 seconds from the end of the video.</td>
          </tr>
          <tr>
            <td>
              <kbd class="kbd kbd-sm">⇧</kbd> + <kbd class="kbd kbd-sm">1/3/4/6/7/9</kbd>
            </td>
            <td>
              Seek while typing in a tag input using the top-row digit keys.
              Shift prevents the corresponding character (<code>!#$^&amp;(</code>)
              from landing in the field.
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </section>

  <!-- ========= Sources ========= -->
  <section>
    <div class="flex items-center justify-between mb-4">
      <h2 class="text-2xl font-semibold">Sources</h2>
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
        <table class="table table-sm" style="table-layout: fixed; width: max-content;">
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
                <th class="relative select-none p-0">
                  <button
                    type="button"
                    class="w-full text-left px-3 py-2 hover:bg-base-200 cursor-pointer flex items-center gap-1"
                    onclick={(e) => onSourcesSortClick(col.key, e)}
                    title="Click to sort. Shift-click for multi-column sort."
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
                    aria-label={`Resize ${col.label}`}
                    class="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/40 active:bg-primary/60 z-10"
                    onmousedown={(e) => startColumnResize(e, getSourcesWidth(col.key, col.def), (w) => setSourcesWidth(col.key, w))}
                  ></button>
                </th>
              {/each}
              <th class="relative select-none text-right">
                Actions
                <button
                  type="button"
                  aria-label="Resize Actions"
                  class="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/40 active:bg-primary/60 z-10"
                  onmousedown={(e) => startColumnResize(e, getSourcesWidth('actions', 200), (w) => setSourcesWidth('actions', w))}
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
                      {s.name}
                      <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" class="w-3 h-3 fill-current opacity-60">
                        <path d="M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04a1 1 0 0 0 0-1.41l-2.34-2.34a1 1 0 0 0-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z" />
                      </svg>
                    </button>
                  {/if}
                </td>
                <td class="font-mono text-xs break-all">{s.path}</td>
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

  <section class="mt-8">
    <h2 class="text-2xl font-semibold mb-2">Style Guide</h2>
    <p class="text-sm text-base-content/70 mb-3">
      Reference of every color and button style used in the app.
    </p>
    <a href="/style-guide" class="btn btn-sm btn-soft btn-accent border border-accent/50">
      Open Style Guide →
    </a>
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
        Delete <span class="font-semibold">{confirmDelete.set.name}</span>?
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

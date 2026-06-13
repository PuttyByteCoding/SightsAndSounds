<script lang="ts">
  // Move a video's file into another folder (issue #4). Destinations are
  // limited to folders that already hold imported videos — a flat,
  // filterable list pulled from the library (GET /import/imported-folders),
  // so there's no slow filesystem walk. Pick a folder, confirm, and watch a
  // live progress bar. Same-drive moves are instant; cross-drive copies
  // report real bytes via /videos/{id}/move-progress.
  import { api } from '$lib/api';
  import type { Video, ImportedFolder, MoveProgress } from '$lib/types';

  interface Props {
    video: Video | null;
    show: boolean;
    onClose: () => void;
    // Fired with the refreshed Video after a successful move so the host
    // can patch its grid / player copy with the new FilePath.
    onMoved?: (updated: Video) => void | Promise<void>;
  }
  let { video, show, onClose, onMoved }: Props = $props();

  let folders = $state<ImportedFolder[]>([]);
  let filterText = $state('');
  let selected = $state<string | null>(null);
  let loading = $state(false);
  let error = $state<string | null>(null);
  let confirming = $state(false);
  let moving = $state(false);
  let progress = $state<MoveProgress | null>(null);
  let filterInput = $state<HTMLInputElement | null>(null);

  let shownFor: string | null = null;
  $effect(() => {
    if (show && video && shownFor !== video.id) {
      shownFor = video.id;
      error = null;
      progress = null;
      moving = false;
      confirming = false;
      selected = null;
      filterText = '';
      void loadFolders();
    } else if (!show) {
      shownFor = null;
    }
  });

  async function loadFolders() {
    loading = true;
    error = null;
    try {
      folders = await api.listImportedFolders();
      // Focus the filter box once the list is up so the user can type
      // straight away.
      queueMicrotask(() => filterInput?.focus());
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
  }

  // Normalize for comparison: strip trailing separators, lowercase.
  const trimSep = (p: string) => p.replace(/[/\\]+$/, '').toLowerCase();
  // The video's current folder — selecting it is a no-op (the server rejects
  // it too), so it's shown disabled.
  const currentFolder = $derived(
    video ? trimSep(video.filePath.replace(/[/\\][^/\\]*$/, '')) : ''
  );
  const isCurrent = (f: ImportedFolder) => trimSep(f.fullPath) === currentFolder;

  const filtered = $derived.by(() => {
    const q = filterText.trim().toLowerCase();
    if (!q) return folders;
    return folders.filter(
      (f) => f.label.toLowerCase().includes(q) || f.fullPath.toLowerCase().includes(q)
    );
  });

  function pick(f: ImportedFolder) {
    if (isCurrent(f)) return;
    selected = f.fullPath;
    confirming = false;
  }

  const selectedLabel = $derived(
    selected ? (folders.find((f) => f.fullPath === selected)?.label ?? selected) : null
  );

  async function doMove() {
    if (!video || !selected || moving) return;
    moving = true;
    confirming = false;
    error = null;
    progress = null;
    const id = video.id;
    const poll = setInterval(async () => {
      try {
        progress = await api.getMoveProgress(id);
      } catch {
        /* transient — keep polling */
      }
    }, 300);
    try {
      const updated = await api.moveVideo(id, selected);
      clearInterval(poll);
      await onMoved?.(updated);
      onClose();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      clearInterval(poll);
      moving = false;
    }
  }

  function fmtBytes(n: number): string {
    if (!n || n <= 0) return '';
    const u = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0;
    let x = n;
    while (x >= 1024 && i < u.length - 1) {
      x /= 1024;
      i++;
    }
    return `${x.toFixed(x >= 10 || i === 0 ? 0 : 1)} ${u[i]}`;
  }
  const pct = $derived(
    progress && progress.totalBytes > 0
      ? Math.round((progress.bytesCopied / progress.totalBytes) * 100)
      : null
  );
</script>

{#if show && video}
  <div class="modal modal-open" role="dialog" aria-modal="true">
    <div class="modal-box w-[80vw] max-w-[80vw]">
      <h3 class="font-bold text-lg">Move file</h3>
      <div class="text-sm mt-1 mb-3">
        <div class="font-medium break-all">{video.fileName}</div>
        <div class="text-xs text-base-content/60 break-all">{video.filePath}</div>
      </div>

      {#if error}
        <div class="alert alert-error text-sm mb-3">{error}</div>
      {/if}

      {#if moving}
        <!-- Live progress. Same-drive moves finish near-instantly (no
             determinate bar); cross-drive copies report a real %. -->
        <div class="py-4 space-y-2">
          <div class="text-sm">
            {progress?.phase === 'finalizing' ? 'Finalizing…' : 'Moving…'}
            {#if pct !== null}<span class="tabular-nums"> {pct}%</span>{/if}
          </div>
          {#if pct !== null}
            <progress class="progress progress-primary w-full" value={pct} max="100"></progress>
            <div class="text-xs text-base-content/60 tabular-nums">
              {fmtBytes(progress?.bytesCopied ?? 0)} / {fmtBytes(progress?.totalBytes ?? 0)}
            </div>
          {:else}
            <progress class="progress progress-primary w-full"></progress>
          {/if}
        </div>
      {:else}
        <div class="text-xs uppercase tracking-wide text-base-content/60 mb-1">
          Destination folder
        </div>
        <input
          bind:this={filterInput}
          type="text"
          class="input input-sm input-bordered w-full mb-2"
          placeholder="Filter folders…"
          bind:value={filterText}
        />
        <div class="border border-base-300 rounded-box overflow-hidden">
          <div class="max-h-64 overflow-y-auto">
            {#if loading}
              <div class="px-3 py-3 text-sm text-base-content/60">
                <span class="loading loading-spinner loading-xs"></span> Loading folders…
              </div>
            {:else if folders.length === 0}
              <div class="px-3 py-3 text-sm text-base-content/50 italic">
                No imported folders yet.
              </div>
            {:else if filtered.length === 0}
              <div class="px-3 py-3 text-sm text-base-content/50 italic">
                No folders match “{filterText}”.
              </div>
            {:else}
              {#each filtered as f (f.fullPath)}
                {@const current = isCurrent(f)}
                <button
                  type="button"
                  class="w-full text-left px-3 py-1.5 text-sm flex items-center gap-2 {selected ===
                  f.fullPath
                    ? 'bg-primary/15'
                    : 'hover:bg-base-200'} {current ? 'opacity-40 cursor-not-allowed' : ''}"
                  onclick={() => pick(f)}
                  disabled={current}
                  title={f.fullPath}
                >
                  <span aria-hidden="true">📁</span>
                  <span class="flex-1 min-w-0 truncate">{f.label}</span>
                  {#if current}
                    <span class="text-xs italic text-base-content/60 shrink-0">current</span>
                  {/if}
                  <span class="text-xs text-base-content/50 tabular-nums shrink-0">{f.videoCount}</span>
                </button>
              {/each}
            {/if}
          </div>
        </div>

        {#if confirming && selectedLabel}
          <p class="text-sm text-info mt-3">
            Move <span class="font-medium">{video.fileName}</span> into
            <span class="font-medium">{selectedLabel}</span>? This can be undone from the Moves
            page.
          </p>
        {/if}
      {/if}

      <div class="modal-action">
        <button class="btn btn-sm btn-cancel" onclick={onClose} disabled={moving}>Cancel</button>
        {#if !moving}
          <button
            class="btn btn-sm btn-soft btn-primary btn-cta"
            onclick={() => (confirming ? doMove() : (confirming = true))}
            disabled={!selected}
            title={selected ? `Move into ${selectedLabel}` : 'Select a destination folder first'}
          >{confirming ? 'Confirm move' : 'Move here'}</button>
        {/if}
      </div>
    </div>
    <button
      type="button"
      class="modal-backdrop"
      aria-label="Cancel move"
      onclick={() => {
        if (!moving) onClose();
      }}
    ></button>
  </div>
{/if}

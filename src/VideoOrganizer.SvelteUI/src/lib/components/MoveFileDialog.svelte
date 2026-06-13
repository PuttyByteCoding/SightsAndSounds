<script lang="ts">
  // Move a video's file into another folder (issue #4). Browse the
  // configured sources/folders (reusing /import/browse), pick a
  // destination, confirm, and watch a live progress bar while the move
  // runs. Same-drive moves are instant; cross-drive copies report real
  // bytes via /videos/{id}/move-progress.
  import { api } from '$lib/api';
  import type { Video, ImportBrowseDirectory, MoveProgress } from '$lib/types';

  interface Props {
    video: Video | null;
    show: boolean;
    onClose: () => void;
    // Fired with the refreshed Video after a successful move so the host
    // can patch its grid / player copy with the new FilePath.
    onMoved?: (updated: Video) => void | Promise<void>;
  }
  let { video, show, onClose, onMoved }: Props = $props();

  let currentPath = $state<string>(''); // '' = the sources root
  let parentPath = $state<string | null>(null);
  let dirs = $state<ImportBrowseDirectory[]>([]);
  let loading = $state(false);
  let error = $state<string | null>(null);
  let confirming = $state(false);
  let moving = $state(false);
  let progress = $state<MoveProgress | null>(null);

  let shownFor: string | null = null;
  $effect(() => {
    if (show && video && shownFor !== video.id) {
      shownFor = video.id;
      error = null;
      progress = null;
      moving = false;
      confirming = false;
      void browse('');
    } else if (!show) {
      shownFor = null;
    }
  });

  async function browse(path: string) {
    confirming = false;
    loading = true;
    error = null;
    try {
      const res = await api.browseImport(path.length ? path : null);
      dirs = res.directories;
      currentPath = res.currentPath ?? '';
      parentPath = res.parentPath;
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
  }

  // The video's current folder — don't allow "moving" it to where it
  // already lives (the server rejects this too, but disable up front).
  const trimSep = (p: string) => p.replace(/[/\\]+$/, '').toLowerCase();
  const currentFolder = $derived(video ? video.filePath.replace(/[/\\][^/\\]*$/, '') : '');
  const canMoveHere = $derived(
    currentPath.length > 0 && trimSep(currentPath) !== trimSep(currentFolder)
  );

  async function doMove() {
    if (!video || !canMoveHere || moving) return;
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
      const updated = await api.moveVideo(id, currentPath);
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
    <div class="modal-box max-w-lg">
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
        <div class="text-xs uppercase tracking-wide text-base-content/60 mb-1">Destination folder</div>
        <div class="border border-base-300 rounded-box overflow-hidden">
          <div class="px-3 py-2 bg-base-200 text-xs font-mono break-all border-b border-base-300">
            {currentPath.length ? currentPath : 'Sources'}
          </div>
          <div class="max-h-64 overflow-y-auto">
            {#if currentPath.length}
              <button
                type="button"
                class="w-full text-left px-3 py-1.5 hover:bg-base-200 text-sm flex items-center gap-2"
                onclick={() => browse(parentPath ?? '')}
                disabled={loading}
              ><span aria-hidden="true">↰</span> ..</button>
            {/if}
            {#if loading}
              <div class="px-3 py-3 text-sm text-base-content/60">
                <span class="loading loading-spinner loading-xs"></span> Loading…
              </div>
            {:else if dirs.length === 0}
              <div class="px-3 py-3 text-sm text-base-content/50 italic">No subfolders here.</div>
            {:else}
              {#each dirs as d (d.fullPath)}
                <button
                  type="button"
                  class="w-full text-left px-3 py-1.5 hover:bg-base-200 text-sm flex items-center gap-2"
                  onclick={() => browse(d.fullPath)}
                  disabled={loading}
                >
                  <span aria-hidden="true">📁</span>
                  <span class="flex-1 min-w-0 truncate">{d.name}</span>
                  {#if d.videoCount > 0}
                    <span class="text-xs text-base-content/50 tabular-nums shrink-0">{d.videoCount}</span>
                  {/if}
                </button>
              {/each}
            {/if}
          </div>
        </div>

        {#if confirming}
          <p class="text-sm text-info mt-3">
            Move <span class="font-medium">{video.fileName}</span> into
            <span class="font-mono break-all">{currentPath}</span>? This can be undone from the Moves page.
          </p>
        {/if}
      {/if}

      <div class="modal-action">
        <button class="btn btn-sm btn-cancel" onclick={onClose} disabled={moving}>Cancel</button>
        {#if !moving}
          <button
            class="btn btn-sm btn-soft btn-primary btn-cta"
            onclick={() => (confirming ? doMove() : (confirming = true))}
            disabled={!canMoveHere}
            title={canMoveHere
              ? `Move into ${currentPath}`
              : 'Navigate into a destination folder first'}
          >{confirming ? 'Confirm move' : 'Move here'}</button>
        {/if}
      </div>
    </div>
    <button
      type="button"
      class="modal-backdrop"
      aria-label="Cancel move"
      onclick={() => { if (!moving) onClose(); }}
    ></button>
  </div>
{/if}

<script lang="ts">
  // Database backups (issue #32). Its own page ahead of the config-page split.
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import type { BackupInfo, BackupSettings, VideoSet, ReRootPreview } from '$lib/types';

  let backups = $state<BackupInfo[]>([]);
  let settings = $state<BackupSettings | null>(null);
  let dirInput = $state('');
  let savingDir = $state(false);
  let dirError = $state<string | null>(null);
  let dirSaved = $state(false);

  let snapshotBusy = $state(false);
  let snapshotMsg = $state<string | null>(null);
  let listError = $state<string | null>(null);

  // --- Restore --------------------------------------------------------------
  // Replacing the whole DB is destructive, so it routes through a confirm
  // modal. The server takes a pre-restore safety snapshot first. After a
  // restore we re-check each source's path — a snapshot from another machine
  // (e.g. a Windows backup restored on Linux) carries that machine's paths,
  // so any source whose folder isn't reachable here is offered a re-root.
  let restoreConfirm = $state<BackupInfo | null>(null);
  let restoreBusy = $state(false);
  let restoreResult = $state<{
    restoredFrom: string;
    safetySnapshot: string;
    videos: number;
    tags: number;
    videoSets: number;
  } | null>(null);

  // Per-unreachable-source path-fix state (re-root with spot check).
  type PathFix = {
    input: string;
    preview: ReRootPreview | null;
    checking: boolean;
    moving: boolean;
    error: string | null;
  };
  let unreachableSets = $state<VideoSet[]>([]);
  let pathFix = $state<Record<string, PathFix>>({});

  function askRestore(b: BackupInfo) {
    restoreResult = null;
    restoreConfirm = b;
  }

  async function doRestore() {
    const b = restoreConfirm;
    if (!b) return;
    restoreBusy = true;
    listError = null;
    try {
      restoreResult = await api.restoreBackup(b.fileName);
      restoreConfirm = null;
      await refreshList(); // the safety snapshot shows up as a new backup
      await checkSourcePaths();
    } catch (e: any) {
      listError = e?.message ?? 'Restore failed';
      restoreConfirm = null;
    } finally {
      restoreBusy = false;
    }
  }

  // Find sources whose folder doesn't exist on this machine — the ones that
  // need re-pointing after restoring a backup from elsewhere.
  async function checkSourcePaths() {
    try {
      const sets = await api.listVideoSets();
      unreachableSets = sets.filter((s) => s.pathExists === false);
      const next: Record<string, PathFix> = {};
      for (const s of unreachableSets) {
        next[s.id] = { input: s.path, preview: null, checking: false, moving: false, error: null };
      }
      pathFix = next;
    } catch {
      /* non-fatal — the user can still fix paths from the Sources page */
    }
  }

  async function checkPath(s: VideoSet) {
    const f = pathFix[s.id];
    if (!f) return;
    const next = f.input.trim();
    if (!next) { f.error = 'Enter a path.'; return; }
    f.checking = true;
    f.error = null;
    f.preview = null;
    try {
      f.preview = await api.reRootVideoSetPreview(s.id, next);
    } catch (e: any) {
      f.error = e?.message ?? 'Check failed';
    } finally {
      f.checking = false;
    }
  }

  async function movePath(s: VideoSet) {
    const f = pathFix[s.id];
    if (!f) return;
    f.moving = true;
    f.error = null;
    try {
      await api.reRootVideoSet(s.id, f.input.trim());
      unreachableSets = unreachableSets.filter((x) => x.id !== s.id);
      delete pathFix[s.id];
      pathFix = { ...pathFix };
    } catch (e: any) {
      f.error = e?.message ?? 'Move failed';
      f.moving = false;
    }
  }

  async function loadAll() {
    try {
      [settings, backups] = await Promise.all([api.getBackupSettings(), api.listBackups()]);
      dirInput = settings.directory;
    } catch (e: any) {
      listError = e?.message ?? 'Failed to load backups';
    }
  }
  onMount(loadAll);

  async function saveDirectory() {
    savingDir = true;
    dirError = null;
    dirSaved = false;
    try {
      settings = await api.setBackupDirectory(dirInput.trim());
      dirInput = settings.directory;
      dirSaved = true;
      await refreshList();
    } catch (e: any) {
      dirError = e?.message ?? 'Failed to save directory';
    } finally {
      savingDir = false;
    }
  }

  async function refreshList() {
    try {
      backups = await api.listBackups();
    } catch (e: any) {
      listError = e?.message ?? 'Failed to load backups';
    }
  }

  async function quickSnapshot() {
    snapshotBusy = true;
    snapshotMsg = null;
    dirError = null;
    try {
      const info = await api.createBackupSnapshot();
      snapshotMsg = `Snapshot saved: ${info.fileName}`;
      await refreshList();
    } catch (e: any) {
      snapshotMsg = null;
      listError = e?.message ?? 'Snapshot failed';
    } finally {
      snapshotBusy = false;
    }
  }

  async function removeBackup(fileName: string) {
    if (!confirm(`Delete backup “${fileName}”?`)) return;
    try {
      await api.deleteBackup(fileName);
      await refreshList();
    } catch (e: any) {
      listError = e?.message ?? 'Delete failed';
    }
  }

  function fmtSize(n: number): string {
    if (n < 1024) return `${n} B`;
    const u = ['KB', 'MB', 'GB'];
    let i = -1;
    let x = n;
    do { x /= 1024; i++; } while (x >= 1024 && i < u.length - 1);
    return `${x.toFixed(1)} ${u[i]}`;
  }
</script>

<div class="p-6 max-w-4xl space-y-8">
  <header>
    <h1 class="text-2xl font-semibold">Backups</h1>
    <p class="text-sm text-base-content/70 mt-1">
      Back up the database (tags, sources, videos, and their metadata) to a file
      you can keep or move elsewhere. Your video files on disk are not part of a
      backup.
    </p>
  </header>

  <!-- Storage location -->
  <section class="space-y-2">
    <h2 class="text-lg font-medium">Storage location</h2>
    <p class="text-sm text-base-content/70">
      Backups are written to this folder on the machine running the server.
    </p>
    <div class="flex flex-wrap items-center gap-2">
      <input
        type="text"
        class="input input-bordered input-sm flex-1 min-w-80 font-mono text-xs"
        bind:value={dirInput}
        placeholder="C:\\Backups\\SightsAndSounds"
        spellcheck="false"
      />
      <button
        class="btn btn-sm btn-primary"
        onclick={saveDirectory}
        disabled={savingDir || dirInput.trim().length === 0 || dirInput.trim() === settings?.directory}
      >
        {#if savingDir}<span class="loading loading-spinner loading-xs"></span>{/if}
        Save
      </button>
    </div>
    {#if dirError}
      <p class="text-sm text-error">{dirError}</p>
    {:else if dirSaved}
      <p class="text-sm text-success">Saved.</p>
    {/if}
    {#if settings && !settings.writable}
      <p class="text-sm text-warning">
        ⚠ This folder isn't writable{settings.error ? `: ${settings.error}` : ''}.
      </p>
    {/if}
  </section>

  <!-- Create a backup -->
  <section class="space-y-2">
    <h2 class="text-lg font-medium">Create a backup</h2>
    <div class="flex flex-wrap items-center gap-3">
      <button
        class="btn btn-sm btn-primary"
        onclick={quickSnapshot}
        disabled={snapshotBusy || (settings ? !settings.writable : false)}
      >
        {#if snapshotBusy}<span class="loading loading-spinner loading-xs"></span>{/if}
        Quick snapshot (JSON)
      </button>
      {#if snapshotMsg}<span class="text-sm text-success">{snapshotMsg}</span>{/if}
    </div>
  </section>

  <!-- Existing backups -->
  <section class="space-y-2">
    <h2 class="text-lg font-medium">Existing backups</h2>
    {#if listError}
      <p class="text-sm text-error">{listError}</p>
    {/if}
    {#if backups.length === 0}
      <p class="text-sm text-base-content/50 italic">No backups yet.</p>
    {:else}
      <div class="overflow-x-auto">
        <table class="table table-sm w-auto">
          <thead>
            <tr>
              <th>File</th>
              <th>Type</th>
              <th class="text-right">Size</th>
              <th>Created</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {#each backups as b (b.fileName)}
              <tr>
                <td class="font-mono text-xs break-all">{b.fileName}</td>
                <td class="uppercase text-xs">{b.type}</td>
                <td class="text-right tabular-nums">{fmtSize(b.sizeBytes)}</td>
                <td class="text-xs">{new Date(b.createdUtc).toLocaleString()}</td>
                <td class="whitespace-nowrap">
                  {#if b.type === 'json'}
                    <button class="btn btn-ghost btn-xs" onclick={() => askRestore(b)}>
                      Restore
                    </button>
                  {/if}
                  <a class="btn btn-ghost btn-xs" href={api.backupDownloadUrl(b.fileName)} download>
                    Download
                  </a>
                  <button class="btn btn-ghost btn-xs text-error" onclick={() => removeBackup(b.fileName)}>
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

  <!-- Post-restore summary + path fix. After restoring a backup taken on
       another machine, sources whose folders aren't reachable here are
       listed for re-pointing (re-root with a spot check). -->
  {#if restoreResult}
    <section class="space-y-3">
      <div class="alert alert-success text-sm">
        Restored from <code>{restoreResult.restoredFrom}</code> —
        {restoreResult.videoSets} sources, {restoreResult.videos} videos, {restoreResult.tags} tags.
        A pre-restore safety snapshot was saved as
        <code>{restoreResult.safetySnapshot}</code>.
      </div>

      {#if unreachableSets.length > 0}
        <div class="space-y-2">
          <h2 class="text-lg font-medium">Re-point moved sources</h2>
          <p class="text-sm text-base-content/70">
            These sources' folders aren't reachable on this machine (expected when
            restoring a backup from another computer). Point each at its location
            here — the videos under it are re-pointed with it.
          </p>
          {#each unreachableSets as s (s.id)}
            {@const f = pathFix[s.id]}
            {#if f}
              <div class="card bg-base-200 p-3 space-y-2">
                <div class="text-sm font-medium">{s.name}</div>
                <div class="text-xs text-base-content/60">
                  Was: <code class="font-mono break-all">{s.path}</code>
                </div>
                <div class="flex flex-wrap items-center gap-2">
                  <input
                    type="text"
                    class="input input-bordered input-sm flex-1 min-w-72 font-mono text-xs"
                    bind:value={f.input}
                    placeholder="/mnt/videos/..."
                    spellcheck="false"
                  />
                  <button
                    class="btn btn-sm"
                    onclick={() => checkPath(s)}
                    disabled={f.checking || f.moving}
                  >
                    {#if f.checking}<span class="loading loading-spinner loading-xs"></span>{/if}
                    Check
                  </button>
                  <button
                    class="btn btn-sm btn-primary"
                    onclick={() => movePath(s)}
                    disabled={f.moving || f.checking}
                    title="Re-point this source and its videos to the new location"
                  >
                    {#if f.moving}<span class="loading loading-spinner loading-xs"></span>{/if}
                    Move &amp; re-point
                  </button>
                </div>
                {#if f.preview}
                  {@const allFound = f.preview.sampled > 0 && f.preview.missing === 0}
                  <div class="text-xs {allFound ? 'text-success' : f.preview.sampled === 0 ? 'text-base-content/60' : 'text-warning'}">
                    {#if f.preview.sampled === 0}
                      No videos stored under this source — only the path changes.
                    {:else}
                      Spot check: {f.preview.found}/{f.preview.sampled} sampled files found at the new location
                      ({f.preview.totalAffected} {f.preview.totalAffected === 1 ? 'video' : 'videos'} will move).
                      {#if !f.preview.newBaseExists}· ⚠ folder not reachable yet{/if}
                    {/if}
                  </div>
                {/if}
                {#if f.error}<div class="text-xs text-error">{f.error}</div>{/if}
              </div>
            {/if}
          {/each}
        </div>
      {:else}
        <p class="text-sm text-success">All sources are reachable on this machine.</p>
      {/if}
    </section>
  {/if}
</div>

<!-- Restore confirmation — destructive, replaces the whole database. -->
{#if restoreConfirm}
  {@const b = restoreConfirm}
  <div class="modal modal-open" role="dialog" aria-modal="true" aria-labelledby="restore-title">
    <div class="modal-box space-y-3">
      <h3 id="restore-title" class="text-lg font-bold">Restore this backup?</h3>
      <p class="text-sm text-base-content/80">
        Replace the <span class="font-semibold">entire current database</span> with
        the contents of <code class="font-mono break-all">{b.fileName}</code>.
      </p>
      <div class="alert alert-warning text-sm">
        Everything in the database now — tags, sources, videos, and their metadata
        — is overwritten. A safety snapshot of the current database is taken first,
        so this can be undone by restoring that. Your video files on disk are not
        touched.
      </div>
      <div class="modal-action">
        <!-- svelte-ignore a11y_autofocus -->
        <button
          type="button"
          class="btn btn-cancel"
          onclick={() => (restoreConfirm = null)}
          disabled={restoreBusy}
          autofocus
        >Cancel</button>
        <button
          type="button"
          class="btn btn-soft btn-warning border border-warning/50"
          onclick={doRestore}
          disabled={restoreBusy}
        >
          {#if restoreBusy}<span class="loading loading-spinner loading-xs"></span>{/if}
          Restore
        </button>
      </div>
    </div>
  </div>
{/if}

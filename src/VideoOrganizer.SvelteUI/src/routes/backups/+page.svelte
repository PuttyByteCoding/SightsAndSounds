<script lang="ts">
  // Database backups (issue #32). Its own page ahead of the config-page split.
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import type { BackupInfo, BackupSettings } from '$lib/types';

  let backups = $state<BackupInfo[]>([]);
  let settings = $state<BackupSettings | null>(null);
  let dirInput = $state('');
  let savingDir = $state(false);
  let dirError = $state<string | null>(null);
  let dirSaved = $state(false);

  let snapshotBusy = $state(false);
  let snapshotMsg = $state<string | null>(null);
  let listError = $state<string | null>(null);

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
    <p class="text-xs text-base-content/50">
      A full backup to SQLite is coming in a follow-up.
    </p>
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
</div>

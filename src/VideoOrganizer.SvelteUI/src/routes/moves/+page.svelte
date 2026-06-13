<script lang="ts">
  // File-move log + Undo (issue #4). Lists moves newest-first; each
  // not-yet-reverted move can be undone — the file goes back to its
  // original folder and the DB path is restored.
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import type { FileMoveLog } from '$lib/types';

  let moves = $state<FileMoveLog[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let revertingId = $state<string | null>(null);

  async function load() {
    loading = true;
    error = null;
    try {
      moves = await api.listFileMoves(200);
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
  }
  onMount(load);

  async function undo(m: FileMoveLog) {
    if (revertingId) return;
    revertingId = m.id;
    error = null;
    try {
      await api.revertFileMove(m.id);
      await load();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      revertingId = null;
    }
  }

  function fmtWhen(iso: string): string {
    const d = new Date(iso);
    return isNaN(d.getTime()) ? iso : d.toLocaleString();
  }
</script>

<svelte:head><title>Moves - Video Organizer</title></svelte:head>

<div class="max-w-5xl mx-auto space-y-4">
  <header>
    <h1 class="text-2xl font-semibold">File Moves</h1>
    <p class="text-sm text-base-content/70 mt-1">
      Every file move is logged here and can be undone — the file goes back
      to its original folder and the database path is restored.
    </p>
  </header>

  {#if error}
    <div class="alert alert-error text-sm">{error}</div>
  {/if}

  {#if loading}
    <div class="flex items-center gap-2 text-base-content/70">
      <span class="loading loading-spinner loading-sm"></span> Loading…
    </div>
  {:else if moves.length === 0}
    <div class="text-base-content/60 italic">No file moves yet.</div>
  {:else}
    <div class="overflow-x-auto">
      <table class="table table-sm">
        <thead>
          <tr>
            <th>File</th>
            <th>From → To</th>
            <th>When</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {#each moves as m (m.id)}
            <tr class={m.revertedAt ? 'opacity-60' : ''}>
              <td class="font-medium break-all">{m.fileName}</td>
              <td class="text-xs font-mono break-all">
                <div class="text-base-content/60">{m.fromPath}</div>
                <div>→ {m.toPath}</div>
              </td>
              <td class="text-sm whitespace-nowrap tabular-nums">{fmtWhen(m.movedAt)}</td>
              <td class="text-right whitespace-nowrap">
                {#if m.revertedAt}
                  <span class="badge badge-ghost badge-sm" title="Undone {fmtWhen(m.revertedAt)}">Undone</span>
                {:else}
                  <button
                    class="btn btn-xs btn-soft btn-warning border border-warning/50"
                    onclick={() => undo(m)}
                    disabled={revertingId !== null}
                  >
                    {#if revertingId === m.id}<span class="loading loading-spinner loading-xs"></span>{/if}
                    Undo
                  </button>
                {/if}
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  {/if}
</div>

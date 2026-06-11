<script lang="ts">
  // Duplicate review page (issue #6). Pairs flagged from the browse
  // page's duplicate-hunt flow arrive here as Pending. Each pair
  // renders a property-by-property comparison — matching rows tinted
  // green, differing rows amber — so "are these really the same
  // content?" is answerable at a glance. Confirm / Reject record the
  // verdict; Reopen undoes a mis-click; Delete drops the pair
  // entirely.
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import type { DuplicateCandidate, DuplicateStatus, Video } from '$lib/types';

  type TabKey = DuplicateStatus;
  const TABS: ReadonlyArray<{ key: TabKey; label: string }> = [
    { key: 'pending',   label: 'Pending' },
    { key: 'confirmed', label: 'Confirmed' },
    { key: 'rejected',  label: 'Rejected' }
  ];
  let activeTab = $state<TabKey>('pending');

  let candidates = $state<DuplicateCandidate[]>([]);
  let counts = $state<Record<TabKey, number>>({ pending: 0, confirmed: 0, rejected: 0 });
  let loading = $state(false);
  let error = $state<string | null>(null);
  // Per-candidate in-flight set so one row's spinner doesn't freeze
  // the rest, and double-clicks can't double-fire.
  let busy = $state<Set<string>>(new Set());

  async function load() {
    loading = true;
    error = null;
    try {
      const all = await api.listDuplicates('all');
      counts = {
        pending: all.filter(c => c.status === 'pending').length,
        confirmed: all.filter(c => c.status === 'confirmed').length,
        rejected: all.filter(c => c.status === 'rejected').length
      };
      candidates = all;
    } catch (e: any) {
      error = e?.message ?? 'Failed to load duplicates';
    } finally {
      loading = false;
    }
  }

  const visible = $derived(candidates.filter(c => c.status === activeTab));

  function setBusy(id: string, on: boolean) {
    const next = new Set(busy);
    if (on) next.add(id);
    else next.delete(id);
    busy = next;
  }

  async function transition(c: DuplicateCandidate, action: 'confirm' | 'reject' | 'reopen') {
    if (busy.has(c.id)) return;
    setBusy(c.id, true);
    try {
      const updated = action === 'confirm' ? await api.confirmDuplicate(c.id)
        : action === 'reject' ? await api.rejectDuplicate(c.id)
        : await api.reopenDuplicate(c.id);
      candidates = candidates.map(x => (x.id === c.id ? updated : x));
      counts = {
        pending: candidates.filter(x => x.status === 'pending').length,
        confirmed: candidates.filter(x => x.status === 'confirmed').length,
        rejected: candidates.filter(x => x.status === 'rejected').length
      };
    } catch (e: any) {
      error = e?.message ?? `Failed to ${action}`;
    } finally {
      setBusy(c.id, false);
    }
  }

  async function remove(c: DuplicateCandidate) {
    if (busy.has(c.id)) return;
    setBusy(c.id, true);
    try {
      await api.deleteDuplicate(c.id);
      candidates = candidates.filter(x => x.id !== c.id);
      counts = { ...counts, [c.status]: Math.max(0, counts[c.status] - 1) };
    } catch (e: any) {
      error = e?.message ?? 'Failed to delete';
    } finally {
      setBusy(c.id, false);
    }
  }

  // --- Formatting helpers --------------------------------------------------

  function formatBytes(bytes: number): string {
    if (!Number.isFinite(bytes) || bytes <= 0) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0;
    let v = bytes;
    while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
    return `${v.toFixed(v >= 100 || i === 0 ? 0 : 1)} ${units[i]}`;
  }

  // ".NET TimeSpan: [d.]hh:mm:ss[.fffffff]" → "M:SS" / "H:MM:SS".
  function formatDuration(ts: string | null | undefined): string {
    if (!ts) return '—';
    const m = ts.match(/^(?:(\d+)\.)?(\d+):(\d{2}):(\d{2})(?:\.(\d+))?$/);
    if (!m) return ts;
    const hours = parseInt(m[2], 10) + (m[1] ? parseInt(m[1], 10) * 24 : 0);
    const mins = parseInt(m[3], 10);
    const secs = parseInt(m[4], 10);
    if (hours > 0) return `${hours}:${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')}`;
    return `${mins}:${String(secs).padStart(2, '0')}`;
  }

  function folderOf(path: string): string {
    const i = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'));
    return i >= 0 ? path.slice(0, i) : '';
  }

  // Comparison rows. `same` drives the row tint: green when the two
  // sides agree (evidence FOR a duplicate), amber when they differ.
  interface CompareRow { label: string; a: string; b: string; same: boolean; }
  function compareRows(c: DuplicateCandidate): CompareRow[] {
    const A = c.videoA, B = c.videoB;
    const row = (label: string, fa: (v: Video) => string): CompareRow => {
      const a = fa(A), b = fa(B);
      return { label, a, b, same: a === b };
    };
    return [
      row('File name', v => v.fileName),
      row('Folder', v => folderOf(v.filePath)),
      row('Size', v => formatBytes(v.fileSize)),
      row('Duration', v => formatDuration(v.duration)),
      row('Resolution', v => `${v.width}×${v.height}`),
      row('Codec', v => v.videoCodec),
      row('Bitrate', v => v.bitrate > 0 ? `${Math.round(v.bitrate / 1000)} kbps` : '—'),
      row('Frame rate', v => v.frameRate > 0 ? `${v.frameRate}` : '—'),
      row('Watch count', v => String(v.watchCount)),
      row('Tags', v => v.tags.map(t => t.name).join(', ') || '—')
    ];
  }

  // MD5 verdict: both hashed AND equal is the strongest possible
  // "identical content" signal, worth surfacing above the table.
  function md5Verdict(c: DuplicateCandidate): 'identical' | 'different' | 'unknown' {
    const a = c.videoA.md5, b = c.videoB.md5;
    if (!a || !b) return 'unknown';
    return a.toLowerCase() === b.toLowerCase() ? 'identical' : 'different';
  }

  onMount(load);
</script>

<svelte:head><title>Duplicates - Video Organizer</title></svelte:head>

<div class="flex flex-col gap-4">
  <div class="flex items-center gap-3 flex-wrap">
    <h1 class="text-2xl font-semibold">Duplicates</h1>
    {#if loading}<span class="loading loading-dots loading-sm"></span>{/if}
    <a class="btn btn-sm ml-auto" href="/browse" title="Flag pairs from the player — 🎯 Find duplicates">+ Find more in the player</a>
  </div>

  <p class="text-sm text-base-content/60">
    Pairs flagged from the player's <span class="font-medium">🎯 Find duplicates</span> hunt.
    Compare the properties, then Confirm or Reject each pair.
  </p>

  {#if error}
    <div class="alert alert-error">
      <span>{error}</span>
      <button class="btn btn-sm" onclick={() => (error = null)}>Dismiss</button>
    </div>
  {/if}

  <div role="tablist" class="tabs tabs-bordered">
    {#each TABS as t (t.key)}
      <button
        role="tab"
        class="tab {activeTab === t.key ? 'tab-active' : ''}"
        onclick={() => (activeTab = t.key)}
      >
        {t.label}
        <span class="badge badge-sm badge-ghost ml-1 tabular-nums">{counts[t.key]}</span>
      </button>
    {/each}
  </div>

  {#if !loading && visible.length === 0}
    <p class="text-base-content/60">
      {#if activeTab === 'pending'}
        No pending pairs. Start a duplicate hunt from the player (🎯 Find duplicates).
      {:else}
        Nothing here yet.
      {/if}
    </p>
  {/if}

  {#each visible as c (c.id)}
    {@const rows = compareRows(c)}
    {@const verdict = md5Verdict(c)}
    {@const isBusy = busy.has(c.id)}
    <div class="card bg-base-200 p-4 space-y-3">
      <div class="flex items-center gap-2 flex-wrap">
        {#if verdict === 'identical'}
          <span class="badge badge-success" title="Both files hash to the same MD5 — byte-identical content">MD5 match — identical content</span>
        {:else if verdict === 'different'}
          <span class="badge badge-warning" title="Different MD5 — files differ at byte level (may still be the same content re-encoded)">MD5 differs</span>
        {:else}
          <span class="badge badge-ghost" title="At least one file hasn't been hashed yet">MD5 unknown</span>
        {/if}
        <span class="text-xs text-base-content/50">flagged {new Date(c.createdAt).toLocaleString()}</span>
        <div class="ml-auto flex items-center gap-2">
          {#if c.status === 'pending'}
            <button class="btn btn-success btn-sm" disabled={isBusy} onclick={() => transition(c, 'confirm')}>
              {#if isBusy}<span class="loading loading-spinner loading-xs"></span>{/if}
              Confirm duplicate
            </button>
            <button class="btn btn-sm" disabled={isBusy} onclick={() => transition(c, 'reject')}>Not a duplicate</button>
          {:else}
            <span class="badge {c.status === 'confirmed' ? 'badge-success' : 'badge-neutral'} uppercase">{c.status}</span>
            <button class="btn btn-sm" disabled={isBusy} onclick={() => transition(c, 'reopen')}>Reopen</button>
          {/if}
          <button
            class="btn btn-ghost btn-sm text-base-content/60 hover:text-error"
            disabled={isBusy}
            onclick={() => remove(c)}
            title="Forget this pair entirely"
          >Delete</button>
        </div>
      </div>

      <div class="overflow-x-auto">
        <table class="table table-sm">
          <thead>
            <tr>
              <th class="w-32"></th>
              <th>
                <a class="link link-hover" href="/browse?id={c.videoA.id}" title="Open in player">▶ {c.videoA.fileName}</a>
              </th>
              <th>
                <a class="link link-hover" href="/browse?id={c.videoB.id}" title="Open in player">▶ {c.videoB.fileName}</a>
              </th>
            </tr>
          </thead>
          <tbody>
            {#each rows as r (r.label)}
              <tr class={r.same ? 'bg-success/10' : 'bg-warning/10'}>
                <td class="font-medium whitespace-nowrap">
                  {r.label}
                  {#if r.same}
                    <span class="text-success ml-1" title="Identical">=</span>
                  {:else}
                    <span class="text-warning ml-1" title="Differs">≠</span>
                  {/if}
                </td>
                <td class="break-all">{r.a}</td>
                <td class="break-all">{r.b}</td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
    </div>
  {/each}
</div>

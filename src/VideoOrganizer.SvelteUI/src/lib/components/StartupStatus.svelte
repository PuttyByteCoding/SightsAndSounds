<script lang="ts">
  import { onMount, onDestroy } from 'svelte';
  import { api } from '$lib/api';

  // Startup load diagnostics (issue #71). Times the data fetches that drive
  // first paint, flags anything over 2 seconds, and surfaces errors. Used as
  // the temporary landing page (routes/+page.svelte) AND as the slow-load
  // dialog that the Videos page pops when /browse doesn't finish in 2s.
  type ProbeDef = { name: string; run: () => Promise<unknown>; summarize?: (r: any) => string };
  type Probe = ProbeDef & {
    status: 'running' | 'done' | 'error';
    startMs: number;
    durationMs: number | null;
    detail: string;
  };

  const defs: ProbeDef[] = [
    { name: 'Tag groups', run: () => api.listTagGroups(), summarize: (r) => `${r.length} groups` },
    {
      name: 'Tags for the filter tree (with counts)',
      run: () => api.listTags({ withCounts: true }),
      summarize: (r) => `${r.length} tags`
    },
    { name: 'Video count', run: () => api.getVideoCount(), summarize: (r) => `${r} videos` },
    { name: 'Flag counts', run: () => api.getFlagCounts() },
    { name: 'Import jobs / queue', run: () => api.listImportJobs(), summarize: (r) => `${r.length} job(s)` },
    {
      name: 'Thumbnail worker status',
      run: () => api.getThumbnailStatus(),
      summarize: (r) => `${r.warmed}/${r.total} warmed`
    },
    {
      name: 'MD5 worker status',
      run: () => api.getMd5BackfillStatus(),
      summarize: (r) => `${r.hashed}/${r.total} hashed`
    },
    { name: 'Runtime info', run: () => api.getRuntimeInfo() }
  ];

  let probes = $state<Probe[]>([]);
  let now = $state(Date.now());
  let ticker: ReturnType<typeof setInterval> | null = null;

  function runAll() {
    const start = Date.now();
    probes = defs.map((d) => ({
      ...d,
      status: 'running' as const,
      startMs: start,
      durationMs: null,
      detail: ''
    }));
    probes.forEach((p, i) => {
      p.run()
        .then((r) => {
          probes[i].status = 'done';
          probes[i].durationMs = Date.now() - probes[i].startMs;
          try {
            probes[i].detail = p.summarize ? p.summarize(r) : '';
          } catch {
            probes[i].detail = '';
          }
        })
        .catch((e) => {
          probes[i].status = 'error';
          probes[i].durationMs = Date.now() - probes[i].startMs;
          probes[i].detail = e instanceof Error ? e.message : String(e);
        });
    });
  }

  onMount(() => {
    runAll();
    // Drives the live elapsed time for still-running (possibly hanging) probes.
    ticker = setInterval(() => (now = Date.now()), 250);
  });
  onDestroy(() => {
    if (ticker) clearInterval(ticker);
  });

  const elapsed = (p: Probe) => p.durationMs ?? now - p.startMs;
  const fmtMs = (ms: number) => (ms >= 1000 ? `${(ms / 1000).toFixed(1)}s` : `${ms} ms`);
  const allDone = $derived(probes.length > 0 && probes.every((p) => p.status !== 'running'));
</script>

<div class="space-y-3">
  <div class="flex flex-wrap items-center gap-3">
    <button class="btn btn-sm" onclick={runAll}>Re-run</button>
    {#if allDone}<span class="text-sm text-success">All checks finished.</span>{/if}
  </div>

  <div class="overflow-x-auto">
    <table class="table table-sm">
      <thead>
        <tr>
          <th class="w-6"></th>
          <th>Component</th>
          <th class="text-right">Time</th>
          <th>Detail</th>
        </tr>
      </thead>
      <tbody>
        {#each probes as p (p.name)}
          {@const ms = elapsed(p)}
          {@const slow = ms > 2000}
          <tr class={p.status === 'error' ? 'text-error' : slow ? 'text-warning' : ''}>
            <td>
              {#if p.status === 'running'}
                <span class="loading loading-spinner loading-xs"></span>
              {:else if p.status === 'done'}
                <span class="text-success">✓</span>
              {:else}
                <span class="text-error">✕</span>
              {/if}
            </td>
            <td>{p.name}</td>
            <td class="text-right tabular-nums {slow ? 'font-bold' : ''}">
              {fmtMs(ms)}{#if slow}&nbsp;⚠{/if}
            </td>
            <td class="text-xs break-all">{p.detail}</td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
</div>

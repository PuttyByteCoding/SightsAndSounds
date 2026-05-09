<script lang="ts">
  import { tagBadgeClass } from '$lib/tagColors';

  // Sample group names so the per-group hash palette renders all six hues.
  const sampleGroups = [
    'Performer',
    'Performer Configuration',
    'Subscription Service',
    'Content Creator',
    'Series',
    'Title',
    'Other',
    'Flags'
  ];

  // Tailwind palette as inline-style swatches. Tailwind 4 + daisyUI 5 isn't
  // emitting bg-{hue}-500/20 utilities here, so we render the colors via
  // inline `style` to guarantee they show up. Each tuple is [name, R G B].
  const swatches: Array<[string, string]> = [
    ['slate',   '100 116 139'],
    ['stone',   '120 113 108'],
    ['zinc',    '113 113 122'],
    ['rose',    '244 63 94'],
    ['pink',    '236 72 153'],
    ['fuchsia', '217 70 239'],
    ['purple',  '168 85 247'],
    ['violet',  '139 92 246'],
    ['indigo',  '99 102 241'],
    ['blue',    '59 130 246'],
    ['sky',     '14 165 233'],
    ['cyan',    '6 182 212'],
    ['teal',    '20 184 166'],
    ['emerald', '16 185 129'],
    ['green',   '34 197 94'],
    ['lime',    '132 204 22'],
    ['yellow',  '234 179 8'],
    ['amber',   '245 158 11'],
    ['orange',  '249 115 22'],
    ['red',     '239 68 68']
  ];
  function chipStyle(rgb: string): string {
    return `background-color: rgb(${rgb} / 0.20); border-color: rgb(${rgb} / 0.45); color: rgb(${rgb} / 1.0); filter: brightness(1.45);`;
  }
</script>

<svelte:head><title>Style Guide</title></svelte:head>

{#snippet row(label: string, code: string)}
  <div class="grid grid-cols-[200px_1fr_auto] items-center gap-3 py-2 border-b border-base-300 last:border-0">
    <div class="text-sm font-medium">{label}</div>
    <div></div>
    <code class="text-xs text-base-content/60">{code}</code>
  </div>
{/snippet}

<div class="space-y-8 max-w-4xl">
  <header>
    <h1 class="text-2xl font-semibold">Style Guide</h1>
    <p class="text-base-content/70 text-sm">
      Preview of every category in the proposed color scheme. Nothing here mutates server state.
    </p>
  </header>

  <!-- ===== Buttons ===== -->
  <section class="space-y-1">
    <h2 class="text-lg font-semibold border-b border-base-300 pb-1 mb-2">Buttons</h2>
    <p class="text-sm text-base-content/70 mb-2">
      Action buttons. Pattern: dim soft fill + brighter border, except Add/Create/Confirm which is the solid primary CTA.
    </p>

    <div class="card bg-base-200 px-4 py-2">
      <div class="grid grid-cols-[200px_1fr_auto] items-center gap-3 py-2 border-b border-base-300">
        <div class="text-sm font-medium">Edit a thing</div>
        <div><button class="btn btn-sm btn-soft btn-accent border border-accent/50">Edit Group</button></div>
        <code class="text-xs text-base-content/60">btn-soft btn-accent border border-accent/50</code>
      </div>
      <div class="grid grid-cols-[200px_1fr_auto] items-center gap-3 py-2 border-b border-base-300">
        <div class="text-sm font-medium">Delete a thing</div>
        <div><button class="btn btn-sm btn-soft btn-error border border-error/50">Delete Group</button></div>
        <code class="text-xs text-base-content/60">btn-soft btn-error border border-error/50</code>
      </div>
      <div class="grid grid-cols-[200px_1fr_auto] items-center gap-3 py-2 border-b border-base-300">
        <div class="text-sm font-medium">Add / Create</div>
        <div><button class="btn btn-sm btn-soft btn-primary btn-cta">+ Tag</button></div>
        <code class="text-xs text-base-content/60">btn-soft btn-primary btn-cta</code>
      </div>
      <div class="grid grid-cols-[200px_1fr_auto] items-center gap-3 py-2 border-b border-base-300">
        <div class="text-sm font-medium">Confirm a thing</div>
        <div><button class="btn btn-sm btn-soft btn-primary btn-cta">Save</button></div>
        <code class="text-xs text-base-content/60">btn-soft btn-primary btn-cta (same as Add)</code>
      </div>
      <div class="grid grid-cols-[200px_1fr_auto] items-center gap-3 py-2 border-b border-base-300">
        <div class="text-sm font-medium">Merge things</div>
        <div><button class="btn btn-sm btn-soft btn-warning border border-warning/50">Merge…</button></div>
        <code class="text-xs text-base-content/60">btn-soft btn-warning border border-warning/50</code>
      </div>
      <div class="grid grid-cols-[200px_1fr_auto] items-center gap-3 py-2 border-b border-base-300">
        <div class="text-sm font-medium">Cancel a thing</div>
        <div><button class="btn btn-sm btn-cancel">Cancel</button></div>
        <code class="text-xs text-base-content/60">btn btn-cancel (self-contained slate)</code>
      </div>
      <div class="grid grid-cols-[200px_1fr_auto] items-center gap-3 py-2 border-b border-base-300">
        <div class="text-sm font-medium">Toggle — OFF</div>
        <div><button class="btn btn-sm btn-ghost border border-base-content/20">Auto-play</button></div>
        <code class="text-xs text-base-content/60">btn-ghost border border-base-content/20</code>
      </div>
      <div class="grid grid-cols-[200px_1fr_auto] items-center gap-3 py-2">
        <div class="text-sm font-medium">Toggle — ON</div>
        <div><button class="btn btn-sm btn-soft btn-success border border-success/50">Auto-play</button></div>
        <code class="text-xs text-base-content/60">btn-soft btn-success border border-success/50</code>
      </div>
    </div>
  </section>

  <!-- ===== Banners ===== -->
  <section class="space-y-1">
    <h2 class="text-lg font-semibold border-b border-base-300 pb-1 mb-2">Banners / Alerts</h2>

    <div class="space-y-2">
      <div>
        <div class="alert alert-info">
          <span>Tip: drag a tag group to reorder it.</span>
        </div>
        <code class="text-xs text-base-content/60">alert alert-info</code>
      </div>
      <div>
        <div class="alert alert-success">
          <span>Saved 3 changes.</span>
        </div>
        <code class="text-xs text-base-content/60">alert alert-success</code>
      </div>
      <div>
        <div class="alert alert-warning">
          <span>This merges 4 source tags into one. Sources will be deleted.</span>
        </div>
        <code class="text-xs text-base-content/60">alert alert-warning</code>
      </div>
      <div>
        <div class="alert alert-error">
          <span>Save failed: TagGroup not found.</span>
        </div>
        <code class="text-xs text-base-content/60">alert alert-error</code>
      </div>
    </div>
  </section>

  <!-- ===== Tag default — current 6 ===== -->
  <section class="space-y-1">
    <h2 class="text-lg font-semibold border-b border-base-300 pb-1 mb-2">Tag Pills — current 6-hue palette</h2>
    <p class="text-sm text-base-content/70 mb-2">
      Today each tag group hashes to one of six muted hues from <code>app.css</code>.
    </p>

    <div class="card bg-base-200 p-3">
      <div class="flex flex-wrap gap-2">
        {#each sampleGroups as g (g)}
          <span class="badge {tagBadgeClass(g)}">{g}: example</span>
        {/each}
      </div>
    </div>
  </section>

  <!-- ===== Tag pill candidates ===== -->
  <section class="space-y-1">
    <h2 class="text-lg font-semibold border-b border-base-300 pb-1 mb-2">Tag Pills — candidate muted hues</h2>
    <p class="text-sm text-base-content/70 mb-2">
      Pick the ones you want in the final palette. All use the same <code>bg-{'{hue}'}-500/20 text-{'{hue}'}-300 border-{'{hue}'}-500/40</code> pattern.
    </p>

    <div class="card bg-base-200 p-3">
      <div class="flex flex-wrap gap-2">
        {#each swatches as [name, rgb] (name)}
          <span class="inline-block px-2 py-0.5 rounded-full text-xs border" style={chipStyle(rgb)}>{name}</span>
        {/each}
      </div>
    </div>
  </section>

  <!-- ===== Filter pills ===== -->
  <section class="space-y-1">
    <h2 class="text-lg font-semibold border-b border-base-300 pb-1 mb-2">Tag Pills — in a filter slot</h2>
    <p class="text-sm text-base-content/70 mb-2">
      When a tag is in any filter slot the slot color overrides the group color.
    </p>

    <div class="card bg-base-200 px-4 py-2">
      <div class="grid grid-cols-[200px_1fr_auto] items-center gap-3 py-2 border-b border-base-300">
        <div class="text-sm font-medium">Filter Required</div>
        <div><span class="badge bg-success/20 text-success border-success/40">2024</span></div>
        <code class="text-xs text-base-content/60">bg-success/20 text-success border-success/40</code>
      </div>
      <div class="grid grid-cols-[200px_1fr_auto] items-center gap-3 py-2 border-b border-base-300">
        <div class="text-sm font-medium">Filter Optional</div>
        <div><span class="badge bg-info/20 text-info border-info/40">Soundboard</span></div>
        <code class="text-xs text-base-content/60">bg-info/20 text-info border-info/40</code>
      </div>
      <div class="grid grid-cols-[200px_1fr_auto] items-center gap-3 py-2">
        <div class="text-sm font-medium">Filter Excluded</div>
        <div><span class="badge bg-error/20 text-error border-error/40 line-through opacity-80">Compilation</span></div>
        <code class="text-xs text-base-content/60">bg-error/20 text-error border-error/40 line-through opacity-80</code>
      </div>
    </div>
  </section>

  <!-- ===== Status pills ===== -->
  <section class="space-y-1">
    <h2 class="text-lg font-semibold border-b border-base-300 pb-1 mb-2">Status Pills (on a video)</h2>
    <p class="text-sm text-base-content/70 mb-2">
      Three statuses: informational (slate) → caution (yellow) → terminal (red).
    </p>

    <div class="card bg-base-200 px-4 py-2">
      <div class="grid grid-cols-[200px_1fr_auto] items-center gap-3 py-2 border-b border-base-300">
        <div class="text-sm font-medium">Status: Needs Review</div>
        <div><span class="inline-block px-2 py-0.5 rounded-full text-xs border" style={chipStyle('168 162 158')}>Needs Review</span></div>
        <code class="text-xs text-base-content/60">stone — rgb(168 162 158)</code>
      </div>
      <div class="grid grid-cols-[200px_1fr_auto] items-center gap-3 py-2 border-b border-base-300">
        <div class="text-sm font-medium">Status: Playback Issue</div>
        <div><span class="inline-block px-2 py-0.5 rounded-full text-xs border" style={chipStyle('249 115 22')}>Playback Issue</span></div>
        <code class="text-xs text-base-content/60">orange — rgb(249 115 22)</code>
      </div>
      <div class="grid grid-cols-[200px_1fr_auto] items-center gap-3 py-2">
        <div class="text-sm font-medium">Status: To Delete</div>
        <div><span class="inline-block px-2 py-0.5 rounded-full text-xs border" style={chipStyle('239 68 68')}>To Delete</span></div>
        <code class="text-xs text-base-content/60">red — rgb(239 68 68)</code>
      </div>
    </div>
  </section>

  <!-- ===== In context ===== -->
  <section class="space-y-1">
    <h2 class="text-lg font-semibold border-b border-base-300 pb-1 mb-2">In context: Tag Management header</h2>
    <p class="text-sm text-base-content/70 mb-2">All five buttons together, as they'd appear on /tags.</p>

    <div class="card bg-base-200 p-3">
      <header class="flex flex-wrap items-center justify-between gap-2">
        <h3 class="text-lg font-semibold">Performer</h3>
        <div class="flex flex-wrap gap-2">
          <button class="btn btn-sm btn-soft btn-accent border border-accent/50">Edit Group</button>
          <button class="btn btn-sm btn-soft btn-error border border-error/50">Delete Group</button>
          <button class="btn btn-sm btn-soft btn-warning border border-warning/50">Merge…</button>
          <button class="btn btn-sm btn-cancel">Cancel Merge</button>
          <button class="btn btn-sm btn-soft btn-primary btn-cta">+ Tag</button>
        </div>
      </header>
    </div>
  </section>
</div>

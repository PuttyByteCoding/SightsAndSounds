<script lang="ts">
  // Hidden-by-default tags management page (issue #84). Lists every tag,
  // grouped by tag group, with a toggle to mark it "hidden by default" —
  // videos carrying such a tag are suppressed from the browse grid unless the
  // user explicitly filters for the tag. The same toggle also lives on the
  // filter-tree tag modal; this page is the manage-them-all-in-one-place view.
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import type { Tag } from '$lib/types';

  let tags = $state<Tag[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let search = $state('');
  // Only show tags currently hidden — handy once a library has many tags.
  let onlyHidden = $state(false);
  // Per-tag in-flight flag so a slow toggle disables just that row.
  let busy = $state<Record<string, boolean>>({});

  async function load() {
    loading = true;
    error = null;
    try {
      tags = await api.listTags();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
  }
  onMount(load);

  const hiddenCount = $derived(tags.filter((t) => t.hiddenByDefault).length);

  const visible = $derived.by(() => {
    const q = search.trim().toLowerCase();
    return tags
      .filter((t) => !onlyHidden || t.hiddenByDefault)
      .filter(
        (t) =>
          q.length === 0 ||
          t.name.toLowerCase().includes(q) ||
          t.tagGroupName.toLowerCase().includes(q)
      );
  });

  // Group the visible tags by their tag group for a readable layout.
  const grouped = $derived.by(() => {
    const map = new Map<string, { groupName: string; tags: Tag[] }>();
    for (const t of visible) {
      const k = t.tagGroupId;
      if (!map.has(k)) map.set(k, { groupName: t.tagGroupName, tags: [] });
      map.get(k)!.tags.push(t);
    }
    return [...map.values()].sort((a, b) => a.groupName.localeCompare(b.groupName));
  });

  async function toggle(t: Tag) {
    const next = !t.hiddenByDefault;
    busy[t.id] = true;
    error = null;
    // Optimistic — reassign the array so the derived views recompute.
    tags = tags.map((x) => (x.id === t.id ? { ...x, hiddenByDefault: next } : x));
    try {
      await api.setTagHiddenByDefault(t.id, next);
    } catch (e) {
      // Roll back.
      tags = tags.map((x) => (x.id === t.id ? { ...x, hiddenByDefault: !next } : x));
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy[t.id] = false;
    }
  }
</script>

<div class="p-6 max-w-3xl space-y-5">
  <header>
    <h1 class="text-2xl font-semibold">Hidden Tags</h1>
    <p class="text-sm text-base-content/70 mt-1">
      A tag marked <span class="font-medium">hidden by default</span> keeps its videos
      out of the Videos grid unless you explicitly filter for that tag (add it to
      Required or Optional). Useful for things like “Behind the Scenes” that you
      only occasionally want to see.
    </p>
  </header>

  <div class="flex flex-wrap items-center gap-3">
    <input
      type="text"
      class="input input-bordered input-sm flex-1 min-w-60"
      placeholder="Filter tags by name or group…"
      bind:value={search}
    />
    <label class="label cursor-pointer gap-2">
      <input type="checkbox" class="checkbox checkbox-sm" bind:checked={onlyHidden} />
      <span class="label-text text-sm">Only hidden</span>
    </label>
    <span class="text-xs text-base-content/60 tabular-nums">
      {hiddenCount} hidden / {tags.length} tags
    </span>
  </div>

  {#if error}
    <div class="alert alert-error text-sm">
      <span>{error}</span>
      <button class="btn btn-xs" onclick={() => (error = null)}>Dismiss</button>
    </div>
  {/if}

  {#if loading}
    <div class="flex items-center gap-2 text-base-content/70">
      <span class="loading loading-spinner loading-sm"></span> Loading tags…
    </div>
  {:else if tags.length === 0}
    <p class="text-sm text-base-content/60 italic">No tags yet.</p>
  {:else if visible.length === 0}
    <p class="text-sm text-base-content/60 italic">No tags match.</p>
  {:else}
    <div class="space-y-4">
      {#each grouped as g (g.groupName)}
        <section>
          <h2 class="text-sm font-semibold text-base-content/70 mb-1">{g.groupName}</h2>
          <ul class="rounded-box bg-base-200 divide-y divide-base-300">
            {#each g.tags as t (t.id)}
              <li class="flex items-center justify-between gap-3 px-3 py-2">
                <span class="min-w-0 truncate {t.hiddenByDefault ? 'text-base-content/50' : ''}">
                  {t.name}
                  {#if t.hiddenByDefault}
                    <span class="text-[10px] italic text-base-content/40 ml-1">(default hidden)</span>
                  {/if}
                </span>
                <span class="shrink-0 flex items-center gap-2">
                  {#if busy[t.id]}<span class="loading loading-spinner loading-xs"></span>{/if}
                  <input
                    type="checkbox"
                    class="toggle toggle-sm toggle-primary"
                    checked={t.hiddenByDefault ?? false}
                    disabled={busy[t.id]}
                    onchange={() => toggle(t)}
                    aria-label="Hide {t.name} by default"
                  />
                </span>
              </li>
            {/each}
          </ul>
        </section>
      {/each}
    </div>
  {/if}
</div>

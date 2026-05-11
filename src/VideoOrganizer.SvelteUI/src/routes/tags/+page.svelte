<script lang="ts">
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { api } from '$lib/api';
  import { filterStore } from '$lib/filterStore.svelte';
  import type { Tag, TagGroup } from '$lib/types';
  import TagEditModal from '$lib/components/TagEditModal.svelte';
  import {
    applySortClick,
    compareBySortStack,
    sortDir,
    sortPosition,
    type SortEntry,
  } from '$lib/tableUtils.svelte';

  let groups = $state<TagGroup[]>([]);
  let selectedGroup = $state<TagGroup | null>(null);
  let tags = $state<Tag[]>([]);
  let loading = $state(false);
  let error = $state<string | null>(null);

  let editingGroup = $state<TagGroup | null>(null);
  let groupForm = $state({ name: '', allowMultiple: true, displayAsCheckboxes: false, sortOrder: 0, notes: '' });
  let showGroupDialog = $state(false);

  let editingTag = $state<Tag | null>(null);
  let showTagModal = $state(false);
  // Filter input for the tag list within the selected group.
  let tagSearch = $state('');
  // Toggles the similar-name finder. When on, the table is replaced
  // with clusters of tags whose names are within a Levenshtein
  // distance of 1-2, separated by dividers — useful for spotting
  // duplicates ("Bob Marley" / "Bob Marleey") and consolidating via
  // the merge tool.
  let similarOnly = $state(false);

  // Levenshtein distance — straight DP. Two rolling rows so memory
  // is O(min(|a|,|b|)) instead of O(|a|*|b|).
  function levenshtein(a: string, b: string): number {
    const al = a.length;
    const bl = b.length;
    if (al === 0) return bl;
    if (bl === 0) return al;
    let prev = new Array(bl + 1);
    let curr = new Array(bl + 1);
    for (let j = 0; j <= bl; j++) prev[j] = j;
    for (let i = 1; i <= al; i++) {
      curr[0] = i;
      for (let j = 1; j <= bl; j++) {
        const cost = a.charCodeAt(i - 1) === b.charCodeAt(j - 1) ? 0 : 1;
        curr[j] = Math.min(
          curr[j - 1] + 1,    // insertion
          prev[j] + 1,        // deletion
          prev[j - 1] + cost  // substitution
        );
      }
      [prev, curr] = [curr, prev];
    }
    return prev[bl];
  }

  // Similarity predicate. Distance ≤ 1 always passes; distance = 2
  // only counts when the longer string has ≥ 5 chars so that "ana"
  // and "abc" don't get clustered. Length-guard short-circuits any
  // pair whose lengths differ by more than 2 — at that point the
  // edit distance has to be ≥ 3, so no point computing it.
  function isSimilar(a: string, b: string): boolean {
    const al = a.length;
    const bl = b.length;
    if (Math.abs(al - bl) > 2) return false;
    const d = levenshtein(a, b);
    if (d <= 1) return true;
    if (d === 2 && Math.max(al, bl) >= 5) return true;
    return false;
  }

  // BFS over the similarity graph. Skips singleton components — the
  // "Similar only" view is meant to surface duplicates, so a tag
  // with no near-twins doesn't earn a row. O(n²) for the adjacency
  // build but the length-guard inside isSimilar() keeps the constant
  // factor down for typical tag lists (a few hundred entries).
  function clusterSimilar(items: Tag[]): Tag[][] {
    const lc = items.map(t => t.name.toLowerCase());
    const adj: number[][] = items.map(() => []);
    for (let i = 0; i < items.length; i++) {
      for (let j = i + 1; j < items.length; j++) {
        if (isSimilar(lc[i], lc[j])) {
          adj[i].push(j);
          adj[j].push(i);
        }
      }
    }
    const visited = new Array(items.length).fill(false);
    const clusters: Tag[][] = [];
    for (let i = 0; i < items.length; i++) {
      if (visited[i] || adj[i].length === 0) {
        visited[i] = true;
        continue;
      }
      const queue: number[] = [i];
      const cluster: Tag[] = [];
      visited[i] = true;
      while (queue.length > 0) {
        const k = queue.shift()!;
        cluster.push(items[k]);
        for (const n of adj[k]) {
          if (!visited[n]) {
            visited[n] = true;
            queue.push(n);
          }
        }
      }
      if (cluster.length >= 2) {
        // Sort variants alphabetically inside a cluster so visually
        // adjacent rows are easier to compare. The clusters themselves
        // are appended in scan order, which keeps stable ordering on
        // re-render.
        cluster.sort((a, b) => a.name.localeCompare(b.name));
        clusters.push(cluster);
      }
    }
    return clusters;
  }

  // Sort state — multi-column stack shared by all four data
  // columns. Plain header click cycles asc → desc → cleared on the
  // primary; shift-click appends / toggles / removes for secondary
  // sorts. Only applied in the flat view; "Similar only" preserves
  // its BFS cluster order so the user can scan grouped variants.
  type TagCol = 'favorite' | 'name' | 'aliases' | 'videos';
  let sortStack = $state<SortEntry<TagCol>[]>([]);
  function onSortClick(col: TagCol, e: MouseEvent) {
    sortStack = applySortClick(sortStack, col, e.shiftKey);
  }

  // Per-column getters for compareBySortStack. Favorite is mapped
  // to a number so the boolean sort puts starred tags ahead of
  // non-starred (or behind, on desc). Aliases compares the joined
  // string so multi-alias tags still order naturally.
  const tagSortGetters: Record<TagCol, (t: Tag) => string | number> = {
    favorite: (t) => (t.isFavorite ? 1 : 0),
    name: (t) => t.name,
    aliases: (t) => t.aliases.join(', '),
    videos: (t) => t.videoCount
  };

  // The plain filtered view (search box only). Same predicate the
  // template used inline before; pulled out so both branches of the
  // similar-or-flat render path can reuse it. When a sort stack is
  // active, the result is re-sorted on top of the search filter.
  const filteredTags = $derived.by(() => {
    const q = tagSearch.trim().toLowerCase();
    const base = !q ? tags : tags.filter(t =>
      t.name.toLowerCase().includes(q)
      || t.aliases.some(a => a.toLowerCase().includes(q))
    );
    if (sortStack.length === 0) return base;
    return [...base].sort(compareBySortStack(tagSortGetters, sortStack));
  });

  // Cluster view operates on the search-filtered set so the user
  // can narrow ("show me Bob*") and find duplicates inside the slice.
  // Sort doesn't apply here — clusters keep their BFS scan order
  // and within-cluster alphabetical order.
  const similarClusters = $derived(similarOnly ? clusterSimilar(filteredTags) : []);

  let mergeMode = $state(false);
  let mergeSelected = $state<Set<string>>(new Set());
  let mergeTargetId = $state<string>('');
  // Optional: name of a brand-new tag to create as the merge target. When
  // set, doMerge creates it first then merges every selected tag into it
  // (the backend folds source names + aliases into the target's alias list).
  let mergeNewTagName = $state<string>('');

  async function loadGroups() {
    loading = true;
    try {
      groups = await api.listTagGroups();
      if (groups.length > 0 && !selectedGroup) await selectGroup(groups[0]);
    } catch (e: any) {
      error = e?.message ?? 'Failed to load tag groups';
    } finally {
      loading = false;
    }
  }

  async function selectGroup(g: TagGroup) {
    selectedGroup = g;
    mergeMode = false;
    mergeSelected = new Set();
    mergeTargetId = '';
    try {
      tags = await api.listTags({ groupId: g.id, withCounts: true });
    } catch (e: any) {
      error = e?.message ?? 'Failed to load tags';
    }
  }

  function startCreateGroup() {
    editingGroup = null;
    groupForm = { name: '', allowMultiple: true, displayAsCheckboxes: false, sortOrder: (groups.at(-1)?.sortOrder ?? 0) + 10, notes: '' };
    showGroupDialog = true;
  }
  function startEditGroup(g: TagGroup) {
    editingGroup = g;
    groupForm = { name: g.name, allowMultiple: g.allowMultiple, displayAsCheckboxes: g.displayAsCheckboxes, sortOrder: g.sortOrder, notes: g.notes };
    showGroupDialog = true;
  }
  async function saveGroup() {
    try {
      if (editingGroup) await api.updateTagGroup(editingGroup.id, groupForm);
      else await api.createTagGroup(groupForm);
      showGroupDialog = false;
      editingGroup = null;
      await loadGroups();
    } catch (e: any) { error = e?.message ?? 'Failed to save group'; }
  }
  // Workers — confirmation is handled by the styled modal below
  // (see openDeleteTagConfirm / openDeleteGroupConfirm). These run
  // only after the user has explicitly clicked the destructive
  // button inside that modal, so no inline window.confirm prompt
  // here.
  async function deleteGroup(g: TagGroup) {
    try {
      await api.deleteTagGroup(g.id);
      if (selectedGroup?.id === g.id) selectedGroup = null;
      await loadGroups();
    } catch (e: any) { error = e?.message ?? 'Failed to delete group'; }
  }

  function startCreateTag() {
    if (!selectedGroup) return;
    editingTag = null;
    showTagModal = true;
  }
  function startEditTag(t: Tag) {
    editingTag = t;
    showTagModal = true;
  }
  async function onTagModalSaved() {
    if (selectedGroup) await selectGroup(selectedGroup);
  }
  async function deleteTag(t: Tag) {
    try {
      await api.deleteTag(t.id);
      if (selectedGroup) await selectGroup(selectedGroup);
    } catch (e: any) { error = e?.message ?? 'Failed to delete tag'; }
  }

  // Styled-modal delete confirmation. Replaces window.confirm() so
  // both destructive paths (per-tag Del + per-group Delete) match
  // the page's daisyUI design system instead of falling out into
  // a system dialog.
  type DeleteConfirm =
    | { kind: 'tag'; tag: Tag }
    | { kind: 'group'; group: TagGroup };
  let deleteConfirm = $state<DeleteConfirm | null>(null);
  let deleting = $state(false);

  function openDeleteTagConfirm(t: Tag) {
    deleteConfirm = { kind: 'tag', tag: t };
  }
  function openDeleteGroupConfirm(g: TagGroup) {
    deleteConfirm = { kind: 'group', group: g };
  }
  function cancelDeleteConfirm() {
    if (deleting) return;
    deleteConfirm = null;
  }
  async function confirmDelete() {
    const c = deleteConfirm;
    if (!c || deleting) return;
    deleting = true;
    try {
      if (c.kind === 'tag') {
        await deleteTag(c.tag);
      } else {
        await deleteGroup(c.group);
      }
      deleteConfirm = null;
    } finally {
      deleting = false;
    }
  }

  // Window-level Esc dismisses the confirm modal. Only attached
  // while the modal is open so we don't fight focus elsewhere on
  // the page.
  function onDeleteConfirmKey(e: KeyboardEvent) {
    if (e.key === 'Escape') {
      e.preventDefault();
      cancelDeleteConfirm();
    }
  }

  // Tags currently mid-toggle. Per-id so a slow round-trip on one
  // row doesn't disable the others; also gates the click handler so
  // a rapid double-click can't fire two updateTag calls.
  let favBusy = $state<Set<string>>(new Set());

  // Jump to /browse with this tag pre-applied as a Required filter.
  // filterStore is module-scoped so its $state survives the SPA
  // navigation; we wipe whatever filter was previously active so
  // the user lands on a clean view scoped to just this tag, then
  // apply() routes the tag straight into the Required bucket
  // (no dialog). Lets the user jump from tag management to "show
  // me the videos with this tag" in one click.
  function showVideosForTag(t: Tag) {
    filterStore.clear();
    filterStore.apply({
      type: 'tag',
      value: t.id,
      label: t.name,
      tagGroupName: selectedGroup?.name
    }, 'required');
    void goto('/browse');
  }

  // (Row-hover highlight is now a pure CSS :hover rule on the
  // <tr>, no per-button state tracking — see the snippet's
  // `hover:bg-info/15` and the cells' `group-hover:opacity-100`.)
  async function toggleFavorite(t: Tag) {
    if (favBusy.has(t.id)) return;
    favBusy.add(t.id);
    favBusy = new Set(favBusy);
    const next = !t.isFavorite;
    // Optimistic flip — the row's ★ updates immediately. Reassign
    // the array so Svelte picks up the change; mutating the proxy
    // works for direct property writes but a fresh array is the
    // safest pattern for keyed lists.
    tags = tags.map(x => x.id === t.id ? { ...x, isFavorite: next } : x);
    try {
      await api.updateTag(t.id, {
        name: t.name,
        aliases: t.aliases,
        isFavorite: next,
        sortOrder: t.sortOrder,
        notes: t.notes
      });
    } catch (e: any) {
      // Roll back the optimistic state and surface the failure.
      tags = tags.map(x => x.id === t.id ? { ...x, isFavorite: t.isFavorite } : x);
      error = e?.message ?? 'Failed to toggle favorite';
    } finally {
      favBusy.delete(t.id);
      favBusy = new Set(favBusy);
    }
  }

  // ---- Bulk-paste create ----
  // Lets the user paste a newline/comma-separated list of tag names. We
  // dedup against existing tags (case-insensitive) and within the paste
  // itself, then issue one createTag call per surviving name.
  let showPasteDialog = $state(false);
  let pasteText = $state('');
  let pasteSaving = $state(false);
  let pasteResult = $state<{ created: number; failed: { name: string; error: string }[] } | null>(null);
  // When set, every tag created from this paste is marked as a
  // favorite up-front. Useful when seeding a list of "important"
  // performers / tags that should already appear in the Favorites
  // tree on the browse page without needing to star each one.
  let pasteAsFavorites = $state(false);

  function parsePasteNames(text: string): string[] {
    // Split on newlines or commas; trim; drop empties; case-insensitive dedup.
    const seen = new Set<string>();
    const out: string[] = [];
    for (const raw of text.split(/[\n,]/)) {
      const trimmed = raw.trim();
      if (!trimmed) continue;
      const key = trimmed.toLowerCase();
      if (seen.has(key)) continue;
      seen.add(key);
      out.push(trimmed);
    }
    return out;
  }

  const pasteNames = $derived(parsePasteNames(pasteText));
  const pasteExistingSet = $derived(
    new Set(tags.flatMap(t => [t.name.toLowerCase(), ...t.aliases.map(a => a.toLowerCase())]))
  );
  const pasteNewNames = $derived(pasteNames.filter(n => !pasteExistingSet.has(n.toLowerCase())));
  const pasteSkipCount = $derived(pasteNames.length - pasteNewNames.length);

  function startPaste() {
    pasteText = '';
    pasteResult = null;
    pasteAsFavorites = false;
    showPasteDialog = true;
  }

  async function commitPaste() {
    if (!selectedGroup || pasteNewNames.length === 0) return;
    pasteSaving = true;
    pasteResult = null;
    const failed: { name: string; error: string }[] = [];
    let created = 0;
    for (const name of pasteNewNames) {
      try {
        await api.createTag({
          tagGroupId: selectedGroup.id,
          name,
          aliases: [],
          isFavorite: pasteAsFavorites,
          notes: ''
        });
        created++;
      } catch (e: any) {
        failed.push({ name, error: e?.message ?? 'Unknown error' });
      }
    }
    pasteSaving = false;
    pasteResult = { created, failed };
    if (selectedGroup) await selectGroup(selectedGroup);
    if (failed.length === 0) {
      // Auto-close on full success after a brief delay so the user sees the count.
      setTimeout(() => { showPasteDialog = false; pasteResult = null; pasteText = ''; }, 800);
    }
  }

  function toggleMerge() {
    mergeMode = !mergeMode;
    mergeSelected = new Set();
    mergeTargetId = '';
    mergeNewTagName = '';
  }
  function toggleMergePick(id: string) {
    const s = new Set(mergeSelected);
    if (s.has(id)) s.delete(id); else s.add(id);
    mergeSelected = s;
  }
  async function doMerge() {
    if (!selectedGroup) return;
    const newName = mergeNewTagName.trim();
    if (!mergeTargetId && !newName) { error = 'Pick an existing target or enter a new tag name.'; return; }
    if (mergeTargetId && newName) { error = 'Pick exactly one — existing target OR a new tag name.'; return; }
    try {
      let targetId = mergeTargetId;
      if (newName) {
        // Reject collision with an existing tag in this group so the user
        // doesn't accidentally create a near-duplicate. Aliases are checked
        // server-side as part of the unique (TagGroupId, Name) constraint.
        if (tags.some(t => t.name.toLowerCase() === newName.toLowerCase())) {
          error = `A tag named "${newName}" already exists in this group — pick it from the Existing dropdown instead.`;
          return;
        }
        const created = await api.createTag({
          tagGroupId: selectedGroup.id, name: newName, aliases: [], isFavorite: false, notes: ''
        });
        targetId = created.id;
      }
      const sources = [...mergeSelected].filter(id => id !== targetId);
      if (sources.length === 0) { error = 'Pick at least one source tag distinct from the target.'; return; }
      await api.mergeTags({ sourceIds: sources, targetId });
      mergeMode = false;
      mergeSelected = new Set();
      mergeTargetId = '';
      mergeNewTagName = '';
      await selectGroup(selectedGroup);
    } catch (e: any) { error = e?.message ?? 'Merge failed'; }
  }

  onMount(loadGroups);
</script>

<svelte:head><title>Tag Management - Video Organizer</title></svelte:head>

<div class="flex flex-col gap-4">
  <h1 class="text-2xl font-semibold">Tag Management</h1>

  {#if error}
    <div class="alert alert-error">
      <span>{error}</span>
      <button class="btn btn-sm" onclick={() => (error = null)}>Dismiss</button>
    </div>
  {/if}

  <div class="grid grid-cols-1 lg:grid-cols-[260px_1fr] gap-4">
    <aside class="card bg-base-200 p-3 space-y-2 h-fit">
      <div class="flex items-center justify-between">
        <h2 class="font-semibold">Groups</h2>
        <button class="btn btn-xs btn-soft btn-primary btn-cta" onclick={startCreateGroup}>+ New</button>
      </div>
      {#if loading}
        <div class="loading loading-dots"></div>
      {:else}
        <ul class="menu menu-sm bg-base-100 rounded-box">
          {#each groups as g (g.id)}
            <li>
              <button
                type="button"
                class="flex items-center justify-between {selectedGroup?.id === g.id ? 'active' : ''}"
                onclick={() => selectGroup(g)}
              >
                <span class="truncate">{g.name}</span>
                <span class="badge badge-sm">{g.tagCount}</span>
              </button>
            </li>
          {/each}
          {#if groups.length === 0}
            <li class="text-sm text-base-content/60 px-2 py-3">No tag groups yet.</li>
          {/if}
        </ul>
      {/if}
    </aside>

    <section class="card bg-base-200 p-3 space-y-3">
      {#if !selectedGroup}
        <p class="text-base-content/60">Select a group on the left, or create one.</p>
      {:else}
        <header class="flex flex-wrap items-center justify-between gap-2">
          <div class="flex items-center gap-2">
            <h2 class="text-lg font-semibold">{selectedGroup.name}</h2>
          </div>
          <div class="flex flex-wrap gap-2">
            <button class="btn btn-sm btn-soft btn-accent border border-accent/50" onclick={() => startEditGroup(selectedGroup!)}>Edit Group</button>
            <!-- Truncation pattern: long group names ellipsize the
                 middle span instead of wrapping the button onto a
                 second line. max-w-xs caps the button so it doesn't
                 hog the action row when the group name is long;
                 below that cap the inner flex+truncate kicks in. -->
            <button
              class="btn btn-sm btn-soft btn-error border border-error/50 flex-nowrap min-w-0 max-w-xs"
              onclick={() => openDeleteGroupConfirm(selectedGroup!)}
              title={`Delete [${selectedGroup.name}] Tag Group`}
            >
              <span class="flex items-baseline w-full min-w-0">
                <span class="shrink-0">Delete&nbsp;[</span>
                <span class="flex-1 min-w-0 truncate">{selectedGroup.name}</span>
                <span class="shrink-0">]&nbsp;Tag&nbsp;Group</span>
              </span>
            </button>
            {#if mergeMode}
              <button class="btn btn-sm btn-cancel" onclick={toggleMerge}>Cancel Merge</button>
            {:else}
              <button class="btn btn-sm btn-soft btn-warning border border-warning/50" onclick={toggleMerge}>Merge tags</button>
            {/if}
            <button class="btn btn-sm btn-soft btn-primary btn-cta" onclick={startCreateTag}>+ Tag</button>
            <button class="btn btn-sm btn-soft btn-primary btn-cta" onclick={startPaste}>Paste List…</button>
          </div>
        </header>

        {#if selectedGroup.notes}
          <p class="text-sm text-base-content/70 italic">{selectedGroup.notes}</p>
        {/if}

        {#if mergeMode}
          <div class="card bg-base-200 border border-warning/40 p-3">
            <div class="flex flex-col gap-2 w-full">
              <p class="text-sm">Pick the tags to merge, then choose a target — either one of the picked tags, or a brand-new tag (the picked tags become its aliases).</p>
              <div class="flex flex-wrap items-center gap-2">
                <label class="text-sm" for="merge-target-existing">Existing target:</label>
                <select id="merge-target-existing" class="select select-sm" bind:value={mergeTargetId} disabled={mergeNewTagName.trim().length > 0}>
                  <option value="">— none —</option>
                  {#each tags.filter(t => mergeSelected.has(t.id)) as t (t.id)}
                    <option value={t.id}>{t.name}</option>
                  {/each}
                </select>
                <span class="text-base-content/50">or</span>
                <label class="text-sm" for="merge-target-new">New tag:</label>
                <input
                  id="merge-target-new"
                  type="text"
                  class="input input-bordered input-sm"
                  placeholder="New tag name…"
                  bind:value={mergeNewTagName}
                  disabled={!!mergeTargetId}
                />
                <button
                  class="btn btn-sm btn-soft btn-primary btn-cta"
                  disabled={(!mergeTargetId && !mergeNewTagName.trim()) || mergeSelected.size === 0}
                  onclick={doMerge}
                >Merge</button>
              </div>
            </div>
          </div>
        {/if}

        <div class="flex items-center gap-3">
          <input
            class="input input-bordered input-sm flex-1"
            placeholder="Filter tags by name or alias…"
            bind:value={tagSearch}
          />
          <!-- Similar-only — runs Levenshtein clustering on the
               currently-filtered tags. Useful for catching dupes
               like "Bob Marley" / "Bob Marlee" before they pile
               up; users typically pair this with the merge tool
               to consolidate. Disabled when no group is loaded
               (nothing to cluster). -->
          <label class="cursor-pointer label justify-start gap-2 py-1 whitespace-nowrap">
            <input
              type="checkbox"
              class="checkbox checkbox-sm"
              bind:checked={similarOnly}
              disabled={tags.length === 0}
            />
            <span class="label-text text-sm">Similar only</span>
          </label>
        </div>

        {#snippet tagRow(t: Tag)}
          {@const unused = t.videoCount === 0}
          {@const togglingFav = favBusy.has(t.id)}
          {@const dim = unused ? 'opacity-50 group-hover:opacity-100' : ''}
          <!-- Single row-hover rule: any hover on the <tr> tints
               the whole row in info-blue. Pure CSS — the Edit /
               Del / Videos buttons no longer need per-button
               JS hover tracking. The same `:hover` also drives the
               group-hover that lifts the unused-tag dim, so the
               row "wakes up" to full saturation while the user
               is over it.
               Unused tags (videoCount === 0) keep their dim on
               the descriptive cells (★, name, aliases, count) —
               action buttons stay full strength so they remain
               inviting to click. Hovering anywhere on the row
               (including the action buttons) lifts the dim. -->
          <tr class="group hover:bg-info/15">
            {#if mergeMode}
              <td>
                <input
                  type="checkbox"
                  class="checkbox checkbox-sm"
                  checked={mergeSelected.has(t.id)}
                  onchange={() => toggleMergePick(t.id)}
                />
              </td>
            {/if}
            <td class="text-center align-middle {dim}">
              <button
                type="button"
                class="leading-none"
                onclick={() => toggleFavorite(t)}
                disabled={togglingFav}
                aria-pressed={t.isFavorite}
                aria-label={t.isFavorite ? `Unmark ${t.name} as favorite` : `Mark ${t.name} as favorite`}
                title={t.isFavorite ? 'Unmark as favorite' : 'Mark as favorite'}
              >
                <span class="{t.isFavorite ? 'text-warning' : 'text-base-content/30 hover:text-warning'} text-lg">★</span>
              </button>
            </td>
            <td class="font-medium {dim}">{t.name}</td>
            <td class="text-sm text-base-content/70 {dim}">{t.aliases.join(', ')}</td>
            <td class="text-right tabular-nums {dim}">{t.videoCount}</td>
            <td class="text-right whitespace-nowrap">
              <button
                class="btn btn-xs btn-soft btn-info border border-info/50"
                onclick={() => showVideosForTag(t)}
                disabled={unused}
                title={unused
                  ? 'No videos use this tag yet'
                  : `Show ${t.videoCount} video${t.videoCount === 1 ? '' : 's'} with this tag`}
              >Videos</button>
              <button
                class="btn btn-xs btn-soft btn-accent border border-accent/50"
                onclick={() => startEditTag(t)}
              >Edit</button>
              <button
                class="btn btn-xs btn-soft btn-error border border-error/50"
                onclick={() => openDeleteTagConfirm(t)}
              >Del</button>
            </td>
          </tr>
        {/snippet}

        <table class="table table-sm">
          <!-- thead is position:sticky on the parent (the page's
               natural document scroll). Each <th> gets a bg-base-200
               background and a bottom border so the header still
               looks separated from the rows underneath when it
               sticks. z-10 keeps it above the row content during
               scroll-overlap. Sort is disabled while "Similar only"
               is on — clusters take priority over column sort there
               (the BFS group order would just get scrambled). -->
          <thead class="sticky top-0 z-10 bg-base-200 shadow-[0_1px_0_0_var(--color-base-300)]">
            <tr>
              {#if mergeMode}<th class="w-8 bg-base-200"></th>{/if}
              {#each [
                { key: 'favorite' as const, label: '★', align: 'center' as const, hint: 'Favorite' },
                { key: 'name' as const, label: 'Name', align: 'left' as const, hint: null },
                { key: 'aliases' as const, label: 'Aliases', align: 'left' as const, hint: null },
                { key: 'videos' as const, label: 'Videos', align: 'right' as const, hint: null },
              ] as col (col.key)}
                {@const dir = sortDir(sortStack, col.key)}
                {@const pos = sortPosition(sortStack, col.key)}
                <th
                  class="bg-base-200 select-none p-0 {col.align === 'right' ? 'text-right' : col.align === 'center' ? 'text-center' : 'text-left'} {col.key === 'favorite' ? 'w-8' : ''}"
                  title={col.hint ?? `Click to sort. Shift-click for multi-column sort.`}
                >
                  <button
                    type="button"
                    class="w-full px-3 py-2 hover:bg-base-300 cursor-pointer flex items-center gap-1 {col.align === 'right' ? 'justify-end' : col.align === 'center' ? 'justify-center' : ''} {similarOnly ? 'opacity-40 cursor-not-allowed' : ''}"
                    onclick={(e) => !similarOnly && onSortClick(col.key, e)}
                    disabled={similarOnly}
                    title={similarOnly
                      ? 'Sort is disabled while "Similar only" is on'
                      : 'Click to sort. Shift-click for multi-column sort.'}
                  >
                    <span>{col.label}</span>
                    {#if dir}
                      <span class="text-[10px] tabular-nums text-base-content/60">
                        {dir === 'asc' ? '▲' : '▼'}{pos > 1 ? pos : ''}
                      </span>
                    {/if}
                  </button>
                </th>
              {/each}
              <th class="w-44 bg-base-200"></th>
            </tr>
          </thead>
          <tbody>
            {#if similarOnly}
              {#each similarClusters as cluster, ci (ci)}
                {#if ci > 0}
                  <!-- Divider between clusters — a tinted
                       full-width strip so the eye registers each
                       cluster as a discrete group of variants. -->
                  <tr class="bg-base-200/60">
                    <td colspan={mergeMode ? 6 : 5} class="text-xs text-base-content/50 italic py-1">
                      — similar group {ci + 1} —
                    </td>
                  </tr>
                {/if}
                {#each cluster as t (t.id)}
                  {@render tagRow(t)}
                {/each}
              {/each}
              {#if similarClusters.length === 0}
                <tr><td colspan={mergeMode ? 6 : 5} class="text-center text-base-content/60">
                  No similar tag pairs found{tagSearch.trim() ? ' in the current filter' : ''}.
                </td></tr>
              {/if}
            {:else}
              {#each filteredTags as t (t.id)}
                {@render tagRow(t)}
              {/each}
              {#if tags.length === 0}
                <tr><td colspan={mergeMode ? 6 : 5} class="text-center text-base-content/60">No tags in this group yet.</td></tr>
              {/if}
            {/if}
          </tbody>
        </table>
      {/if}
    </section>
  </div>
</div>

{#if showGroupDialog}
  <div class="modal modal-open">
    <div class="modal-box">
      <h3 class="font-bold text-lg">{editingGroup ? 'Edit' : 'New'} Tag Group</h3>
      <div class="form-control gap-3 mt-3">
        <label class="form-control">
          <span class="label-text">Name</span>
          <input class="input input-bordered" bind:value={groupForm.name} />
        </label>
        <label class="cursor-pointer label justify-start gap-3">
          <input type="checkbox" class="checkbox" bind:checked={groupForm.allowMultiple} />
          <span class="label-text">Allow multiple tags per video</span>
        </label>
        <label class="cursor-pointer label justify-start gap-3">
          <input type="checkbox" class="checkbox" bind:checked={groupForm.displayAsCheckboxes} />
          <span class="label-text">Display as checkboxes (and Alt+1..9 keyboard toggles)</span>
        </label>
        <label class="form-control">
          <span class="label-text">Sort order</span>
          <input type="number" class="input input-bordered" bind:value={groupForm.sortOrder} />
        </label>
        <label class="form-control">
          <span class="label-text">Notes</span>
          <textarea class="textarea textarea-bordered" bind:value={groupForm.notes}></textarea>
        </label>
      </div>
      <div class="modal-action">
        <button class="btn btn-cancel" onclick={() => (showGroupDialog = false)}>Cancel</button>
        <button class="btn btn-soft btn-primary btn-cta" onclick={saveGroup} disabled={!groupForm.name.trim()}>
          {editingGroup ? 'Save' : 'Create'}
        </button>
      </div>
    </div>
  </div>
{/if}

{#if selectedGroup}
  <TagEditModal
    bind:show={showTagModal}
    tag={editingTag}
    tagGroupId={selectedGroup.id}
    onSaved={onTagModalSaved}
  />
{/if}

{#if showPasteDialog && selectedGroup}
  <div class="modal modal-open">
    <div class="modal-box max-w-lg space-y-3">
      <h3 class="font-bold text-lg">Paste tags into {selectedGroup.name}</h3>
      <p class="text-sm text-base-content/70">
        One name per line (commas also work). Existing names and aliases are skipped.
      </p>
      <textarea
        class="textarea textarea-bordered w-full font-mono text-sm"
        rows="10"
        placeholder={'Bob Marley\nThe Wailers\nPeter Tosh'}
        bind:value={pasteText}
        disabled={pasteSaving}
      ></textarea>
      <div class="text-sm tabular-nums">
        <span class="text-success">{pasteNewNames.length} to create</span>
        {#if pasteSkipCount > 0}
          <span class="text-base-content/60"> · {pasteSkipCount} already exist (skipped)</span>
        {/if}
      </div>

      <!-- Bulk favorite toggle. Sets isFavorite=true on every
           created tag so a freshly-pasted batch shows up in the
           browse-page Favorites tree without a one-by-one star
           click. Only applies to NEW tags being created — tags
           that match an existing name (and are therefore skipped)
           keep whatever favorite state they already had. -->
      <label class="cursor-pointer label justify-start gap-3 py-1">
        <input
          type="checkbox"
          class="checkbox checkbox-sm"
          bind:checked={pasteAsFavorites}
          disabled={pasteSaving}
        />
        <span class="label-text inline-flex items-center gap-1">
          Mark <span class="text-warning">★</span> all newly-created tags as favorite
        </span>
      </label>

      {#if pasteResult}
        <div class="alert {pasteResult.failed.length === 0 ? 'alert-success' : 'alert-warning'} text-sm">
          <div class="space-y-1 w-full">
            <div>Created {pasteResult.created} tag{pasteResult.created === 1 ? '' : 's'}.</div>
            {#if pasteResult.failed.length > 0}
              <ul class="list-disc ml-5">
                {#each pasteResult.failed as f, i (i)}
                  <li><strong>{f.name}:</strong> {f.error}</li>
                {/each}
              </ul>
            {/if}
          </div>
        </div>
      {/if}

      <div class="modal-action">
        <button class="btn btn-cancel" onclick={() => (showPasteDialog = false)} disabled={pasteSaving}>
          {pasteResult ? 'Close' : 'Cancel'}
        </button>
        <button
          class="btn btn-soft btn-primary btn-cta"
          onclick={commitPaste}
          disabled={pasteSaving || pasteNewNames.length === 0}
        >
          {#if pasteSaving}<span class="loading loading-spinner loading-xs"></span>{/if}
          {pasteSaving ? 'Creating…' : `Create ${pasteNewNames.length}`}
        </button>
      </div>
    </div>
  </div>
{/if}

<!-- Delete confirmation modal — replaces the previous system
     window.confirm() prompts so the destructive paths match the
     page's daisyUI styling. Handles both per-tag Del and per-
     group Delete via a single modal scoped by `kind`. -->
<svelte:window onkeydown={deleteConfirm ? onDeleteConfirmKey : undefined} />

{#if deleteConfirm}
  {@const c = deleteConfirm}
  <div class="modal modal-open" role="dialog" aria-modal="true" aria-labelledby="tag-delete-confirm-title">
    <div class="modal-box max-w-md">
      <h3 id="tag-delete-confirm-title" class="font-bold text-lg">
        {c.kind === 'tag' ? 'Delete tag?' : 'Delete tag group?'}
      </h3>

      <div class="mt-3 text-sm">
        {#if c.kind === 'tag'}
          <div class="font-medium break-all">{c.tag.name}</div>
          {#if c.tag.aliases.length > 0}
            <div class="text-xs text-base-content/60 break-all mt-0.5">
              aliases: {c.tag.aliases.join(', ')}
            </div>
          {/if}
        {:else}
          <div class="font-medium break-all">{c.group.name}</div>
        {/if}
      </div>

      <p class="mt-3 text-sm text-base-content/80">
        {#if c.kind === 'tag'}
          Permanently delete this tag? It will be removed from
          <span class="font-semibold tabular-nums">{c.tag.videoCount}</span>
          video{c.tag.videoCount === 1 ? '' : 's'} that currently use it.
        {:else}
          Permanently delete this tag group <span class="font-semibold">and every tag inside it</span>?
          This cascades to every video using any of those tags.
        {/if}
      </p>

      <p class="mt-3 text-sm text-error font-semibold">This cannot be undone.</p>

      <!-- Initial focus lands on Cancel rather than the destructive
           Delete button — accidental Enter on the modal dismisses,
           not destroys. The user has to deliberately Tab or click
           to confirm. -->
      <div class="modal-action">
        <!-- svelte-ignore a11y_autofocus -->
        <button
          type="button"
          class="btn btn-sm btn-cancel"
          onclick={cancelDeleteConfirm}
          disabled={deleting}
          autofocus
        >Cancel</button>
        <button
          type="button"
          class="btn btn-sm btn-soft btn-error border border-error/50"
          onclick={confirmDelete}
          disabled={deleting}
        >
          {#if deleting}<span class="loading loading-spinner loading-xs"></span>{/if}
          {c.kind === 'tag' ? 'Delete tag' : 'Delete group'}
        </button>
      </div>
    </div>
    <!-- Backdrop click cancels — same affordance as Cancel/Esc. -->
    <button
      type="button"
      class="modal-backdrop"
      aria-label="Cancel delete"
      onclick={cancelDeleteConfirm}
    ></button>
  </div>
{/if}

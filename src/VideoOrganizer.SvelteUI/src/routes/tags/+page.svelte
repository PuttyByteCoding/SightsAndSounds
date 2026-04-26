<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import type { Tag, TagGroup } from '$lib/types';
  import TagEditModal from '$lib/components/TagEditModal.svelte';

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
  async function deleteGroup(g: TagGroup) {
    if (!confirm(`Delete tag group "${g.name}" and all its tags? This cascades to every video using these tags.`)) return;
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
    if (!confirm(`Delete tag "${t.name}"? It will be removed from every video using it.`)) return;
    try {
      await api.deleteTag(t.id);
      if (selectedGroup) await selectGroup(selectedGroup);
    } catch (e: any) { error = e?.message ?? 'Failed to delete tag'; }
  }

  // ---- Bulk-paste create ----
  // Lets the user paste a newline/comma-separated list of tag names. We
  // dedup against existing tags (case-insensitive) and within the paste
  // itself, then issue one createTag call per surviving name.
  let showPasteDialog = $state(false);
  let pasteText = $state('');
  let pasteSaving = $state(false);
  let pasteResult = $state<{ created: number; failed: { name: string; error: string }[] } | null>(null);

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
        await api.createTag({ tagGroupId: selectedGroup.id, name, aliases: [], isFavorite: false, notes: '' });
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
            <button class="btn btn-sm btn-soft btn-error border border-error/50" onclick={() => deleteGroup(selectedGroup!)}>Delete [{selectedGroup.name}] Tag Group</button>
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

        <input
          class="input input-bordered input-sm w-full"
          placeholder="Filter tags by name or alias…"
          bind:value={tagSearch}
        />

        <table class="table table-sm">
          <thead>
            <tr>
              {#if mergeMode}<th class="w-8"></th>{/if}
              <th>Name</th>
              <th>Aliases</th>
              <th class="text-right">Videos</th>
              <th class="w-32"></th>
            </tr>
          </thead>
          <tbody>
            {#each tags.filter(t => {
              const q = tagSearch.trim().toLowerCase();
              if (!q) return true;
              return t.name.toLowerCase().includes(q)
                || t.aliases.some(a => a.toLowerCase().includes(q));
            }) as t (t.id)}
              <tr class="hover">
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
                <td class="font-medium">
                  {#if t.isFavorite}<span class="text-warning mr-1">★</span>{/if}
                  {t.name}
                </td>
                <td class="text-sm text-base-content/70">{t.aliases.join(', ')}</td>
                <td class="text-right tabular-nums">{t.videoCount}</td>
                <td class="text-right">
                  <button class="btn btn-xs btn-soft btn-accent border border-accent/50" onclick={() => startEditTag(t)}>Edit</button>
                  <button class="btn btn-xs btn-soft btn-error border border-error/50" onclick={() => deleteTag(t)}>Del</button>
                </td>
              </tr>
            {/each}
            {#if tags.length === 0}
              <tr><td colspan={mergeMode ? 5 : 4} class="text-center text-base-content/60">No tags in this group yet.</td></tr>
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

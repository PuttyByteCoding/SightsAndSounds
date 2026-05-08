<script lang="ts">
  // Generic tag editor panel. Renders one section per TagGroup (loaded
  // dynamically) plus structural fields (Year, NeedsReview, Notes). Saves
  // tag set + scalar fields via /api/videos/{id} and /api/videos/{id}/tags.

  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import type { Tag, TagGroup, Video, VideoTagSummary } from '$lib/types';
  import { pillClass } from '$lib/tagColors';
  import TagEditModal from './TagEditModal.svelte';

  interface Props {
    video: Video | null;
    show: boolean;
    onAfterSave?: () => void | Promise<void>;
    // Fires whenever a tag is created or updated via the panel's
    // autocomplete + TagEditModal flow. The host uses this to refresh
    // its own copies of allTags / tagsByGroup / groups so the sidebar
    // tag tree picks up the change without waiting for a route reload.
    onTagSaved?: (saved: Tag) => void | Promise<void>;
    // Fires when the inner TagEditModal opens or closes. The host on
    // /browse keys the tags strip's expanded state off this so the
    // panel doesn't collapse when the modal portals out to <body>
    // (which kills the strip's :focus-within).
    onModalOpenChange?: (open: boolean) => void;
  }

  let {
    video = $bindable(),
    show = $bindable(),
    onAfterSave,
    onTagSaved: onTagSavedExternal,
    onModalOpenChange
  }: Props = $props();

  let groups = $state<TagGroup[]>([]);
  // Tag list per group, lazy-loaded on first focus.
  let tagsByGroup = $state<Record<string, Tag[]>>({});

  // Items rendered in the panel — one per tag group, in their persisted order.
  type PanelItem = { kind: 'group'; group: TagGroup };
  const panelItems = $derived<PanelItem[]>(groups.map(g => ({ kind: 'group', group: g })));

  let saving = $state(false);
  let error = $state<string | null>(null);
  let success = $state<string | null>(null);

  // Per-group composer (autocomplete input + filtered list + keyboard hi)
  let composer = $state<Record<string, { input: string; open: boolean; highlighted: number }>>({});
  // DOM refs for each group's composer input, indexed by render order. We
  // focus [0] on Shift+arrow nav so the user can start typing into the
  // first tag group immediately.
  let composerInputs: HTMLInputElement[] = $state([]);

  function ensureComposer(groupId: string) {
    if (!composer[groupId]) composer[groupId] = { input: '', open: false, highlighted: -1 };
  }

  // Focus the first tag-group composer ONLY when the panel transitions
  // from closed to open — not on every video change. Refocusing on
  // every nav (R clears review, W marks won't-play, →, etc.) used to
  // re-trigger :focus-within on the host's hover-strip wrapper and
  // visually expand it on each keystroke, which felt like the panel
  // was "opening itself". Tracking the previous `show` value lets us
  // discriminate the open transition from later video swaps.
  let prevShow = false;
  $effect(() => {
    const opened = show && !prevShow;
    prevShow = show;
    if (!opened || !video?.id) return;
    const first = composerInputs[0];
    if (first) queueMicrotask(() => first.focus());
  });

  // Edits live-mutate `video.*` directly (no staged copy). That way
  // VideoPlayer.saveIfDirty (which diffs the bound video) catches them on
  // Shift+arrow nav, and external changes (e.g. Alt+1) flow back into the
  // panel without a special re-init effect.
  function tagsAppliedInGroup(groupId: string): VideoTagSummary[] {
    return video?.tags.filter(t => t.tagGroupId === groupId) ?? [];
  }
  function isApplied(groupId: string, tagId: string): boolean {
    return video?.tags.some(t => t.id === tagId) ?? false;
  }

  async function loadGroups() {
    try {
      groups = await api.listTagGroups();
      for (const g of groups) ensureComposer(g.id);
    } catch (e: any) {
      error = e?.message ?? 'Failed to load tag groups';
    }
  }


  // --- Panel-edit (reorder groups via drag-and-drop) ---------------------

  let editingPanel = $state(false);
  // Current index of the dragged item in pendingOrder. Updates as we splice
  // mid-drag so adjacent groups visually bump aside.
  let dragIdx = $state<number | null>(null);
  // Pending order while editing — copy of panelItems the user is rearranging.
  let pendingOrder = $state<PanelItem[]>([]);
  // Items the template iterates over: live drag state while editing, persisted order otherwise.
  const renderItems = $derived(editingPanel ? pendingOrder : panelItems);

  function startPanelEdit() {
    pendingOrder = panelItems.map(x => x);
    editingPanel = true;
  }

  function onDragStart(idx: number, e: DragEvent) {
    dragIdx = idx;
    if (e.dataTransfer) {
      e.dataTransfer.effectAllowed = 'move';
      e.dataTransfer.setData('text/plain', String(idx));
      // Force the drag image to be just this fieldset. Without this, some
      // browsers snapshot a larger region (sibling fieldsets bleed in)
      // because of <fieldset>'s legend/anonymous-box layout quirks.
      const el = e.currentTarget as HTMLElement;
      const r = el.getBoundingClientRect();
      e.dataTransfer.setDragImage(el, e.clientX - r.left, e.clientY - r.top);
    }
  }
  // While dragging, splice the dragged item into the hovered slot in real
  // time. The visible reorder gives the "bump aside" effect — no separate
  // drop-line indicator needed.
  function onDragOver(idx: number, e: DragEvent) {
    if (dragIdx === null) return;
    e.preventDefault();
    if (e.dataTransfer) e.dataTransfer.dropEffect = 'move';
    if (idx === dragIdx) return;

    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect();
    const before = e.clientY < rect.top + rect.height / 2;
    // Target slot relative to the *current* layout. If we're moving down past
    // the hovered item, drop after it; if up, drop before. Otherwise pick by
    // cursor half.
    let target: number;
    if (idx > dragIdx) target = before ? idx - 1 : idx;
    else                target = before ? idx     : idx + 1;
    if (target === dragIdx) return;

    const next = [...pendingOrder];
    const [moved] = next.splice(dragIdx, 1);
    next.splice(target, 0, moved);
    pendingOrder = next;
    dragIdx = target;
  }
  function onDrop(_idx: number, e: DragEvent) {
    e.preventDefault();
    dragIdx = null;
  }
  function onDragEnd() {
    dragIdx = null;
  }

  // Persist the new ordering. Groups get new sortOrder via api.updateTagGroup.
  async function savePanelOrder() {
    const ordered = pendingOrder.map(item => item.group);
    try {
      await Promise.all(ordered.map((g, i) =>
        api.updateTagGroup(g.id, {
          name: g.name,
          allowMultiple: g.allowMultiple,
          displayAsCheckboxes: g.displayAsCheckboxes,
          sortOrder: (i + 1) * 10,
          notes: g.notes
        })
      ));
      editingPanel = false;
      pendingOrder = [];
      await loadGroups();
    } catch (e: any) {
      error = e?.message ?? 'Failed to save order';
    }
  }

  function cancelPanelEdit() {
    editingPanel = false;
    pendingOrder = [];
  }

  async function ensureTagsLoaded(groupId: string) {
    if (tagsByGroup[groupId]) return;
    tagsByGroup[groupId] = await api.listTags({ groupId });
  }

  function addTag(group: TagGroup, tag: Tag) {
    if (!video) return;
    if (video.tags.some(t => t.id === tag.id)) return;
    const summary: VideoTagSummary = {
      id: tag.id,
      tagGroupId: tag.tagGroupId,
      tagGroupName: tag.tagGroupName,
      name: tag.name
    };
    if (!group.allowMultiple) {
      // Single-value group: replace any existing tag from this group.
      video.tags = [...video.tags.filter(t => t.tagGroupId !== group.id), summary];
    } else {
      video.tags = [...video.tags, summary];
    }
    composer[group.id] = { input: '', open: false, highlighted: -1 };
  }

  function removeTag(_groupId: string, tagId: string) {
    if (!video) return;
    video.tags = video.tags.filter(t => t.id !== tagId);
  }

  function toggleTag(group: TagGroup, tag: Tag) {
    if (isApplied(group.id, tag.id)) removeTag(group.id, tag.id);
    else addTag(group, tag);
  }

  // Auto-load tags for any group rendered as checkboxes (we need the full
  // tag list to render the checkbox per tag).
  $effect(() => {
    for (const g of groups) {
      if (g.displayAsCheckboxes && !tagsByGroup[g.id]) ensureTagsLoaded(g.id);
    }
  });

  // --- Tag edit modal ----------------------------------------------------
  // Opened by autocomplete "+ Create", pill click, or pencil icon. When the
  // modal saves, refresh the affected group's tag list and update the tag
  // assignment locally.
  let editModalShow = $state(false);
  let editingTag = $state<Tag | null>(null);
  let editTagGroupId = $state<string | undefined>(undefined);

  // Bubble open/close transitions up so the host (e.g. /browse) can
  // pin the surrounding tags strip expanded while the modal is up.
  // Without this, the modal's portal-to-body strips :focus-within
  // from the strip and it collapses out from under the user.
  $effect(() => {
    onModalOpenChange?.(editModalShow);
  });
  let editInitialName = $state('');
  // When create flow comes from autocomplete, auto-apply the new tag to the
  // video on save (mimics the old behavior where + Create added it).
  let editAutoApplyToVideo = $state(false);
  // Where to return focus on Esc/Cancel. Captured at the moment the modal
  // opens (typically the composer input the user was typing in).
  let editFocusReturn: HTMLElement | null = null;

  function openCreateModal(group: TagGroup, prefill: string, autoApply = true) {
    editingTag = null;
    editTagGroupId = group.id;
    editInitialName = prefill;
    editAutoApplyToVideo = autoApply;
    editFocusReturn = (typeof document !== 'undefined'
      ? (document.activeElement as HTMLElement | null) : null);
    editModalShow = true;
  }
  function openEditModal(tag: Tag | VideoTagSummary, groupId: string) {
    // VideoTagSummary lacks aliases/etc.; fetch the full tag if needed.
    if ('aliases' in tag) {
      editingTag = tag as Tag;
      openModalWith(groupId);
    } else {
      api.getTag(tag.id).then(full => {
        editingTag = full;
        openModalWith(groupId);
      }).catch(e => { error = e?.message ?? 'Failed to load tag'; });
    }
  }
  function openModalWith(groupId: string) {
    editTagGroupId = groupId;
    editInitialName = '';
    editAutoApplyToVideo = false;
    editFocusReturn = (typeof document !== 'undefined'
      ? (document.activeElement as HTMLElement | null) : null);
    editModalShow = true;
  }

  // Restore focus to the input the user was on when the modal opened, with
  // the caret at the end of the typed string. Used on cancel and after
  // create-from-autocomplete saves so the user can keep typing.
  function returnFocus() {
    const el = editFocusReturn;
    editFocusReturn = null;
    if (!el) return;
    queueMicrotask(() => {
      el.focus();
      if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) {
        const len = el.value.length;
        try { el.setSelectionRange(len, len); } catch { /* numeric inputs etc. */ }
      }
    });
  }

  async function onTagSaved(saved: Tag) {
    // Refresh the tag list for that group.
    tagsByGroup[saved.tagGroupId] = await api.listTags({ groupId: saved.tagGroupId });

    // If we just created a tag from autocomplete, apply it to the video and
    // close the composer for that group.
    if (editAutoApplyToVideo) {
      const group = groups.find(g => g.id === saved.tagGroupId);
      if (group) addTag(group, saved);
    }

    // If an existing applied tag was renamed, update the name in video.tags
    // so the pill text updates immediately. Reassign the array so reactivity
    // catches the change.
    if (video) {
      const idx = video.tags.findIndex(t => t.id === saved.id);
      if (idx >= 0) {
        const next = [...video.tags];
        next[idx] = {
          id: saved.id,
          tagGroupId: saved.tagGroupId,
          tagGroupName: saved.tagGroupName,
          name: saved.name
        };
        video.tags = next;
      }
    }

    // Notify the host so its sidebar (allTags / tagsByGroup / group
    // counts) picks up the new or renamed tag without a route reload.
    if (onTagSavedExternal) await onTagSavedExternal(saved);

    // After saving (or applying via autocomplete), return focus to the
    // composer input the user was on so they can keep typing more tags.
    returnFocus();
  }

  function onTagCanceled() {
    returnFocus();
  }

  function suggestionsFor(group: TagGroup): Tag[] {
    // No query → no dropdown. The user wants the field to look like
    // a clean text input until they actually type; surfacing the full
    // tag inventory on focus was overwhelming and meant the suggestion
    // list mostly lived in the way of typing.
    const q = (composer[group.id]?.input ?? '').toLowerCase().trim();
    if (!q) return [];
    const all = tagsByGroup[group.id] ?? [];
    const selectedIds = new Set((video?.tags ?? []).map(t => t.id));
    return all
      .filter(t => !selectedIds.has(t.id))
      .filter(t =>
        t.name.toLowerCase().includes(q) ||
        t.aliases.some(a => a.toLowerCase().includes(q)))
      .slice(0, 12);
  }

  // True when the input has text and doesn't match an existing tag's name
  // or alias — drives the "NEW" badge and the Enter-to-create behavior.
  function isNovel(group: TagGroup): boolean {
    const q = (composer[group.id]?.input ?? '').trim().toLowerCase();
    if (!q) return false;
    const all = tagsByGroup[group.id] ?? [];
    return !all.some(t =>
      t.name.toLowerCase() === q || t.aliases.some(a => a.toLowerCase() === q));
  }

  function onComposerKeyDown(group: TagGroup, e: KeyboardEvent) {
    const c = composer[group.id];
    if (!c) return;
    const sugs = suggestionsFor(group);
    switch (e.key) {
      case 'ArrowDown':
        if (sugs.length === 0) return;
        e.preventDefault();
        composer[group.id] = { ...c, open: true, highlighted: (c.highlighted + 1) % sugs.length };
        break;
      case 'ArrowUp':
        if (sugs.length === 0) return;
        e.preventDefault();
        composer[group.id] = { ...c, open: true, highlighted: c.highlighted <= 0 ? sugs.length - 1 : c.highlighted - 1 };
        break;
      case 'Enter':
        e.preventDefault();
        e.stopPropagation();
        if (c.highlighted >= 0 && c.highlighted < sugs.length) {
          addTag(group, sugs[c.highlighted]);
        } else if (c.input.trim().length > 0 && isNovel(group)) {
          openCreateModal(group, c.input.trim(), true);
        }
        break;
      case 'Escape':
        composer[group.id] = { ...c, open: false, highlighted: -1 };
        break;
    }
  }

  async function save() {
    if (!video) return;
    saving = true;
    error = null;
    success = null;
    try {
      await api.updateVideo(video.id, {
        fileName: video.fileName,
        ingestDate: video.ingestDate,
        cameraType: video.cameraType,
        videoQuality: video.videoQuality,
        watchCount: video.watchCount,
        notes: video.notes,
        needsReview: video.needsReview,
        isFavorite: video.isFavorite,
        clipStartSeconds: video.clipStartSeconds,
        clipEndSeconds: video.clipEndSeconds,
        chapterMarkers: video.chapterMarkers,
        videoBlocks: video.videoBlocks,
        tagIds: video.tags.map(t => t.id)
      });
      success = 'Saved.';
      const fresh = await api.getVideo(video.id);
      if (fresh) video = fresh;
      if (onAfterSave) await onAfterSave();
    } catch (e: any) {
      error = e?.message ?? 'Save failed';
    } finally {
      saving = false;
    }
  }

  onMount(loadGroups);
</script>

{#if show && video}
  <div class="card bg-base-200 p-4 space-y-4">
    <header class="flex items-center justify-between">
      <h2 class="text-lg font-semibold">Tags</h2>
      <div class="flex gap-2">
        {#if !editingPanel}
          <button class="btn btn-sm" onclick={startPanelEdit}>Edit Tags Panel</button>
        {:else}
          <button class="btn btn-sm" onclick={cancelPanelEdit}>Cancel</button>
          <button class="btn btn-sm btn-primary" onclick={savePanelOrder}>Save Order</button>
        {/if}
        <button class="btn btn-sm" onclick={() => (show = false)}>Close</button>
      </div>
    </header>

    {#if error}
      <div class="alert alert-error">
        <span>{error}</span>
        <button class="btn btn-xs" onclick={() => (error = null)}>Dismiss</button>
      </div>
    {/if}
    {#if success}
      <div class="alert alert-success py-2 text-sm">{success}</div>
    {/if}

    {#if editingPanel}
      <p class="text-xs text-base-content/60">Drag items to reorder, then Save Order.</p>
    {/if}

    {#each renderItems as item, idx (item.group.id)}
      {@const group = item.group}
        <fieldset
          class="rounded px-3 pb-3 pt-1 transition-opacity {editingPanel ? 'cursor-grab border-2 border-base-300' : 'border border-base-300'} {dragIdx !== null && dragIdx !== idx ? 'opacity-40' : ''} {dragIdx === idx ? 'border-primary' : ''}"
          draggable={editingPanel}
          ondragstart={editingPanel ? (e) => onDragStart(idx, e) : undefined}
          ondragover={editingPanel ? (e) => onDragOver(idx, e) : undefined}
          ondrop={editingPanel ? (e) => onDrop(idx, e) : undefined}
          ondragend={editingPanel ? onDragEnd : undefined}
        >
          <legend class="px-2 text-sm font-medium flex items-center gap-2">
            {#if editingPanel}<span class="text-base-content/50" aria-hidden="true">⋮⋮</span>{/if}
            <span>{group.name}</span>
          </legend>

          {#if group.displayAsCheckboxes}
            <!-- Checkbox-mode group: list every tag with a checkbox + ✎. -->
            <div class="flex flex-col gap-1">
              {#each (tagsByGroup[group.id] ?? []) as t, tagIdx (t.id)}
                <div class="flex items-center gap-2 py-0.5">
                  <input
                    type="checkbox"
                    class="checkbox checkbox-sm"
                    id="cb-{t.id}"
                    checked={isApplied(group.id, t.id)}
                    onchange={() => toggleTag(group, t)}
                  />
                  <label for="cb-{t.id}" class="label-text cursor-pointer">{t.name}</label>
                  <button
                    type="button"
                    class="btn btn-ghost btn-xs px-1"
                    onclick={() => openEditModal(t, group.id)}
                    title="Edit tag"
                    aria-label="Edit {t.name}"
                  >✎</button>
                  {#if tagIdx < 9}
                    <span class="text-xs text-base-content/50 ml-auto">Alt+{tagIdx + 1}</span>
                  {/if}
                </div>
              {/each}
              {#if (tagsByGroup[group.id] ?? []).length === 0}
                <span class="text-xs text-base-content/50">No tags in this group yet.</span>
              {/if}
              <button
                type="button"
                class="btn btn-ghost btn-xs self-start mt-1 text-base-content/60"
                onclick={() => openCreateModal(group, '', false)}
              >+ New tag</button>
            </div>
          {:else}
            {#if tagsAppliedInGroup(group.id).length > 0}
              <div class="flex flex-wrap gap-1 mb-2">
                {#each tagsAppliedInGroup(group.id) as t (t.id)}
                  <span class="badge {pillClass(t.id, group.name)} gap-1">
                    <button
                      type="button"
                      class="cursor-pointer"
                      onclick={() => openEditModal(t, group.id)}
                      title="Edit tag"
                    >{t.name}</button>
                    <button
                      type="button"
                      class="opacity-70 hover:opacity-100"
                      onclick={() => openEditModal(t, group.id)}
                      title="Edit tag"
                      aria-label="Edit {t.name}"
                    >✎</button>
                    <button
                      type="button"
                      onclick={() => removeTag(group.id, t.id)}
                      title="Remove from video"
                      aria-label="Remove {t.name}"
                    >×</button>
                  </span>
                {/each}
              </div>
            {/if}

            <div class="relative">
              <input
                bind:this={composerInputs[idx]}
                type="text"
                class="input input-bordered w-full pr-14"
                placeholder={group.allowMultiple ? 'Add Tags' : 'Set Tag'}
                value={composer[group.id]?.input ?? ''}
                autocomplete="off"
                oninput={(e) => composer[group.id] = {
                  ...(composer[group.id] ?? { input: '', open: false, highlighted: -1 }),
                  input: (e.target as HTMLInputElement).value,
                  open: true,
                  highlighted: -1
                }}
                onfocus={() => {
                  ensureTagsLoaded(group.id);
                  ensureComposer(group.id);
                  composer[group.id] = { ...composer[group.id], open: true };
                }}
                onkeydown={(e) => onComposerKeyDown(group, e)}
                onblur={() => setTimeout(() => {
                  if (composer[group.id]) composer[group.id] = { ...composer[group.id], open: false };
                }, 200)}
              />
              {#if isNovel(group)}
                <span class="badge badge-accent badge-sm absolute right-2 top-1/2 -translate-y-1/2">NEW</span>
              {/if}

              {#if composer[group.id]?.open && suggestionsFor(group).length > 0}
                <div class="absolute z-10 mt-1 w-full bg-base-100 border border-base-300 rounded shadow-lg max-h-64 overflow-auto">
                  <div class="px-3 py-1 text-xs uppercase tracking-wider text-base-content/60 border-b border-base-300">
                    Existing {group.name}
                  </div>
                  {#each suggestionsFor(group) as t, i (t.id)}
                    {@const q = (composer[group.id]?.input ?? '').toLowerCase().trim()}
                    {@const matchedAlias = q && !t.name.toLowerCase().includes(q)
                      ? t.aliases.find(a => a.toLowerCase().includes(q))
                      : undefined}
                    <button
                      type="button"
                      class="w-full text-left px-3 py-2 hover:bg-base-200 {composer[group.id]?.highlighted === i ? 'bg-base-200' : ''}"
                      onmousedown={() => addTag(group, t)}
                      onmouseenter={() => composer[group.id] = { ...composer[group.id], highlighted: i }}
                    >
                      {t.name}
                      {#if matchedAlias}
                        <span class="text-xs text-base-content/50 ml-2">matched: {matchedAlias}</span>
                      {:else if t.aliases.length > 0}
                        <span class="text-xs text-base-content/50 ml-2">({t.aliases.join(', ')})</span>
                      {/if}
                    </button>
                  {/each}
                </div>
              {/if}
            </div>
          {/if}
        </fieldset>
    {/each}

    <!-- Notes (binds directly to video.notes; saveIfDirty in the player
         picks up changes on Shift+arrow nav). -->
    {#if video}
      <label class="form-control">
        <span class="label-text">Notes</span>
        <textarea class="textarea textarea-bordered" rows="3" bind:value={video.notes}></textarea>
      </label>
    {/if}

    <div class="flex justify-end gap-2">
      <button class="btn" onclick={() => (show = false)} disabled={editingPanel}>Cancel</button>
      <button class="btn btn-primary" onclick={save} disabled={saving || editingPanel}>
        {saving ? 'Saving…' : 'Save'}
      </button>
    </div>
  </div>

  <TagEditModal
    bind:show={editModalShow}
    tag={editingTag}
    tagGroupId={editTagGroupId}
    initialName={editInitialName}
    onSaved={onTagSaved}
    onCanceled={onTagCanceled}
  />
{/if}

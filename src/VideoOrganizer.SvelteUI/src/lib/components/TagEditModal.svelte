<script lang="ts">
  // Universal tag create/edit modal. Used everywhere a tag can be created or
  // edited: inline + Create in autocomplete, pencil ✎ on tag pills/badges,
  // and the Tags Management page. Esc cancels, Ctrl/Cmd+Enter saves.
  //
  // Modes:
  //   tag is non-null         -> edit existing tag
  //   tag is null              -> create new tag. A Group select lets the
  //                              user pick any tag group; tagGroupId (when
  //                              provided) preselects it, otherwise the
  //                              first group is preselected.
  //                              (initialName preseeds the name field)
  import { untrack } from 'svelte';
  import { api, ApiError } from '$lib/api';
  import type { Tag, TagGroup, TagSearchHit } from '$lib/types';

  // Portal action lives in $lib/portal — same one is used by the
  // FfprobeResultModal and the inline clip-preview modal in
  // VideoPlayer. See that file for the rationale on why a fixed-
  // position modal needs to escape its ancestor stacking context.
  import { portal } from '$lib/portal';

  interface Props {
    tag?: Tag | null;
    tagGroupId?: string;
    initialName?: string;
    show: boolean;
    onSaved?: (tag: Tag) => void;
    onCanceled?: () => void;
    // When opening to create a tag with no group preselected (e.g. from the
    // Import tool, issue #65), start with the cursor in a blank Group field so
    // the user picks/creates a group before anything else.
    focusGroup?: boolean;
  }

  let {
    tag = null,
    tagGroupId,
    initialName = '',
    show = $bindable(false),
    onSaved,
    onCanceled,
    focusGroup = false
  }: Props = $props();

  let name = $state('');
  let aliasInput = $state('');
  let aliases = $state<string[]>([]);
  let isFavorite = $state(false);
  let hiddenByDefault = $state(false);
  let notes = $state('');
  let saving = $state(false);
  let error = $state<string | null>(null);
  let nameInputEl: HTMLInputElement | null = $state(null);
  let aliasInputEl: HTMLInputElement | null = $state(null);
  let modalEl: HTMLDivElement | null = $state(null);

  // Group select, both modes. Create: pick where the new tag goes —
  // tagGroupId (when the host passes one) preselects its option,
  // otherwise the first group wins. Edit: picking a different group
  // moves the tag there on Save, keeping every video tagging (the
  // server re-points TagGroupId; VideoTag rows reference the tag by
  // id). Lazy-loaded on first open.
  let groups = $state<TagGroup[]>([]);
  let groupsLoading = false;
  let selectedGroupId = $state<string | undefined>(undefined);

  // Group field is a create-capable typeahead (issue #65): type to filter
  // groups, pick an existing one, or pick "Create … " to make a new group on
  // save. groupInput is the editable text; selectedGroupId is set when it
  // resolves to an existing group, undefined when it'll become a new group.
  let groupInput = $state('');
  let groupOpen = $state(false);
  let groupInputEl: HTMLInputElement | null = $state(null);

  const groupExactMatch = $derived(
    groups.find(g => g.name.trim().toLowerCase() === groupInput.trim().toLowerCase())
  );
  const groupMatches = $derived.by(() => {
    const q = groupInput.trim().toLowerCase();
    const list = q ? groups.filter(g => g.name.toLowerCase().includes(q)) : groups;
    return list.slice(0, 12);
  });
  // True when the typed text is a non-empty name that isn't an existing group.
  const groupIsNew = $derived(groupInput.trim().length > 0 && !groupExactMatch);

  function pickGroup(g: TagGroup) {
    selectedGroupId = g.id;
    groupInput = g.name;
    groupOpen = false;
  }
  function onGroupInput(value: string) {
    groupInput = value;
    groupOpen = true;
    // Resolve to an existing group when the text matches one exactly; else it
    // becomes a new group on save.
    selectedGroupId = groups.find(
      g => g.name.trim().toLowerCase() === value.trim().toLowerCase()
    )?.id;
  }

  const isEdit = $derived(tag !== null && tag !== undefined);

  // When creating a tag, prompt to use an existing one if the entered name
  // already exists as a tag name OR an alias (issue #10). Debounced exact-match
  // search against /tags/search (name + alias).
  //
  // Scoped to the SELECTED GROUP (issue #191): the same name in a *different*
  // group is intentional and distinct (e.g. a Producer "Tom Petty" and an
  // Artist "Tom Petty"), so a cross-group match must NOT nudge the user to
  // reuse the other group's tag. A new group (no selectedGroupId yet) can't
  // collide with anything, so there's nothing to suggest.
  let nameMatches = $state<TagSearchHit[]>([]);
  $effect(() => {
    const q = name.trim();
    const group = selectedGroupId;
    // Only in create mode — an edit renaming into a collision is the server's
    // job, and "use this other tag" makes no sense mid-edit.
    if (!show || tag || q.length < 2 || !group) {
      nameMatches = [];
      return;
    }
    const t = setTimeout(async () => {
      try {
        const hits = await api.searchTags(q);
        const lc = q.toLowerCase();
        nameMatches = hits.filter(
          h => h.tagGroupId === group &&
            (h.name.toLowerCase() === lc || h.aliases.some(a => a.toLowerCase() === lc))
        );
      } catch {
        nameMatches = [];
      }
    }, 250);
    return () => clearTimeout(t);
  });

  // Apply the existing tag instead of creating a duplicate. Pull the full Tag
  // so the host's onSaved gets the same shape it would after a real save.
  async function useExisting(hit: TagSearchHit) {
    saving = true;
    error = null;
    try {
      const full = await api.getTag(hit.tagId);
      show = false;
      onSaved?.(full);
    } catch (e) {
      error = e instanceof ApiError || e instanceof Error ? e.message : String(e);
    } finally {
      saving = false;
    }
  }

  $effect(() => {
    if (!show) return;
    if (tag) {
      name = tag.name;
      aliases = [...tag.aliases];
      isFavorite = tag.isFavorite;
      hiddenByDefault = tag.hiddenByDefault;
      notes = tag.notes;
      selectedGroupId = tag.tagGroupId;
    } else {
      name = initialName;
      aliases = [];
      isFavorite = false;
      hiddenByDefault = false;
      notes = '';
      selectedGroupId = focusGroup ? undefined : tagGroupId;
    }
    error = null;
    aliasInput = '';
    groupInput = ''; // filled from the resolved group's name once groups load
    groupOpen = false;
    // Import opens straight onto a blank Group field (issue #65); otherwise the
    // Name input, where Enter accepts the (pre-filled or typed) name.
    queueMicrotask(() => ((focusGroup && !tag) ? groupInputEl : nameInputEl)?.focus());
  });

  // Load the group list the first time the modal opens (either mode).
  // Separate effect (with the groups read untracked) so the reset effect
  // above doesn't re-fire — and clobber in-progress typing — when the
  // async load lands. The first-group fallback only applies in create
  // mode; edit mode always preselects the tag's current group via the
  // reset effect.
  $effect(() => {
    if (!show) return;
    untrack(() => {
      const afterLoad = () => {
        // Default to the first group in create mode — but NOT when the host
        // wants a blank Group field (import, issue #65).
        if (!selectedGroupId && !tag && !focusGroup) selectedGroupId = groups[0]?.id;
        // Reflect the resolved group in the input, unless the user already
        // started typing (a new group name).
        if (selectedGroupId && groupInput.trim().length === 0) {
          groupInput = groups.find(g => g.id === selectedGroupId)?.name ?? '';
        }
      };
      if (groups.length > 0) { afterLoad(); return; }
      if (groupsLoading) return;
      groupsLoading = true;
      api.listTagGroups()
        .then(gs => { groups = gs; afterLoad(); })
        .catch(e => {
          error = e instanceof Error ? e.message : 'Failed to load tag groups';
        })
        .finally(() => { groupsLoading = false; });
    });
  });

  function addAlias() {
    const a = aliasInput.trim();
    if (!a) return;
    if (aliases.some(x => x.toLowerCase() === a.toLowerCase())) {
      aliasInput = '';
      return;
    }
    aliases = [...aliases, a];
    aliasInput = '';
  }

  function removeAlias(a: string) {
    aliases = aliases.filter(x => x !== a);
  }

  async function save() {
    const trimmed = name.trim();
    if (!trimmed) { error = 'Name is required.'; return; }
    saving = true;
    error = null;
    try {
      // Resolve the group: the selected existing one, or create a new group
      // from the typed name (issue #65).
      let groupId = selectedGroupId;
      if (!groupId) {
        const newName = groupInput.trim();
        if (!newName) { error = 'Pick or name a tag group first.'; saving = false; return; }
        const existing = groups.find(g => g.name.trim().toLowerCase() === newName.toLowerCase());
        if (existing) {
          groupId = existing.id;
        } else {
          const grp = await api.createTagGroup({ name: newName });
          groups = [...groups, grp];
          groupId = grp.id;
        }
      }

      let saved: Tag;
      if (tag) {
        await api.updateTag(tag.id, {
          name: trimmed,
          aliases,
          isFavorite,
          hiddenByDefault,
          sortOrder: tag.sortOrder,
          notes,
          tagGroupId: groupId
        });
        saved = await api.getTag(tag.id);
      } else {
        saved = await api.createTag({
          tagGroupId: groupId,
          name: trimmed,
          aliases,
          isFavorite,
          hiddenByDefault,
          notes
        });
      }
      show = false;
      onSaved?.(saved);
    } catch (e) {
      error = e instanceof ApiError || e instanceof Error ? e.message : String(e);
    } finally {
      saving = false;
    }
  }

  function cancel() {
    show = false;
    onCanceled?.();
  }

  function onKey(e: KeyboardEvent) {
    if (!show) return;
    if (e.key === 'Escape') {
      e.preventDefault();
      cancel();
      return;
    }
    if (e.key === 'Tab' && modalEl) {
      // Focus trap — without this, Tab past Save lands on whatever is in
      // document order behind the modal (the video grid). daisyUI's modal
      // is a plain div, not a <dialog>, so we cycle Tab manually.
      const focusables = Array.from(
        modalEl.querySelectorAll<HTMLElement>(
          'a[href], button, input, select, textarea, [tabindex]:not([tabindex="-1"])'
        )
      ).filter(el => !el.hasAttribute('disabled') && el.offsetParent !== null);
      if (focusables.length === 0) return;
      const first = focusables[0];
      const last = focusables[focusables.length - 1];
      const active = document.activeElement;
      if (e.shiftKey && active === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && active === last) {
        e.preventDefault();
        first.focus();
      } else if (active && !modalEl.contains(active)) {
        // If focus has somehow leaked outside (e.g. browser default), pull
        // it back to the first focusable inside the modal.
        e.preventDefault();
        first.focus();
      }
      return;
    }
    if (e.key === 'Enter' && !e.shiftKey && !e.ctrlKey && !e.metaKey && !e.altKey) {
      const target = e.target as HTMLElement | null;
      // In Notes (textarea), let Enter insert a newline.
      if (target?.tagName === 'TEXTAREA') return;
      // In the alias input, the input's own onAliasKey handler already
      // captured Enter (and stops propagation) to add the alias.
      if (target === aliasInputEl) return;
      e.preventDefault();
      save();
    }
  }

  function onAliasKey(e: KeyboardEvent) {
    if (e.key === 'Enter') {
      e.preventDefault();
      e.stopPropagation();
      addAlias();
    }
  }
</script>

<svelte:window onkeydown={show ? onKey : undefined} />

{#if show}
  <!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
  <!-- use:portal moves this node to <body> so the modal renders over
       everything regardless of where the component is instantiated.
       Without it, when this component lives inside the /browse hover
       tags-strip (sticky + overflow-hidden) the modal would render
       clipped or behind the video.
       Inline z-index forces it above the daisyUI default (999); the
       sticky+z parents on /browse (player z-10, sidebar z-20) sit in
       the same root stacking context after the portal moves us to
       body, but some browsers still get the layering wrong without a
       big-numbered hint. -->
  <div bind:this={modalEl} use:portal class="modal modal-open" role="dialog" aria-modal="true" tabindex="-1" style="z-index: 9999;">
    <div class="modal-box max-w-md">
      <h3 class="font-bold text-lg mb-3">{isEdit ? 'Edit Tag' : 'New Tag'}</h3>

      {#if error}
        <div class="alert alert-error text-sm mb-3">
          <span>{error}</span>
        </div>
      {/if}

      {#if !isEdit || groups.length > 0}
        <!-- Group select, both modes. Create: the new tag can go into
             any group — preselected from the host's tagGroupId when
             one was passed (e.g. the per-group composer in
             EditTagsPanel), otherwise the first group. Edit: picking
             a different group moves the tag there on Save — every
             video keeps its tagging (the move re-points the tag row;
             video↔tag links are by id). -->
        <div class="flex items-center gap-2 mb-1">
          <span class="label-text w-20 shrink-0">Group</span>
          <div class="relative flex-1">
            <input
              bind:this={groupInputEl}
              type="text"
              class="input input-bordered w-full pr-12"
              placeholder="Type to find or create a group"
              value={groupInput}
              autocomplete="off"
              oninput={(e) => onGroupInput((e.target as HTMLInputElement).value)}
              onfocus={() => (groupOpen = true)}
              onblur={() => setTimeout(() => (groupOpen = false), 150)}
            />
            {#if groupIsNew}
              <span class="badge badge-accent badge-sm absolute right-2 top-1/2 -translate-y-1/2">NEW</span>
            {/if}
            {#if groupOpen && (groupMatches.length > 0 || groupIsNew)}
              <div class="absolute z-20 mt-1 w-full bg-base-300 border-2 border-primary rounded-md shadow-2xl ring-4 ring-primary/30 max-h-56 overflow-auto">
                {#each groupMatches as g (g.id)}
                  <button
                    type="button"
                    class="w-full text-left px-3 py-2 hover:bg-base-200"
                    onmousedown={() => pickGroup(g)}
                  >{g.name}</button>
                {/each}
                {#if groupIsNew}
                  <button
                    type="button"
                    class="w-full text-left px-3 py-2 hover:bg-base-200 text-accent"
                    onmousedown={() => (groupOpen = false)}
                  >+ Create new group “{groupInput.trim()}”</button>
                {/if}
              </div>
            {/if}
          </div>
        </div>
        {#if tag && selectedGroupId !== tag.tagGroupId}
          <p class="text-xs text-info mb-3 ml-22">
            Saving moves this tag to the selected group — all videos
            tagged with it stay tagged.
          </p>
        {:else}
          <div class="mb-3"></div>
        {/if}
      {/if}

      <!-- Name + Favorite star inline. Labels share a fixed width so Name,
           Aliases, and Notes all line up on the left edge. -->
      <div class="flex items-center gap-2 mb-3">
        <span class="label-text w-20 shrink-0">Name</span>
        <input
          bind:this={nameInputEl}
          class="input input-bordered flex-1"
          bind:value={name}
        />
        <button
          type="button"
          class="leading-none cursor-pointer flex items-center shrink-0"
          onclick={() => (isFavorite = !isFavorite)}
          title={isFavorite ? 'Unfavorite this tag' : 'Mark this tag as a favorite'}
          aria-label={isFavorite ? 'Unfavorite' : 'Favorite'}
          aria-pressed={isFavorite}
        >
          <svg viewBox="0 0 24 24" class="h-7 w-7"
            fill={isFavorite ? 'rgb(234 179 8)' : 'none'}
            stroke="rgb(255 255 255 / 0.85)" stroke-width="1.25" stroke-linejoin="round">
            <path d="M12 2.5 L14.6 8.9 L21.5 9.5 L16.2 14.1 L17.8 20.9 L12 17.3 L6.2 20.9 L7.8 14.1 L2.5 9.5 L9.4 8.9 Z" />
          </svg>
        </button>
      </div>

      <!-- Existing-tag prompt (create mode). If the entered name already
           exists as a tag name or alias, offer to use that tag instead of
           creating a duplicate. (issue #10) -->
      {#if !isEdit && nameMatches.length > 0}
        <div class="alert alert-warning text-sm mb-3 flex-col items-start gap-2">
          <span>“{name.trim()}” already exists — use the existing tag?</span>
          <div class="flex flex-col gap-1 w-full">
            {#each nameMatches as m (m.tagId)}
              {@const viaAlias = m.name.toLowerCase() !== name.trim().toLowerCase()}
              <div class="flex items-center justify-between gap-2 w-full">
                <span class="min-w-0 truncate">
                  <span class="font-medium">{m.name}</span>
                  <span class="text-xs opacity-70">in {m.tagGroupName}</span>
                  {#if viaAlias}<span class="text-xs opacity-70">· alias “{name.trim()}”</span>{/if}
                </span>
                <button
                  type="button"
                  class="btn btn-xs btn-primary shrink-0"
                  onclick={() => useExisting(m)}
                  disabled={saving}
                >Use this tag</button>
              </div>
            {/each}
          </div>
        </div>
      {/if}

      <div class="mb-3">
        {#if aliases.length > 0}
          <div class="flex flex-wrap gap-1 mb-2 ml-22">
            {#each aliases as a (a)}
              <span class="badge badge-outline gap-1">
                {a}
                <button
                  type="button"
                  class="btn btn-ghost btn-xs px-1 leading-none"
                  onclick={() => removeAlias(a)}
                  aria-label="Remove alias {a}"
                >×</button>
              </span>
            {/each}
          </div>
        {/if}
        <div class="flex items-center gap-2">
          <span class="label-text w-20 shrink-0">Aliases</span>
          <input
            bind:this={aliasInputEl}
            class="input input-bordered flex-1"
            placeholder="Add alias and press Enter"
            bind:value={aliasInput}
            onkeydown={onAliasKey}
            autocomplete="off"
          />
          <button
            type="button"
            class="btn btn-sm btn-soft btn-primary btn-cta"
            onclick={addAlias}
            disabled={!aliasInput.trim()}
          >Add</button>
        </div>
      </div>

      <div class="flex items-start gap-2 mb-3">
        <span class="label-text w-20 shrink-0 mt-2">Notes</span>
        <textarea class="textarea textarea-bordered flex-1" rows="2" bind:value={notes}></textarea>
      </div>

      <!-- Hidden-by-default (issue #84/#192): videos with this tag stay out of
           the grid unless the user filters for the tag. Moved here from the
           old Tag Actions dialog so the Edit Tag modal is the one place to
           manage a tag. -->
      <div class="flex items-start gap-2 mb-3">
        <span class="label-text w-20 shrink-0 mt-1">Hidden</span>
        <label class="flex items-center gap-2 cursor-pointer">
          <input type="checkbox" class="toggle toggle-sm toggle-primary" bind:checked={hiddenByDefault} />
          <span class="text-xs text-base-content/60">Hide videos with this tag unless you filter for it.</span>
        </label>
      </div>

      <p class="text-xs text-base-content/50 mb-2">Enter to save · Esc to cancel · Tab to edit aliases / favorite / notes</p>

      <div class="modal-action">
        <button type="button" class="btn btn-cancel" onclick={cancel}>Cancel</button>
        <button
          type="button"
          class="btn btn-soft btn-primary btn-cta"
          onclick={save}
          disabled={saving || !name.trim() || (!isEdit && !selectedGroupId && groupInput.trim().length === 0)}
        >
          {saving ? 'Saving…' : (isEdit ? 'Save' : 'Create')}
        </button>
      </div>
    </div>
    <div class="modal-backdrop"></div>
  </div>
{/if}

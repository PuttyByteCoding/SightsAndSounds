<script lang="ts">
  // Universal tag create/edit modal. Used everywhere a tag can be created or
  // edited: inline + Create in autocomplete, pencil ✎ on tag pills/badges,
  // and the Tags Management page. Esc cancels, Ctrl/Cmd+Enter saves.
  //
  // Modes:
  //   tag is non-null         -> edit existing tag
  //   tag is null + tagGroupId -> create new tag in that group
  //                              (initialName preseeds the name field)
  import { api, ApiError } from '$lib/api';
  import type { Tag } from '$lib/types';

  // Portal action — re-parents the element to <body> on mount so the
  // modal escapes every ancestor stacking context, transform-
  // containing-block, and overflow-hidden it might be sitting under
  // (e.g. inside the EditTagsPanel hover strip on /browse, or any
  // sticky+z-index card). Position-fixed inside the portaled element
  // then renders relative to the viewport unconditionally.
  //
  // On destroy we just `node.remove()` — putting the node back into
  // the original parent (which we tried first) confused Svelte's own
  // unmount of the {#if show} block: the modal stayed in body when
  // show flipped to false. `node.remove()` is idempotent and works
  // regardless of where the node is currently attached.
  function portal(node: HTMLElement) {
    document.body.appendChild(node);
    return {
      destroy() { node.remove(); }
    };
  }

  interface Props {
    tag?: Tag | null;
    tagGroupId?: string;
    initialName?: string;
    show: boolean;
    onSaved?: (tag: Tag) => void;
    onCanceled?: () => void;
  }

  let {
    tag = null,
    tagGroupId,
    initialName = '',
    show = $bindable(false),
    onSaved,
    onCanceled
  }: Props = $props();

  let name = $state('');
  let aliasInput = $state('');
  let aliases = $state<string[]>([]);
  let isFavorite = $state(false);
  let notes = $state('');
  let saving = $state(false);
  let error = $state<string | null>(null);
  let nameInputEl: HTMLInputElement | null = $state(null);
  let aliasInputEl: HTMLInputElement | null = $state(null);
  let modalEl: HTMLDivElement | null = $state(null);

  const isEdit = $derived(tag !== null && tag !== undefined);

  $effect(() => {
    if (!show) return;
    if (tag) {
      name = tag.name;
      aliases = [...tag.aliases];
      isFavorite = tag.isFavorite;
      notes = tag.notes;
    } else {
      name = initialName;
      aliases = [];
      isFavorite = false;
      notes = '';
    }
    error = null;
    aliasInput = '';
    // Always land on the Name input — Enter then accepts the (pre-filled or
    // typed) name with default aliases/favorite/notes. Tab to reach those.
    queueMicrotask(() => nameInputEl?.focus());
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
      let saved: Tag;
      if (tag) {
        await api.updateTag(tag.id, {
          name: trimmed,
          aliases,
          isFavorite,
          sortOrder: tag.sortOrder,
          notes
        });
        saved = await api.getTag(tag.id);
      } else if (tagGroupId) {
        saved = await api.createTag({
          tagGroupId,
          name: trimmed,
          aliases,
          isFavorite,
          notes
        });
      } else {
        error = 'Internal error: no tag and no tagGroupId.';
        saving = false;
        return;
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

      <p class="text-xs text-base-content/50 mb-2">Enter to save · Esc to cancel · Tab to edit aliases / favorite / notes</p>

      <div class="modal-action">
        <button type="button" class="btn btn-cancel" onclick={cancel}>Cancel</button>
        <button
          type="button"
          class="btn btn-soft btn-primary btn-cta"
          onclick={save}
          disabled={saving || !name.trim()}
        >
          {saving ? 'Saving…' : (isEdit ? 'Save' : 'Create')}
        </button>
      </div>
    </div>
    <div class="modal-backdrop"></div>
  </div>
{/if}

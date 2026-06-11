<script lang="ts">
  // Manage key → tag bindings (see $lib/tagKeyBindings.svelte). Opened
  // from the ⌨ button in the VideoPlayer toolbar. Top half lists the
  // current bindings with an unbind affordance; bottom half is an
  // "Add binding" form: pick a free key, search for a tag, Add.
  import { api } from '$lib/api';
  import type { Tag } from '$lib/types';
  import { portal } from '$lib/portal';
  import { tagKeyBindings, BINDABLE_KEYS } from '$lib/tagKeyBindings.svelte';

  interface Props {
    show: boolean;
  }

  let { show = $bindable(false) }: Props = $props();

  // Full tag inventory for the search box. Loaded once per page life —
  // the list is small (hundreds at most) and the modal may open many
  // times in a tagging session.
  let allTags = $state<Tag[]>([]);
  let tagsLoaded = false;
  let error = $state<string | null>(null);

  let selectedKey = $state<string>('');
  let tagQuery = $state('');
  let selectedTag = $state<Tag | null>(null);

  $effect(() => {
    if (!show) return;
    error = null;
    tagQuery = '';
    selectedTag = null;
    // Preselect the first free key so "search tag → Enter" is the
    // whole flow for the common case.
    selectedKey = tagKeyBindings.availableKeys[0] ?? '';
    if (!tagsLoaded) {
      tagsLoaded = true;
      api.listTags()
        .then((tags) => { allTags = tags; })
        .catch((e) => {
          tagsLoaded = false;
          error = e instanceof Error ? e.message : 'Failed to load tags';
        });
    }
  });

  const suggestions = $derived.by<Tag[]>(() => {
    const q = tagQuery.trim().toLowerCase();
    if (!q || selectedTag) return [];
    return allTags
      .filter((t) =>
        t.name.toLowerCase().includes(q)
        || t.aliases.some((a) => a.toLowerCase().includes(q)))
      .slice(0, 10);
  });

  function pickTag(t: Tag) {
    selectedTag = t;
    tagQuery = t.name;
  }

  function addBinding() {
    if (!selectedKey || !selectedTag) return;
    tagKeyBindings.bind({
      key: selectedKey,
      tagId: selectedTag.id,
      tagName: selectedTag.name,
      tagGroupId: selectedTag.tagGroupId,
      tagGroupName: selectedTag.tagGroupName
    });
    tagQuery = '';
    selectedTag = null;
    selectedKey = tagKeyBindings.availableKeys[0] ?? '';
  }

  // Sorted view: F-keys first in numeric order, then letters — same
  // order as BINDABLE_KEYS so the list reads predictably.
  const sortedBindings = $derived(
    [...tagKeyBindings.bindings].sort(
      (a, b) => BINDABLE_KEYS.indexOf(a.key) - BINDABLE_KEYS.indexOf(b.key))
  );

  function close() {
    show = false;
  }

  function onKey(e: KeyboardEvent) {
    if (!show) return;
    if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      close();
      return;
    }
    if (e.key === 'Enter') {
      // Enter adds when the form is complete; otherwise picks the top
      // suggestion so "type a few letters → Enter → Enter" works.
      e.preventDefault();
      e.stopPropagation();
      if (selectedTag) addBinding();
      else if (suggestions.length > 0) pickTag(suggestions[0]);
    }
  }

  function keyLabel(key: string): string {
    return key.length === 1 ? key.toUpperCase() : key;
  }
</script>

<svelte:window onkeydowncapture={show ? onKey : undefined} />

{#if show}
  <div use:portal class="modal modal-open" role="dialog" aria-modal="true" aria-labelledby="tag-key-bindings-title" style="z-index: 9999;">
    <div class="modal-box max-w-lg">
      <h3 id="tag-key-bindings-title" class="font-bold text-lg mb-1">Tag Key Bindings</h3>
      <p class="text-sm text-base-content/60 mb-3">
        Press a bound key in the player to toggle its tag on the current video.
      </p>

      {#if error}
        <div class="alert alert-error text-sm mb-3"><span>{error}</span></div>
      {/if}

      {#if sortedBindings.length > 0}
        <div class="border border-base-300 rounded p-2 mb-4 space-y-1">
          {#each sortedBindings as b (b.key)}
            <div class="flex items-center gap-2 text-sm">
              <kbd class="kbd kbd-sm w-12 justify-center">{keyLabel(b.key)}</kbd>
              <span class="truncate">{b.tagName}</span>
              <span class="text-xs text-base-content/50 italic truncate">{b.tagGroupName}</span>
              <button
                type="button"
                class="btn btn-ghost btn-xs ml-auto text-base-content/60 hover:text-error"
                onclick={() => tagKeyBindings.unbind(b.key)}
                aria-label="Unbind {keyLabel(b.key)}"
                title="Remove this binding"
              >×</button>
            </div>
          {/each}
        </div>
      {:else}
        <p class="text-sm text-base-content/50 italic mb-4">No bindings yet.</p>
      {/if}

      <div class="border border-base-300 rounded p-3 space-y-2">
        <div class="text-sm font-medium">Add binding</div>
        <div class="flex items-center gap-2">
          <select
            class="select select-bordered select-sm w-24"
            bind:value={selectedKey}
            aria-label="Key to bind"
          >
            {#each tagKeyBindings.availableKeys as k (k)}
              <option value={k}>{keyLabel(k)}</option>
            {/each}
          </select>
          <div class="relative flex-1">
            <input
              type="text"
              class="input input-bordered input-sm w-full"
              placeholder="Search tags…"
              bind:value={tagQuery}
              oninput={() => (selectedTag = null)}
              autocomplete="off"
            />
            {#if suggestions.length > 0}
              <div class="absolute z-10 mt-1 w-full bg-base-100 border border-base-300 rounded-md shadow-xl max-h-56 overflow-auto">
                {#each suggestions as t (t.id)}
                  <button
                    type="button"
                    class="w-full text-left px-3 py-1.5 text-sm hover:bg-base-200 flex items-center justify-between gap-2"
                    onclick={() => pickTag(t)}
                  >
                    <span class="truncate">{t.name}</span>
                    <span class="text-xs text-base-content/50 italic shrink-0">{t.tagGroupName}</span>
                  </button>
                {/each}
              </div>
            {/if}
          </div>
          <button
            type="button"
            class="btn btn-sm btn-soft btn-primary btn-cta"
            onclick={addBinding}
            disabled={!selectedKey || !selectedTag}
          >Add</button>
        </div>
        {#if tagKeyBindings.availableKeys.length === 0}
          <p class="text-xs text-base-content/50">Every bindable key is in use — remove a binding to free one.</p>
        {/if}
      </div>

      <p class="text-xs text-base-content/50 mt-3">
        Available keys are the ones the player doesn't already use for
        shortcuts. Bindings are saved in this browser.
      </p>

      <div class="modal-action">
        <button type="button" class="btn btn-sm" onclick={close}>Close</button>
      </div>
    </div>
    <button type="button" class="modal-backdrop" aria-label="Close" onclick={close}></button>
  </div>
{/if}

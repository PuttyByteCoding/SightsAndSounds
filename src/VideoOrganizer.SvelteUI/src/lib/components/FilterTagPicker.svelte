<script lang="ts">
  // Multi-tag autocomplete used by the player's required/optional/excluded
  // boxes. Wraps /api/tags/search; each pick adds a FilterTag (type='tag').
  import { api } from '$lib/api';
  import type { FilterTag, TagSearchHit } from '$lib/types';
  import { pillClass } from '$lib/tagColors';

  interface Props {
    label: string;
    values: FilterTag[];
    hint?: string;
    placeholder?: string;
  }

  let { label, values = $bindable(), hint, placeholder = 'Type to search…' }: Props = $props();

  let inputText = $state('');
  let suggestions = $state<TagSearchHit[]>([]);
  let showSuggestions = $state(false);
  let highlighted = $state(-1);
  let debounceTimer: ReturnType<typeof setTimeout> | null = null;

  function keyOf(v: FilterTag) { return `${v.type}::${v.value.toLowerCase()}`; }
  function hitKey(h: TagSearchHit) { return `tag::${h.tagId.toLowerCase()}`; }

  async function runSearch(query: string) {
    const q = query.trim();
    if (q.length === 0) {
      suggestions = []; showSuggestions = false; return;
    }
    try {
      const results = await api.searchTags(q);
      const existing = new Set(values.map(keyOf));
      suggestions = results.filter(r => !existing.has(hitKey(r)));
      showSuggestions = suggestions.length > 0;
      highlighted = suggestions.length > 0 ? 0 : -1;
    } catch {
      suggestions = []; showSuggestions = false;
    }
  }

  function onInput(ev: Event) {
    inputText = (ev.target as HTMLInputElement).value;
    if (debounceTimer) clearTimeout(debounceTimer);
    debounceTimer = setTimeout(() => runSearch(inputText), 250);
  }

  function pick(h: TagSearchHit) {
    if (!values.some(v => keyOf(v) === hitKey(h))) {
      values.push({ type: 'tag', value: h.tagId, label: h.name, tagGroupName: h.tagGroupName });
    }
    inputText = ''; suggestions = []; showSuggestions = false; highlighted = -1;
  }

  function remove(i: number) { values.splice(i, 1); }

  async function onFocus() {
    if (inputText.trim().length > 0) await runSearch(inputText);
  }
  function onBlur() {
    setTimeout(() => { showSuggestions = false; highlighted = -1; }, 200);
  }

  function onKeyDown(e: KeyboardEvent) {
    const hasList = showSuggestions && suggestions.length > 0;
    if (!hasList) {
      if (e.key === 'Escape') { showSuggestions = false; highlighted = -1; }
      return;
    }
    switch (e.key) {
      case 'ArrowDown':
        e.preventDefault();
        highlighted = (highlighted + 1) % suggestions.length; break;
      case 'ArrowUp':
        e.preventDefault();
        highlighted = highlighted <= 0 ? suggestions.length - 1 : highlighted - 1; break;
      case 'Tab':
        e.preventDefault();
        highlighted = highlighted < 0 ? 0 : (highlighted + 1) % suggestions.length; break;
      case 'Enter':
        e.preventDefault();
        if (highlighted >= 0 && highlighted < suggestions.length) pick(suggestions[highlighted]);
        break;
      case 'Escape':
        showSuggestions = false; highlighted = -1; break;
    }
  }
</script>

<div class="form-control">
  <div class="flex items-baseline gap-2">
    <span class="text-sm font-medium">{label}</span>
    {#if hint}<span class="text-xs text-base-content/60">{hint}</span>{/if}
  </div>

  <div class="relative mt-1">
    <input
      type="text"
      class="input input-bordered input-sm w-full"
      value={inputText}
      oninput={onInput}
      onkeydown={onKeyDown}
      onfocus={onFocus}
      onblur={onBlur}
      {placeholder}
      autocomplete="off"
    />

    {#if showSuggestions && suggestions.length > 0}
      <div class="absolute z-20 mt-1 w-full bg-base-100 border border-base-300 rounded shadow-lg max-h-72 overflow-auto">
        {#each suggestions as s, i (s.tagId)}
          <button
            type="button"
            class="w-full text-left px-3 py-1.5 flex items-center justify-between gap-2 hover:bg-base-200 {i === highlighted ? 'bg-base-200' : ''}"
            onmousedown={() => pick(s)}
            onmouseenter={() => (highlighted = i)}
          >
            <span class="truncate">{s.name}</span>
            <span class="text-xs text-base-content/50 uppercase tracking-wide shrink-0">{s.tagGroupName}</span>
          </button>
        {/each}
      </div>
    {/if}
  </div>

  {#if values.length > 0}
    <div class="flex flex-wrap gap-1 mt-2">
      {#each values as v, i (i + '-' + v.type + '-' + v.value)}
        <span class="badge {pillClass(v.value, v.tagGroupName)} gap-1">
          {v.label}
          <button class="btn btn-ghost btn-xs px-1 leading-none" onclick={() => remove(i)} aria-label="Remove {v.label}">×</button>
        </span>
      {/each}
    </div>
  {/if}
</div>

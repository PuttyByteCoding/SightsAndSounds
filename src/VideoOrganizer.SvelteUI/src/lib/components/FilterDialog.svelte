<script lang="ts">
  // Routing dialog. Every tag click parks the tag in filterStore.pending,
  // and this dialog asks the user to put it in Required / Optional / Excluded.
  import { filterStore } from '$lib/filterStore.svelte';

  function onKey(e: KeyboardEvent) {
    if (e.key === 'Escape') {
      e.preventDefault();
      filterStore.cancelPending();
    } else if (e.key === 'Enter' && (e.target as HTMLElement | null)?.tagName !== 'INPUT') {
      e.preventDefault();
      filterStore.applyPending('optional');
    }
  }
</script>

<svelte:window onkeydown={filterStore.pending ? onKey : undefined} />

{#if filterStore.pending}
  {@const p = filterStore.pending}
  <!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
  <div class="modal modal-open" role="dialog" aria-modal="true" tabindex="-1">
    <div class="modal-box max-w-md">
      <h3 class="font-semibold text-lg mb-1">Filter</h3>
      <div class="text-base-content/70 mb-4">
        {#if p.tagGroupName}
          <span class="uppercase tracking-wide text-xs">{p.tagGroupName}:</span>
        {:else}
          <span class="uppercase tracking-wide text-xs">{p.type}:</span>
        {/if}
        <span class="font-medium">{p.label}</span>
      </div>

      <div class="text-xs uppercase tracking-wide text-base-content/60 mb-1">Add to filter</div>
      <div class="flex gap-2">
        <button class="btn btn-sm btn-soft btn-success border border-success/50 flex-1" onclick={() => filterStore.applyPending('required')}>Required</button>
        <button class="btn btn-sm btn-soft btn-info border border-info/50 flex-1" onclick={() => filterStore.applyPending('optional')}>Optional</button>
        <button class="btn btn-sm btn-soft btn-error border border-error/50 flex-1" onclick={() => filterStore.applyPending('excluded')}>Exclude</button>
      </div>
      <div class="modal-action mt-4">
        <button class="btn btn-sm btn-cancel" onclick={() => filterStore.cancelPending()}>Cancel</button>
      </div>
    </div>
    <div class="modal-backdrop"></div>
  </div>
{/if}

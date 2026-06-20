<script lang="ts">
  // Global error banner (issue #201): any API call that returns 4xx/5xx pushes
  // a message onto errorBanner; each shows here for ~3s then fades out. Mounted
  // once in the root layout so it overlays every page. Stacks newest-on-top in
  // case several fail at once. Clickable to dismiss early.
  import { fade } from 'svelte/transition';
  import { errorBanner } from '$lib/errorBanner.svelte';
</script>

{#if errorBanner.entries.length > 0}
  <div class="fixed top-3 left-1/2 -translate-x-1/2 z-[100] flex flex-col gap-2 w-full max-w-md px-3 pointer-events-none">
    {#each errorBanner.entries as entry (entry.id)}
      <div transition:fade={{ duration: 300 }} class="pointer-events-auto">
        <div
          class="alert alert-error shadow-lg text-sm flex items-start gap-2"
          role="alert"
          aria-live="assertive"
        >
          <span class="flex-1 break-words">{entry.message}</span>
          <button
            class="btn btn-xs btn-ghost shrink-0"
            aria-label="Dismiss"
            onclick={() => errorBanner.dismiss(entry.id)}
          >✕</button>
        </div>
      </div>
    {/each}
  </div>
{/if}

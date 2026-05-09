<script lang="ts">
  // Warning banner shown on pages that surface local-only
  // affordances (reveal in file manager, run ffprobe) when the
  // browser isn't on the same machine as the API. Reads
  // runtimeStore which is loaded once at app boot from the layout's
  // onMount; the banner appears the moment that fetch confirms
  // !isLocal, and stays for the session unless the user dismisses
  // it. Pages where local access doesn't gate any feature don't
  // include this component at all — keeps the warning relevant.
  import { runtimeStore } from '$lib/runtimeStore.svelte';
</script>

{#if runtimeStore.loaded && !runtimeStore.isLocal && !runtimeStore.dismissed}
  <div class="alert alert-warning mb-4 text-sm flex items-start gap-3">
    <svg viewBox="0 0 24 24" class="h-5 w-5 fill-current shrink-0" aria-hidden="true">
      <path d="M12 2 L1 21 H23 L12 2 Z M12 9 V14 M12 17 V18" stroke="currentColor" stroke-width="2" fill="none" stroke-linecap="round" stroke-linejoin="round"/>
    </svg>
    <div class="flex-1">
      <div class="font-semibold">Must be on computer with SightsAndSounds server.</div>
      <div class="text-xs opacity-80 mt-0.5">
        Some diagnostic features (reveal file in folder, run ffprobe) only work when
        the browser is running on the same machine as the API. Other features work normally.
      </div>
    </div>
    <button
      type="button"
      class="btn btn-ghost btn-xs"
      onclick={() => runtimeStore.dismiss()}
      aria-label="Dismiss"
    >×</button>
  </div>
{/if}

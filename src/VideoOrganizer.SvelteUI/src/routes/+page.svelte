<script lang="ts">
  import { onMount } from 'svelte';
  import { api, ApiError } from '$lib/api';

  let count = $state<number | null>(null);
  let error = $state<string | null>(null);

  onMount(async () => {
    try {
      count = await api.getVideoCount();
    } catch (e) {
      error = e instanceof ApiError ? e.message : String(e);
    }
  });
</script>

<div class="prose max-w-none">
  <h1>Video Organizer</h1>
  <p>Welcome. Pick a destination from the sidebar.</p>

  <div class="stats shadow mt-6 not-prose">
    <div class="stat">
      <div class="stat-title">Total videos</div>
      <div class="stat-value">
        {#if error}
          <span class="text-error text-base">API unreachable</span>
        {:else if count === null}
          <span class="loading loading-spinner"></span>
        {:else}
          {count}
        {/if}
      </div>
      {#if error}
        <div class="stat-desc text-error/80 text-xs">{error}</div>
      {/if}
    </div>
  </div>
</div>

<script lang="ts">
  // OIDC redirect target (#124, Phase 3). Keycloak sends the browser back here
  // with ?code=…; we exchange it for tokens and return to where login started.
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { auth } from '$lib/auth.svelte';

  let error = $state<string | null>(null);

  onMount(async () => {
    try {
      const returnTo = await auth.completeLogin();
      await goto(returnTo, { replaceState: true });
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    }
  });
</script>

<div class="min-h-screen flex items-center justify-center p-6">
  {#if error}
    <div class="card bg-base-200 p-8 max-w-md w-full text-center space-y-4">
      <h1 class="text-xl font-semibold text-error">Sign-in failed</h1>
      <p class="text-sm text-base-content/70 break-words">{error}</p>
      <a class="btn btn-sm" href="/">Back to app</a>
    </div>
  {:else}
    <div class="flex items-center gap-3 text-base-content/70">
      <span class="loading loading-spinner loading-md"></span>
      Signing you in…
    </div>
  {/if}
</div>

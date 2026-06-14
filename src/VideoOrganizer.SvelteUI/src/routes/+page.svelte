<script lang="ts">
  // Temporary startup-status / diagnostics landing page (issue #71). First load
  // is slow and some parts don't finish; the StartupStatus component runs the
  // data fetches that drive first paint, times each, and flags anything over 2
  // seconds — so it's clear what's hanging and where the time goes. The same
  // component is reused as the slow-load dialog on the Videos page.
  import StartupStatus from '$lib/components/StartupStatus.svelte';
</script>

<div class="p-6 max-w-3xl space-y-6">
  <header>
    <h1 class="text-2xl font-semibold">Startup status</h1>
    <p class="text-sm text-base-content/70 mt-1">
      Temporary diagnostic landing page (issue #71): times the data loads that make
      first paint slow. Anything taking more than 2 seconds is highlighted.
    </p>
  </header>

  <div class="flex flex-wrap items-center gap-3">
    <a class="btn btn-primary btn-sm" href="/browse">Continue to Videos →</a>
  </div>

  <StartupStatus />
</div>

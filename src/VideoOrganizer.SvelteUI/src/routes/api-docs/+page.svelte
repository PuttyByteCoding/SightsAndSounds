<script lang="ts">
  // Embeds the API's Swagger UI in an iframe so the user can browse / try
  // endpoints without leaving the app. /swagger is served by the API host;
  // in dev the Vite proxy forwards it to :5098.
</script>

<svelte:head><title>API - Video Organizer</title></svelte:head>

<div class="flex flex-col h-[calc(100vh-3rem)] gap-2">
  <div class="flex items-baseline justify-between">
    <h1 class="text-2xl font-semibold">API</h1>
    <a href="/swagger" target="_blank" rel="noopener" class="text-xs link link-hover text-base-content/60">
      open in new tab ↗
    </a>
  </div>
  <iframe
    src="/swagger"
    title="Swagger UI"
    class="flex-1 w-full bg-base-100 rounded border border-base-300"
  ></iframe>
</div>

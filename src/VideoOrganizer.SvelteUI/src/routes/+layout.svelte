<script lang="ts">
  import '../app.css';
  import { page } from '$app/state';
  import { onMount, onDestroy } from 'svelte';
  import { api } from '$lib/api';
  import { playbackSettings } from '$lib/playbackSettings.svelte';

  let { children } = $props();

  // --- Background-task status poll ----------------------------------------
  // Shows up only when there's work pending. Poll every 5s while anything is
  // pending; slow to 30s when idle so we're not hammering the API during
  // normal browsing.
  let md5Pending = $state<number | null>(null);
  let md5Total = $state<number | null>(null);
  let thumbPending = $state<number | null>(null);
  let thumbTotal = $state<number | null>(null);
  let statusTimer: ReturnType<typeof setTimeout> | null = null;

  async function pollBackgroundStatus() {
    try {
      const m = await api.getMd5BackfillStatus();
      md5Pending = m.pending;
      md5Total = m.total;
    } catch { /* non-fatal — backend may not be up yet at first render */ }
    try {
      const t = await api.getThumbnailStatus();
      thumbPending = t.pending;
      thumbTotal = t.total;
    } catch { /* non-fatal */ }

    const hasWork = (md5Pending ?? 0) > 0 || (thumbPending ?? 0) > 0;
    statusTimer = setTimeout(pollBackgroundStatus, hasWork ? 5000 : 30000);
  }
  onMount(pollBackgroundStatus);
  onDestroy(() => {
    if (statusTimer) clearTimeout(statusTimer);
  });

  // Renders a skip amount as "±Ns" or "±Nm" (or "±NmSs" mid-minute), signed
  // to match its direction.
  function formatSeek(seconds: number, direction: '+' | '-'): string {
    if (seconds < 60) return `${direction}${seconds}s`;
    const wholeM = Math.floor(seconds / 60);
    const s = seconds - wholeM * 60;
    return s === 0 ? `${direction}${wholeM}m` : `${direction}${wholeM}m${s}s`;
  }

  const nav = [
    { href: '/browse', label: 'Videos', icon: 'M4 5h16v3H4V5zm0 5h7v9H4v-9zm9 0h7v4h-7v-4zm0 6h7v3h-7v-3z' },
    { href: '/history', label: 'History', icon: 'M13 3a9 9 0 1 0 9 9h-2a7 7 0 1 1-7-7V3zm-1 5v5l4 2 .72-1.45L13.5 12V8H12z' },
    { href: '/tags',   label: 'Tag Management', icon: 'M21.41 11.58l-9-9A2 2 0 0 0 11 2H4a2 2 0 0 0-2 2v7a2 2 0 0 0 .59 1.42l9 9a2 2 0 0 0 2.83 0l7-7a2 2 0 0 0 0-2.84ZM7 7.5A1.5 1.5 0 1 1 7 4.5a1.5 1.5 0 0 1 0 3Z' },
    { href: '/import', label: 'Import Tool', icon: 'M12 2a1 1 0 0 1 1 1v8h8a1 1 0 1 1 0 2h-8v8a1 1 0 1 1-2 0v-8H3a1 1 0 1 1 0-2h8V3a1 1 0 0 1 1-1Z' },
    { href: '/config', label: 'Configuration', icon: 'M19.43 12.98c.04-.32.07-.64.07-.98s-.03-.66-.07-.98l2.11-1.65c.19-.15.24-.42.12-.64l-2-3.46c-.12-.22-.39-.3-.61-.22l-2.49 1c-.52-.4-1.08-.73-1.69-.98l-.38-2.65A.488.488 0 0 0 14 2h-4c-.24 0-.45.18-.48.42l-.38 2.65c-.61.25-1.17.59-1.69.98l-2.49-1c-.23-.09-.49 0-.61.22l-2 3.46c-.12.22-.07.49.12.64l2.11 1.65c-.04.32-.07.65-.07.98s.03.66.07.98l-2.11 1.65c-.19.15-.24.42-.12.64l2 3.46c.12.22.39.3.61.22l2.49-1c.52.4 1.08.73 1.69.98l.38 2.65c.03.24.24.42.48.42h4c.24 0 .45-.18.48-.42l.38-2.65c.61-.25 1.17-.59 1.69-.98l2.49 1c.23.09.49 0 .61-.22l2-3.46c.12-.22.07-.49-.12-.64l-2.11-1.65zM12 15.5c-1.93 0-3.5-1.57-3.5-3.5s1.57-3.5 3.5-3.5 3.5 1.57 3.5 3.5-1.57 3.5-3.5 3.5z' },
    { href: '/background-tasks', label: 'Background Tasks', icon: 'M12 2a10 10 0 1 0 10 10h-2a8 8 0 1 1-8-8V2zm1 0v6h6a6 6 0 0 0-6-6z' },
    { href: '/logs', label: 'Logs', icon: 'M4 3h16a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zm2 4v2h12V7H6zm0 4v2h12v-2H6zm0 4v2h8v-2H6z' },
    { href: '/api-docs', label: 'API', icon: 'M9.4 16.6 4.8 12l4.6-4.6L8 6l-6 6 6 6 1.4-1.4zm5.2 0L19.2 12l-4.6-4.6L16 6l6 6-6 6-1.4-1.4z' },
    { href: '/style-guide', label: 'Style Guide', icon: 'M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10c1.38 0 2.5-1.12 2.5-2.5 0-.61-.23-1.18-.64-1.61-.4-.43-.61-.99-.61-1.59 0-1.38 1.12-2.5 2.5-2.5H17c2.76 0 5-2.24 5-5 0-4.96-4.49-9-10-9zm-5.5 9c-.83 0-1.5-.67-1.5-1.5S5.67 8 6.5 8 8 8.67 8 9.5 7.33 11 6.5 11zm3-4C8.67 7 8 6.33 8 5.5S8.67 4 9.5 4s1.5.67 1.5 1.5S10.33 7 9.5 7zm5 0c-.83 0-1.5-.67-1.5-1.5S13.67 4 14.5 4s1.5.67 1.5 1.5S15.33 7 14.5 7zm3 4c-.83 0-1.5-.67-1.5-1.5S16.67 8 17.5 8s1.5.67 1.5 1.5-.67 1.5-1.5 1.5z' },
    { href: '/purge', label: 'Purge Deleted', icon: 'M9 3v1H4v2h16V4h-5V3H9zm-3 5l1 13a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-13H6zm4 2h1v9h-1v-9zm3 0h1v9h-1v-9z' }
  ];

  // Most-used shortcuts — the full reference lives on the Config page.
  // Seek keys live in their own 3x2 numpad-style grid below.
  const shortcuts = [
    { keys: ['␣'],       label: 'Play / pause' },
    { keys: ['←', '→'],  label: 'Save & prev/next' },
    { keys: ['T'],       label: 'Edit Tags' },
    { keys: ['I'],       label: 'File Info' },
    { keys: ['W'],       label: "Won't Play + advance" },
    { keys: ['D'],       label: 'Delete + advance' },
    { keys: ['U'],       label: 'Undo W/D' },
    { keys: ['R'],       label: 'Toggle Needs Review' },
    { keys: ['F'],       label: 'Toggle Favorite' },
    { keys: ['K'],       label: 'Add bookmark' },
    // The bracket cluster shares the [/] keys; modifiers pick the layer.
    { keys: ['⇧+[', '⇧+]'],   label: 'Start / End block' },
    { keys: ['⌃⇧+[', '⌃⇧+]'], label: 'Start / End clip' },
    { keys: ['[', ']'],   label: 'Shrink / Enlarge video' },
    { keys: ['\\'],      label: 'Fit video to column' }
  ];

  const isActive = (href: string) => page.url.pathname === href || page.url.pathname.startsWith(`${href}/`);

  // Sidebar collapse — persisted to localStorage so the choice sticks across
  // reloads. Only affects the lg:drawer-open (always-on) sidebar; the mobile
  // drawer uses its own open/close toggle.
  let collapsed = $state(false);
  onMount(() => {
    collapsed = localStorage.getItem('sidebarCollapsed') === '1';
  });
  function toggleCollapsed() {
    collapsed = !collapsed;
    localStorage.setItem('sidebarCollapsed', collapsed ? '1' : '0');
  }
</script>

<div class="drawer lg:drawer-open min-h-screen">
  <input id="main-drawer" type="checkbox" class="drawer-toggle" />

  <div class="drawer-content flex flex-col">
    <!-- Top navbar (mobile hamburger + brand) -->
    <div class="navbar bg-base-200 lg:hidden">
      <div class="flex-none">
        <label for="main-drawer" aria-label="open sidebar" class="btn btn-square btn-ghost">
          <svg xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" class="inline-block h-6 w-6 stroke-current">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M4 6h16M4 12h16M4 18h16"></path>
          </svg>
        </label>
      </div>
      <div class="mx-2 flex-1 px-2 font-semibold">Video Organizer</div>
    </div>

    <!-- Page content -->
    <main class="flex-1 p-4 lg:p-6">
      {@render children()}
    </main>
  </div>

  <aside class="drawer-side">
    <label for="main-drawer" aria-label="close sidebar" class="drawer-overlay"></label>
    <div class="min-h-full bg-base-200 flex flex-col transition-[width] duration-150 {collapsed ? 'w-16' : 'w-64'}">
      <div class="px-3 py-4 border-b border-base-300 flex items-center justify-between gap-2">
        {#if !collapsed}
          <a href="/" class="text-lg font-semibold truncate">Video Organizer</a>
        {/if}
        <button
          type="button"
          class="btn btn-ghost btn-sm btn-square shrink-0"
          aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          onclick={toggleCollapsed}
        >
          <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" class="h-5 w-5 fill-current">
            {#if collapsed}
              <path d="M9 6l6 6-6 6V6z" />
            {:else}
              <path d="M15 6l-6 6 6 6V6z" />
            {/if}
          </svg>
        </button>
      </div>

      {#if !collapsed}
        <div class="px-4 py-2 text-xs uppercase tracking-wider text-base-content/60">Pages</div>
      {/if}
      <ul class="menu px-2">
        {#each nav as item (item.href)}
          <li>
            <a
              href={item.href}
              class={isActive(item.href) ? 'active' : ''}
              title={collapsed ? item.label : undefined}
            >
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" class="h-5 w-5 fill-current shrink-0">
                <path d={item.icon} />
              </svg>
              {#if !collapsed}{item.label}{/if}
            </a>
          </li>
        {/each}
      </ul>

      {#if !collapsed}
        <div class="px-4 pt-4 pb-2 text-xs uppercase tracking-wider text-base-content/60">
          Keyboard Shortcuts
        </div>
        <div class="px-4 pb-4 space-y-1.5 text-sm overflow-y-auto">
          {#each shortcuts as s (s.label)}
            <div class="flex items-center gap-2">
              <div class="flex gap-1 shrink-0">
                {#each s.keys as k (k)}
                  <kbd class="kbd kbd-sm">{k}</kbd>
                {/each}
              </div>
              <span class="text-base-content/80 truncate">{s.label}</span>
            </div>
          {/each}

          <div class="pt-3 text-xs uppercase tracking-wider text-base-content/60">Seek</div>
          <div class="grid grid-cols-2 gap-x-3 gap-y-1.5 text-sm">
            <div class="flex items-center gap-1.5">
              <kbd class="kbd kbd-sm">7</kbd>
              <span class="text-base-content/70 tabular-nums">({formatSeek(playbackSettings.key7Seconds, '-')})</span>
            </div>
            <div class="flex items-center gap-1.5">
              <kbd class="kbd kbd-sm">9</kbd>
              <span class="text-base-content/70 tabular-nums">({formatSeek(playbackSettings.key9Seconds, '+')})</span>
            </div>
            <div class="flex items-center gap-1.5">
              <kbd class="kbd kbd-sm">4</kbd>
              <span class="text-base-content/70 tabular-nums">({formatSeek(playbackSettings.key4Seconds, '-')})</span>
            </div>
            <div class="flex items-center gap-1.5">
              <kbd class="kbd kbd-sm">6</kbd>
              <span class="text-base-content/70 tabular-nums">({formatSeek(playbackSettings.key6Seconds, '+')})</span>
            </div>
            <div class="flex items-center gap-1.5">
              <kbd class="kbd kbd-sm">1</kbd>
              <span class="text-base-content/70 tabular-nums">({formatSeek(playbackSettings.key1Seconds, '-')})</span>
            </div>
            <div class="flex items-center gap-1.5">
              <kbd class="kbd kbd-sm">3</kbd>
              <span class="text-base-content/70 tabular-nums">({formatSeek(playbackSettings.key3Seconds, '+')})</span>
            </div>
          </div>

          <div class="pt-1 text-xs text-base-content/60 leading-snug space-y-1">
            <div>
              Numpad <kbd class="kbd kbd-xs">0</kbd> jumps to start;
              <kbd class="kbd kbd-xs">−</kbd> jumps to 10s from end.
            </div>
            <div>
              Numpad digits always seek. For the top row, hold
              <kbd class="kbd kbd-xs">⇧ Shift</kbd> + digit while typing in a tag field.
            </div>
          </div>
          <div class="pt-2 text-xs text-base-content/60">
            Full list on <a class="link" href="/config">Configuration</a>.
          </div>
        </div>

        {#if (md5Pending !== null && md5Pending > 0) || (thumbPending !== null && thumbPending > 0)}
          <div class="px-4 py-3 mt-auto border-t border-base-300 text-xs text-base-content/70 space-y-1.5">
            {#if thumbPending !== null && thumbPending > 0}
              <div class="flex items-center gap-2">
                <span class="loading loading-spinner loading-xs"></span>
                <span>Thumbnails: <span class="tabular-nums font-medium">{thumbPending}</span></span>
              </div>
            {/if}
            {#if md5Pending !== null && md5Pending > 0}
              <div class="flex items-center gap-2">
                <span class="loading loading-spinner loading-xs"></span>
                <span>MD5: <span class="tabular-nums font-medium">{md5Pending}</span></span>
              </div>
            {/if}
          </div>
        {/if}
      {/if}
    </div>
  </aside>
</div>

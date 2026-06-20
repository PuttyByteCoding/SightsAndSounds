<script lang="ts">
  import '../app.css';
  import { page } from '$app/state';
  import { onMount, onDestroy } from 'svelte';
  import { api } from '$lib/api';
  import { playbackSettings } from '$lib/playbackSettings.svelte';
  import { runtimeStore } from '$lib/runtimeStore.svelte';
  import SearchPalette from '$lib/components/SearchPalette.svelte';
  import TourOverlay from '$lib/components/TourOverlay.svelte';
  import ErrorBanner from '$lib/components/ErrorBanner.svelte';

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
    { href: '/hidden-tags', label: 'Hidden Tags', icon: 'M12 7a5 5 0 0 1 5 5c0 .65-.13 1.27-.36 1.84l2.92 2.92A11.8 11.8 0 0 0 23 12c-1.73-4.39-6-7.5-11-7.5-1.4 0-2.74.25-3.98.7l2.16 2.16C10.73 7.13 11.35 7 12 7zM2 4.27l2.28 2.28.46.46A11.8 11.8 0 0 0 1 12c1.73 4.39 6 7.5 11 7.5 1.55 0 3.03-.3 4.38-.84l.42.42L19.73 21 21 19.73 3.27 2 2 4.27zM7.53 9.8l1.55 1.55c-.05.21-.08.43-.08.65a3 3 0 0 0 3 3c.22 0 .44-.03.65-.08l1.55 1.55c-.67.33-1.41.53-2.2.53a5 5 0 0 1-5-5c0-.79.2-1.53.53-2.2z' },
    { href: '/import', label: 'Import Tool', icon: 'M12 2a1 1 0 0 1 1 1v8h8a1 1 0 1 1 0 2h-8v8a1 1 0 1 1-2 0v-8H3a1 1 0 1 1 0-2h8V3a1 1 0 0 1 1-1Z' },
    { href: '/sources', label: 'Sources', icon: 'M10 4H4a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-8l-2-2z' },
    { href: '/keyboard-shortcuts', label: 'Keyboard Shortcuts', icon: 'M4 5h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2zm2 3v2h2V8H6zm4 0v2h2V8h-2zm4 0v2h2V8h-2zm4 0v2h2V8h-2zM6 12v2h2v-2H6zm4 0v2h8v-2h-8zm10 0v2h2v-2h-2zM8 16v2h8v-2H8z' },
    // In-app reference for what each section does + key workflows (issue #169).
    { href: '/help', label: 'Help & Features', icon: 'M12 2a10 10 0 100 20 10 10 0 000-20zm0 18a8 8 0 110-16 8 8 0 010 16zm-1-5h2v2h-2v-2zm2.07-7.75c-.9.92-1.07 1.32-1.07 2.25h-2c0-1.13.34-1.87 1.29-2.83.5-.5.71-.95.71-1.42a1.5 1.5 0 00-3 0H7a3.5 3.5 0 117 0c0 .77-.31 1.47-.93 2z' },
    { href: '/backups', label: 'Backups', icon: 'M12 2C6.48 2 2 4.02 2 6.5v11C2 19.98 6.48 22 12 22s10-2.02 10-4.5v-11C22 4.02 17.52 2 12 2zm0 2c4.42 0 8 1.57 8 2.5S16.42 11 12 11 4 9.43 4 8.5 7.58 4 12 4zm8 13.5c0 .93-3.58 2.5-8 2.5s-8-1.57-8-2.5v-2.93C5.78 15.67 8.7 16.5 12 16.5s6.22-.83 8-1.93v2.93z' },
    { href: '/background-tasks', label: 'Background Tasks', icon: 'M12 2a10 10 0 1 0 10 10h-2a8 8 0 1 1-8-8V2zm1 0v6h6a6 6 0 0 0-6-6z' },
    { href: '/logs', label: 'Logs', icon: 'M4 3h16a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zm2 4v2h12V7H6zm0 4v2h12v-2H6zm0 4v2h8v-2H6z' },
    { href: '/api-docs', label: 'API', icon: 'M9.4 16.6 4.8 12l4.6-4.6L8 6l-6 6 6 6 1.4-1.4zm5.2 0L19.2 12l-4.6-4.6L16 6l6 6-6 6-1.4-1.4z' },
    { href: '/style-guide', label: 'Style Guide', icon: 'M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10c1.38 0 2.5-1.12 2.5-2.5 0-.61-.23-1.18-.64-1.61-.4-.43-.61-.99-.61-1.59 0-1.38 1.12-2.5 2.5-2.5H17c2.76 0 5-2.24 5-5 0-4.96-4.49-9-10-9zm-5.5 9c-.83 0-1.5-.67-1.5-1.5S5.67 8 6.5 8 8 8.67 8 9.5 7.33 11 6.5 11zm3-4C8.67 7 8 6.33 8 5.5S8.67 4 9.5 4s1.5.67 1.5 1.5S10.33 7 9.5 7zm5 0c-.83 0-1.5-.67-1.5-1.5S13.67 4 14.5 4s1.5.67 1.5 1.5S15.33 7 14.5 7zm3 4c-.83 0-1.5-.67-1.5-1.5S16.67 8 17.5 8s1.5.67 1.5 1.5-.67 1.5-1.5 1.5z' },
    { href: '/playback-issues', label: 'Playback Issues', icon: 'M12 2a10 10 0 100 20 10 10 0 000-20zm0 2a8 8 0 016.32 12.9L7.1 5.68A8 8 0 0112 4zM5.68 7.1l11.22 11.22A8 8 0 015.68 7.1z' },
    // Review queue for user-flagged duplicate pairs (browse page's
    // 🎯 Find duplicates hunt). Lives in the "library health" cluster
    // with Playback Issues / Purge / Data Validation.
    { href: '/duplicates', label: 'Duplicates', icon: 'M16 1H4a2 2 0 0 0-2 2v14h2V3h12V1zm3 4H8a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h11a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2zm0 16H8V7h11v14z' },
    { href: '/purge', label: 'Purge Deleted', icon: 'M9 3v1H4v2h16V4h-5V3H9zm-3 5l1 13a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-13H6zm4 2h1v9h-1v-9zm3 0h1v9h-1v-9z' },
    // Export defined clips to standalone files (issue #69). Library tool,
    // grouped with the other clip/duplicate/purge "library shaping" pages.
    { href: '/clips-export', label: 'Export Clips', icon: 'M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8l-6-6zm2 16H8v-2h8v2zm0-4H8v-2h8v2zm-3-5V3.5L18.5 9H13z' },
    // Build trimmed files with the hidden sections cut out (issue #70).
    { href: '/remove-blocked', label: 'Remove Blocked', icon: 'M19 13H5v-2h14v2z' },
    // Concatenate several videos into one new file (issue #163).
    { href: '/join', label: 'Join Videos', icon: 'M2 7h6v4H2V7zm14 0h6v4h-6V7zM9 8h6v2H9V8z' },
    // Re-encode videos to a configurable ffmpeg/HandBrake profile (issue #164).
    { href: '/encode', label: 'Encode / Convert', icon: 'M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zm6 4v8l6-4-6-4z' },
    // Faststart remux so large MP4s start playing instantly (issue #166).
    { href: '/optimize', label: 'Optimize for Streaming', icon: 'M13 2 3 14h7v8l10-12h-7V2z' },
    // Log of file moves (browse page's ↪ Move file) with one-click Undo.
    { href: '/moves', label: 'File Moves', icon: 'M3 6h7l2 2h9v10a2 2 0 0 1-2 2H3a2 2 0 0 1-2-2V6zm10.5 4-1.41 1.41L13.67 13H8v2h5.67l-1.58 1.59L13.5 18l4-4-4-4z' },
    // Diagnostic tooling — surfaces drift between the DB and the
    // filesystem (missing files, un-imported leftovers, unreachable
    // sources). Sits next to Purge / Playback Issues since all three
    // are "library health" pages.
    { href: '/data-validation', label: 'Data Validation', icon: 'M9 16.17 4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41L9 16.17z' }
  ];

  // Most-used shortcuts — the full reference lives on the Config page.
  // Seek keys live in their own 3x2 numpad-style grid below.
  const shortcuts = [
    { keys: ['⌃K'],      label: 'Search everything' },
    { keys: ['␣'],       label: 'Play / pause' },
    { keys: ['←', '→'],  label: 'Save & prev/next' },
    { keys: ['T'],       label: 'Edit Tags' },
    { keys: ['I'],       label: 'File Info' },
    { keys: ['W'],       label: "Playback Issue + advance" },
    { keys: ['D'],       label: 'Delete + advance' },
    { keys: ['U'],       label: 'Undo' },
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
    // Boot the runtime-info fetch — drives the host-machine banner
    // below + gates the local-only diagnostic buttons elsewhere.
    void runtimeStore.load();
  });
  function toggleCollapsed() {
    collapsed = !collapsed;
    localStorage.setItem('sidebarCollapsed', collapsed ? '1' : '0');
  }

  // --- Global Ctrl+K / Cmd+K command palette -----------------------------
  // Mounted once at the layout level so it's available from every
  // route. Hotkey works anywhere on the page — including while
  // typing in inputs/textareas — because every other interactive
  // form on the site reserves their text-only Ctrl key combos for
  // browser defaults (Ctrl+A, Ctrl+C, etc.). Ctrl+K isn't taken.
  //
  // The palette itself is bind:open={searchOpen} so it can flip
  // itself closed on Esc / result-pick.
  let searchOpen = $state(false);
  function onGlobalKeyDown(e: KeyboardEvent) {
    // Ctrl+K on Win/Linux, Cmd+K on macOS. Ignore when a modifier
    // combo we don't expect is also pressed so users with Karabiner
    // / AutoHotKey remappings don't get a surprise palette.
    const isToggle =
      (e.key === 'k' || e.key === 'K') &&
      (e.ctrlKey || e.metaKey) &&
      !e.altKey && !e.shiftKey;
    if (!isToggle) return;
    e.preventDefault();
    searchOpen = !searchOpen;
  }
</script>

<!-- Global Ctrl+K / Cmd+K listener — single window-level handler so
     the palette opens regardless of where focus currently is. -->
<svelte:window onkeydown={onGlobalKeyDown} />

<!-- Global search palette. Mounted once at the layout level so every
     route gets it. bind:open lets the palette flip itself closed on
     Esc / result pick. -->
<SearchPalette bind:open={searchOpen} />

<!-- Guided tour overlay (issue #170). Mounted once here so it can spotlight
     elements on any route, including this nav. Inert until tour.start(). -->
<TourOverlay />

<!-- Global 4xx/5xx error banner (issue #201). Mounted once so any failed API
     call surfaces a brief, auto-fading banner on whatever page is showing. -->
<ErrorBanner />

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

    <!-- Page content. The "must be on host machine" warning banner
         used to live here, but it's only relevant on pages that
         actually expose local-only buttons (reveal in folder,
         ffprobe). Each such page renders <RemoteHostBanner /> at
         its top instead, so the warning is contextual rather than
         globally nagging. -->
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
      <ul class="menu px-2" data-tour="tools-nav">
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
            Full list on <a class="link" href="/keyboard-shortcuts">Keyboard Shortcuts</a>.
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

<script lang="ts">
  // Recursive node for the Folders section of the browse-page filter
  // sidebar. Each node renders one directory as `chevron + label`. The
  // label is a button that adds a folder filter to the filterStore;
  // the chevron lazy-loads immediate children via /api/import/browse
  // on first expand, then re-uses the cached list on subsequent
  // toggles. Children render as <svelte:self> so the tree reflects
  // arbitrary depth without needing a separate import.
  //
  // Filter pills only carry the folder's basename — full paths
  // overflowed the chip row. Path disambiguation lives in the row's
  // `title` attribute (full absolute path on hover) and the filter
  // value itself (which is the absolute path, so two same-named
  // folders from different sources resolve to distinct filters).
  import { api } from '$lib/api';
  import type { ImportBrowseDirectory } from '$lib/types';
  import Self from './FolderTreeNode.svelte';

  interface Props {
    name: string;
    fullPath: string;
    // hasSubdirectories=true forces the chevron to render. We default
    // VideoSet roots to true so the user always sees the affordance;
    // an empty source folder shows "No subfolders" on first expand
    // — fine, since this UI is meant for organized libraries.
    hasSubdirectories: boolean;
    depth: number;
    // Recursive counts from /api/import/browse. importedCount (how many are
    // already in the DB) is returned eagerly; videoCount (total video files
    // under this directory) is NULL because browse no longer walks the disk —
    // each node lazy-fetches its own via /import/folder-count (issue #197).
    // null / 0 suppresses the count badge until it loads.
    videoCount?: number | null;
    importedCount: number;
    onPickFolder: (fullPath: string, label: string) => void;
    // When provided, a hover trash button removes this folder's imported
    // videos from the library (issue #53). Bubbles up to the browse page,
    // which confirms + calls the API. Threaded down recursively.
    onRemoveFolder?: (fullPath: string, label: string, importedCount: number) => void;
    // Only meaningful at depth 0 (source-root rows). When false, the
    // row renders the source name with a strikethrough and a faded
    // "(Disabled)" suffix so it's clear the source is browse-only —
    // its videos won't appear in the player grid until re-enabled.
    // Children inherit the visual treatment via depth — sub-folders
    // of a disabled source still expand, but the user already sees
    // the disabled state at the root.
    enabled?: boolean;
  }

  let {
    name,
    fullPath,
    hasSubdirectories,
    depth,
    videoCount,
    importedCount,
    onPickFolder,
    onRemoveFolder,
    enabled = true
  }: Props = $props();

  let expanded = $state(false);
  let loading = $state(false);
  let error = $state<string | null>(null);
  let children = $state<ImportBrowseDirectory[] | null>(null);

  // Lazy recursive video count (issue #197). browse returns videoCount=null;
  // fetch this folder's count once in the background and show it when it lands.
  let fetchedCount = $state<number | null>(null);
  const count = $derived(videoCount ?? fetchedCount);
  $effect(() => {
    if (videoCount == null && fetchedCount == null) {
      api.getImportFolderCount(fullPath)
        .then(r => { fetchedCount = r.videoCount; })
        .catch(() => { /* leave unset — badge just stays hidden */ });
    }
  });

  // Hide subfolders that contain no imported videos (recursive).
  // importedCount is itself recursive on the server, so dropping a
  // 0-import folder is safe — nothing under it can have imports
  // either. Filter at render time, not at fetch time, so a future
  // toggle (if we ever add one) could flip back without a refetch.
  const visibleChildren = $derived(
    children?.filter(c => c.importedCount > 0) ?? null
  );

  async function toggle() {
    if (!hasSubdirectories) return;
    if (expanded) {
      expanded = false;
      return;
    }
    expanded = true;
    if (children === null && !loading) {
      loading = true;
      error = null;
      try {
        const resp = await api.browseImport(fullPath);
        children = resp.directories;
      } catch (e: any) {
        error = e?.message ?? 'Failed to load subfolders';
      } finally {
        loading = false;
      }
    }
  }

  function pick() {
    // Filter pills get just the folder's basename — the full
    // "Source / a / b / Folder" path bloats chips and pushes other
    // pills off-screen. The hover title on the row label still shows
    // the absolute path for disambiguation, and the folder filter
    // matches by exact path so two same-named folders won't collide.
    onPickFolder(fullPath, name);
  }

  // 0.75rem (~12px) per level. Keeps deep trees readable without
  // running the labels off the right edge of the 280px sidebar.
  const indentRem = $derived(depth * 0.75);
</script>

<div class="text-sm">
  <div
    class="group flex items-center gap-1 hover:bg-base-200 rounded"
    style="padding-left: {indentRem}rem"
  >
    {#if hasSubdirectories}
      <button
        type="button"
        class="shrink-0 w-5 h-5 flex items-center justify-center text-base-content/70 hover:text-base-content"
        aria-label={expanded ? 'Collapse subfolders' : 'Expand subfolders'}
        title={expanded ? 'Collapse' : 'Expand'}
        onclick={toggle}
      >
        <svg
          xmlns="http://www.w3.org/2000/svg"
          viewBox="0 0 24 24"
          class="h-3 w-3 fill-current transition-transform {expanded ? 'rotate-90' : ''}"
        >
          <path d="M9 6l6 6-6 6V6z" />
        </svg>
      </button>
    {:else}
      <span class="shrink-0 w-5 h-5" aria-hidden="true"></span>
    {/if}
    <button
      type="button"
      class="flex-1 min-w-0 text-left truncate py-1 hover:underline"
      onclick={pick}
      title={enabled ? fullPath : `${fullPath} — disabled (videos hidden from the grid)`}
    >
      <span class={enabled ? '' : 'line-through text-base-content/60'}>{name}</span>
      {#if !enabled}
        <span class="text-xs italic text-base-content/50 ml-1 no-underline">(Disabled)</span>
      {/if}
    </button>
    <!-- Import-status badge. Two visual states share one slot:
         · fully imported (importedCount ≥ videoCount > 0) → show the
           total as a muted "X" so the user can see the size at a
           glance without it competing for attention.
         · partial (importedCount < videoCount)            → show
           "X/Y" tinted text-warning so unimported folders pop out
           of an otherwise greyed-out tree.
         videoCount === 0 → no badge (nothing to count). -->
    {#if count != null && count > 0}
      {#if importedCount >= count}
        <span
          class="shrink-0 text-xs tabular-nums opacity-50"
          title="All {count} video{count === 1 ? '' : 's'} imported"
        >{count}</span>
      {:else}
        <span
          class="shrink-0 text-xs tabular-nums text-warning"
          title="{importedCount} of {count} videos imported"
        >{importedCount}/{count}</span>
      {/if}
    {/if}
    {#if onRemoveFolder && importedCount > 0}
      <!-- Remove this folder's imported videos from the library (issue #53).
           Hover-revealed so it doesn't clutter the tree; stopPropagation so it
           doesn't also trigger the row's filter-pick. -->
      <button
        type="button"
        class="shrink-0 w-5 h-5 flex items-center justify-center rounded text-base-content/40 opacity-0 group-hover:opacity-100 focus:opacity-100 hover:text-error"
        title="Remove this folder's videos from the library (files on disk are kept)"
        aria-label="Remove {name} from library"
        onclick={(e) => { e.stopPropagation(); onRemoveFolder?.(fullPath, name, importedCount); }}
      >
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" class="h-3.5 w-3.5 fill-current">
          <path d="M9 3a1 1 0 00-1 1v1H5a1 1 0 100 2h14a1 1 0 100-2h-3V4a1 1 0 00-1-1H9zm-2 6v9a2 2 0 002 2h6a2 2 0 002-2V9H7z" />
        </svg>
      </button>
    {/if}
  </div>

  {#if expanded}
    {#if loading}
      <div
        class="text-xs text-base-content/50 italic py-1"
        style="padding-left: {indentRem + 1.25}rem"
      >Loading…</div>
    {:else if error}
      <div
        class="text-xs text-error py-1"
        style="padding-left: {indentRem + 1.25}rem"
      >{error}</div>
    {:else if visibleChildren && visibleChildren.length === 0}
      <div
        class="text-xs text-base-content/50 italic py-1"
        style="padding-left: {indentRem + 1.25}rem"
      >No subfolders with imported videos</div>
    {:else if visibleChildren}
      {#each visibleChildren as c (c.fullPath)}
        <Self
          name={c.name}
          fullPath={c.fullPath}
          hasSubdirectories={c.hasSubdirectories}
          depth={depth + 1}
          videoCount={c.videoCount}
          importedCount={c.importedCount}
          {onPickFolder}
          {onRemoveFolder}
        />
      {/each}
    {/if}
  {/if}
</div>

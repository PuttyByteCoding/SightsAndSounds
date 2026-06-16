<script lang="ts">
  // Read-only side panel summarizing a video's technical metadata —
  // codec, bitrate, dimensions, stream counts, MD5, etc. Mirrors the
  // shape of EditTagsPanel: bound to the host's `show` flag, the host
  // wraps it in a fixed-width sticky column. T toggles tags, I toggles
  // file-info; either or both can be open at once.
  import type { Video } from '$lib/types';
  import {
    loadColumnWidths,
    saveColumnWidths,
    resizable,
  } from '$lib/tableUtils.svelte';

  interface Props {
    show: boolean;
    video: Video | null;
    // When provided, the File Name row gets an inline rename control (#172).
    // The host performs the rename and refreshes the video.
    onRename?: (newName: string) => Promise<void>;
  }

  let { show = $bindable(), video, onRename }: Props = $props();

  // Inline rename state (#172).
  let renaming = $state(false);
  let renameValue = $state('');
  let renameBusy = $state(false);
  let renameError = $state<string | null>(null);

  function startRename() {
    if (!video) return;
    // Pre-fill with the base name (no extension) — the server keeps the ext.
    const name = video.fileName;
    const dot = name.lastIndexOf('.');
    renameValue = dot > 0 ? name.slice(0, dot) : name;
    renameError = null;
    renaming = true;
  }
  function cancelRename() {
    renaming = false;
    renameError = null;
  }
  async function commitRename() {
    if (!onRename || renameBusy || renameValue.trim().length === 0) return;
    renameBusy = true;
    renameError = null;
    try {
      await onRename(renameValue.trim());
      renaming = false;
    } catch (e) {
      renameError = e instanceof Error ? e.message : String(e);
    } finally {
      renameBusy = false;
    }
  }

  // Persisted widths for the key/value table. The Field column was
  // previously fixed at `w-40` (160px); Value flowed from content.
  const WIDTHS_KEY = 'fileInfoPanel.kv';
  let widths = $state<Record<string, number>>(loadColumnWidths(WIDTHS_KEY, {
    field: 160,
    value: 280,
  }));
  function setWidth(col: string, w: number) {
    widths = { ...widths, [col]: w };
    saveColumnWidths(WIDTHS_KEY, widths);
  }
  function getWidth(col: string, fallback: number): number {
    return widths[col] ?? fallback;
  }
  // Explicit table width (see DataTableModal note re: max-content).
  const totalWidth = $derived(getWidth('field', 160) + getWidth('value', 280));

  function formatBytes(bytes: number): string {
    if (!bytes || bytes <= 0) return '—';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0;
    let n = bytes;
    while (n >= 1024 && i < units.length - 1) {
      n /= 1024;
      i++;
    }
    return `${n.toFixed(i === 0 ? 0 : 2)} ${units[i]}`;
  }

  function formatBitrate(bps: number): string {
    if (!bps || bps <= 0) return '—';
    if (bps >= 1_000_000) return `${(bps / 1_000_000).toFixed(2)} Mbps`;
    if (bps >= 1_000) return `${(bps / 1_000).toFixed(0)} Kbps`;
    return `${bps} bps`;
  }

  function formatDateTime(iso: string | null): string {
    if (!iso) return '—';
    const d = new Date(iso);
    return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
  }

  function orDash(v: string | number | null | undefined): string {
    if (v === null || v === undefined || v === '') return '—';
    return String(v);
  }

  function close() { show = false; }
</script>

{#if show && video}
  <div class="p-4 space-y-3">
    <!-- Header mirrors EditTagsPanel: title left, Close right. The
         host's I keybinding also toggles `show`, so the user has two
         affordances for closing. -->
    <header class="flex items-center justify-between">
      <h2 class="text-lg font-semibold">File Info</h2>
      <button class="btn btn-sm" onclick={close}>Close</button>
    </header>

    <div class="text-xs text-base-content/60 break-all">{video.filePath}</div>

    <table class="table table-sm resizable-table" style="table-layout: fixed; width: {totalWidth}px;">
      <colgroup>
        <col style="width: {getWidth('field', 160)}px" />
        <col style="width: {getWidth('value', 280)}px" />
      </colgroup>
      <thead>
        <tr>
          {#each [
            { key: 'field', label: 'Field', def: 160 },
            { key: 'value', label: 'Value', def: 280 },
          ] as col (col.key)}
            <th
              class="relative select-none p-0 text-left bg-base-200"
              style="width: {getWidth(col.key, col.def)}px;"
            >
              <span class="block px-3 py-2 truncate text-xs uppercase tracking-wide text-base-content/70">{col.label}</span>
              <button
                type="button"
                aria-label={`Resize ${col.label} (double-click to auto-fit)`}
                class="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/40 active:bg-primary/60 z-10"
                use:resizable={{
                  getWidth: () => getWidth(col.key, 100),
                  setWidth: (w) => setWidth(col.key, w),
                }}
              ></button>
            </th>
          {/each}
        </tr>
      </thead>
      <tbody>
        <tr>
          <td class="font-medium">File Name</td>
          <td class="break-all">
            {#if renaming}
              <div class="flex flex-col gap-1">
                <input
                  class="input input-bordered input-xs w-full"
                  bind:value={renameValue}
                  disabled={renameBusy}
                  onkeydown={(e) => { if (e.key === 'Enter') commitRename(); else if (e.key === 'Escape') cancelRename(); }}
                />
                <div class="flex gap-1">
                  <button class="btn btn-xs btn-primary" onclick={commitRename} disabled={renameBusy}>
                    {#if renameBusy}<span class="loading loading-spinner loading-xs"></span>{/if}Save
                  </button>
                  <button class="btn btn-xs btn-ghost" onclick={cancelRename} disabled={renameBusy}>Cancel</button>
                </div>
                {#if renameError}<div class="text-xs text-error">{renameError}</div>{/if}
              </div>
            {:else}
              <span>{video.fileName}</span>
              {#if onRename}
                <button class="btn btn-ghost btn-xs ml-1" title="Rename file" onclick={startRename}>✎</button>
              {/if}
            {/if}
          </td>
        </tr>
        <tr><td class="font-medium">File Size</td><td>{formatBytes(video.fileSize)}</td></tr>
        <tr><td class="font-medium">MD5</td><td class="font-mono text-xs break-all">{orDash(video.md5)}</td></tr>
        <tr><td class="font-medium">Duration</td><td>{orDash(video.duration)}</td></tr>
        <tr><td class="font-medium">Dimensions</td><td>{video.width} × {video.height}</td></tr>
        <tr><td class="font-medium">Format</td><td>{orDash(video.videoDimensionFormat)}</td></tr>
        <tr><td class="font-medium">Codec</td><td>{orDash(video.videoCodec)}</td></tr>
        <tr><td class="font-medium">Bitrate</td><td>{formatBitrate(video.bitrate)}</td></tr>
        <tr><td class="font-medium">Frame Rate</td><td>{video.frameRate ? `${video.frameRate.toFixed(2)} fps` : '—'}</td></tr>
        <tr><td class="font-medium">Pixel Format</td><td>{orDash(video.pixelFormat)}</td></tr>
        <tr><td class="font-medium">Aspect Ratio</td><td>{orDash(video.ratio)}</td></tr>
        <tr><td class="font-medium">Creation Time</td><td>{formatDateTime(video.creationTime)}</td></tr>
        <tr><td class="font-medium">Video Streams</td><td>{video.videoStreamCount}</td></tr>
        <tr><td class="font-medium">Audio Streams</td><td>{video.audioStreamCount}</td></tr>
        <tr><td class="font-medium">Ingested</td><td>{formatDateTime(video.ingestDate)}</td></tr>
      </tbody>
    </table>
  </div>
{/if}

<script lang="ts">
  // Read-only side panel summarizing a video's technical metadata —
  // codec, bitrate, dimensions, stream counts, MD5, etc. Mirrors the
  // shape of EditTagsPanel: bound to the host's `show` flag, the host
  // wraps it in a fixed-width sticky column. T toggles tags, I toggles
  // file-info; either or both can be open at once.
  import type { Video } from '$lib/types';

  interface Props {
    show: boolean;
    video: Video | null;
  }

  let { show = $bindable(), video }: Props = $props();

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

    <table class="table table-sm">
      <tbody>
        <tr><td class="font-medium w-40">File Name</td><td class="break-all">{video.fileName}</td></tr>
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

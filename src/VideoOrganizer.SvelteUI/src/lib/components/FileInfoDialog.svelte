<script lang="ts">
  // Read-only modal summarizing a video's technical metadata — codec, bitrate,
  // dimensions, stream counts, MD5, etc. Bound to the host's `show` flag so
  // the same keyboard shortcut (`I` on the player) can toggle it.
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
  <!-- svelte-ignore a11y_click_events_have_key_events a11y_no_static_element_interactions -->
  <div class="modal modal-open" onclick={close} role="presentation">
    <!-- svelte-ignore a11y_click_events_have_key_events a11y_no_static_element_interactions -->
    <div class="modal-box max-w-2xl" onclick={(e) => e.stopPropagation()} role="presentation">
      <div class="flex items-center justify-between mb-3">
        <h3 class="font-bold text-lg">File Info</h3>
        <button class="btn btn-sm btn-ghost" type="button" onclick={close}>×</button>
      </div>
      <div class="text-xs text-base-content/60 break-all mb-3">{video.filePath}</div>
      <table class="table table-sm">
        <tbody>
          <tr><td class="font-medium w-48">File Name</td><td class="break-all">{video.fileName}</td></tr>
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
      <div class="modal-action">
        <button type="button" class="btn btn-sm btn-cancel" onclick={close}>Close</button>
      </div>
    </div>
  </div>
{/if}

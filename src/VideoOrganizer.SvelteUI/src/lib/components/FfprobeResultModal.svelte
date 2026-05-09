<script lang="ts">
  // Shared diagnostic-output modal used both inside the VideoPlayer
  // (when the user clicks "Diagnose" on the Playback Issue overlay)
  // and on the /playback-issues triage page (per-row "Diagnose"
  // button). Pretty-prints ffprobe's `-of json` stdout, surfaces
  // exit-code + stderr separately, and offers a line filter so the
  // user can focus on a specific section (codec, bitrate, error,
  // pixel format, …) without scrolling the whole stream tree.
  //
  // Bind `result` from the parent: setting it to a non-null
  // FfprobeResult opens the modal; the modal sets it back to null
  // on Close / backdrop click. Filter state lives here and persists
  // while the modal stays open — useful when checking the same key
  // ("codec_name", "bitrate") across several diagnoses in a row.
  import type { FfprobeResult } from '$lib/types';

  interface Props {
    result: FfprobeResult | null;
  }
  let { result = $bindable() }: Props = $props();

  let filter = $state('');

  // Pretty-print the JSON payload. ffprobe writes a JSON document to
  // stdout when run with `-of json`; if for some reason the output
  // isn't parseable JSON (e.g. an early ffprobe error printed to
  // stdout instead of stderr) we fall back to the raw string so the
  // user still sees something useful.
  const pretty = $derived.by(() => {
    if (!result) return '';
    try {
      return JSON.stringify(JSON.parse(result.stdout), null, 2);
    } catch {
      return result.stdout;
    }
  });

  // Total line count cached separately so the filter-result counter
  // can show "N / Total" without re-splitting twice per render.
  const totalLines = $derived(pretty ? pretty.split('\n').length : 0);

  // Filtered lines. Empty filter = show everything. Match is
  // case-insensitive substring against the whole line; the resulting
  // <pre> block shows the entire matching line (not just the matched
  // substring) so the user keeps the JSON's structural punctuation /
  // key context.
  const filteredLines = $derived.by(() => {
    if (!result) return [] as string[];
    const lines = pretty.split('\n');
    const f = filter.trim().toLowerCase();
    if (!f) return lines;
    return lines.filter(line => line.toLowerCase().includes(f));
  });

  function close() {
    result = null;
    // Don't reset `filter` — if the user opens a second diagnosis
    // they often want to apply the same filter (e.g. "codec_name"
    // across several files). Cleared explicitly via the Clear button.
  }

  // Escape closes the modal. Bound at the window level so the key
  // works whether the user is focused inside the filter input, on
  // the Close button, or anywhere else on the page. Guarded on
  // `result` so we only intercept Escape when we're actually open
  // — no swallowing the key when the modal is dismissed.
  function onWindowKeyDown(e: KeyboardEvent) {
    if (e.key === 'Escape' && result !== null) {
      e.preventDefault();
      close();
    }
  }
</script>

<svelte:window onkeydown={onWindowKeyDown} />

{#if result !== null}
  <div class="modal modal-open" role="dialog" aria-modal="true" aria-labelledby="ffprobe-title">
    <div class="modal-box max-w-3xl">
      <div class="flex items-baseline justify-between gap-3">
        <h3 id="ffprobe-title" class="font-bold text-lg">ffprobe diagnostics</h3>
        <span
          class="badge badge-sm {result.exitCode === 0 ? 'badge-success' : 'badge-error'}"
          title="ffprobe process exit code"
        >exit {result.exitCode}</span>
      </div>
      <div class="text-xs text-base-content/60 break-all mt-1" title={result.filePath}>
        {result.filePath}
      </div>

      {#if result.stderr}
        <!-- stderr is shown unconditionally when present so container-
             level errors / warnings (the actual "why won't this play"
             signal in many cases) don't get hidden by the line filter
             below, which only applies to the JSON payload. -->
        <div class="alert alert-warning text-xs mt-3">
          <div>
            <div class="font-semibold mb-1">stderr</div>
            <pre class="whitespace-pre-wrap break-all">{result.stderr}</pre>
          </div>
        </div>
      {/if}

      <!-- Filter input — case-insensitive substring match against
           each line of the pretty-printed JSON. Counter on the right
           shows match count vs total so the user can see at a glance
           whether their filter narrowed anything down. -->
      <div class="mt-3 flex items-center gap-2">
        <input
          type="text"
          class="input input-sm input-bordered flex-1"
          placeholder="Filter lines (e.g. codec, bitrate, pix_fmt)…"
          bind:value={filter}
          autocomplete="off"
        />
        {#if filter.trim()}
          <span class="text-xs text-base-content/60 tabular-nums shrink-0">
            {filteredLines.length} / {totalLines}
          </span>
          <button
            type="button"
            class="btn btn-xs btn-ghost"
            onclick={() => (filter = '')}
            title="Clear filter"
          >Clear</button>
        {/if}
      </div>

      <!-- Output. break-all keeps very long values (paths, encoder
           strings) from forcing a horizontal scrollbar; whitespace-
           pre-wrap preserves the indentation of the pretty-printed
           JSON for the un-filtered view. -->
      {#if filteredLines.length === 0 && filter.trim()}
        <div class="bg-base-200 rounded p-3 mt-2 text-xs text-base-content/60 italic">
          No lines match the filter.
        </div>
      {:else}
        <pre
          class="bg-base-200 rounded p-3 mt-2 text-xs overflow-auto max-h-[60vh] whitespace-pre-wrap break-all"
        >{filteredLines.join('\n')}</pre>
      {/if}

      <div class="modal-action">
        <button type="button" class="btn btn-sm" onclick={close}>Close</button>
      </div>
    </div>
    <button
      type="button"
      class="modal-backdrop"
      aria-label="Close diagnostics"
      onclick={close}
    ></button>
  </div>
{/if}

<script lang="ts">
  // Keyboard Shortcuts page (issue #67 — split out of the old Configuration
  // page). Configurable seek times + the full player shortcut reference.
  import {
    playbackSettings,
    savePlaybackSettings,
    resetPlaybackSettings,
    defaultSettings
  } from '$lib/playbackSettings.svelte';

  let status = $state<string | null>(null);
  let statusTimer: ReturnType<typeof setTimeout> | null = null;

  const rows = [
    { key: 'key1Seconds', label: 'Key 1 (backward)' },
    { key: 'key3Seconds', label: 'Key 3 (forward)' },
    { key: 'key4Seconds', label: 'Key 4 (backward)' },
    { key: 'key6Seconds', label: 'Key 6 (forward)' },
    { key: 'key7Seconds', label: 'Key 7 (backward)' },
    { key: 'key9Seconds', label: 'Key 9 (forward)' }
  ] as const;

  function flash(message: string) {
    status = message;
    if (statusTimer) clearTimeout(statusTimer);
    statusTimer = setTimeout(() => (status = null), 2500);
  }

  function normalize() {
    for (const { key } of rows) {
      const v = playbackSettings[key];
      playbackSettings[key] = Math.max(0, Number.isFinite(v) ? Math.floor(v) : 0);
    }
  }

  function handleSubmit(event: SubmitEvent) {
    event.preventDefault();
    normalize();
    savePlaybackSettings({ ...playbackSettings });
    flash('Saved');
  }

  function handleReset() {
    resetPlaybackSettings();
    Object.assign(playbackSettings, defaultSettings());
    flash('Reset to defaults');
  }
</script>

<div class="max-w-4xl p-6 space-y-6">
  <header>
    <h1 class="text-2xl font-semibold">Keyboard Shortcuts</h1>
    <p class="text-sm text-base-content/70 mt-1">
      All shortcuts are active on the Video Player page. They're ignored while you're
      typing in any input, textarea, or select — so entering text into tag fields
      never triggers a skip or a file move.
    </p>
  </header>

  <!-- Configurable seek times -->
  <section>
    <h2 class="text-lg font-medium mb-3">Seek (configurable)</h2>
    <form onsubmit={handleSubmit} class="space-y-4 max-w-xl">
      {#each rows as row (row.key)}
        <div class="form-control">
          <label class="label" for={row.key}>
            <span class="label-text">{row.label}</span>
          </label>
          <div class="join">
            <input
              id={row.key}
              type="number"
              min="0"
              class="input input-bordered join-item w-32"
              bind:value={playbackSettings[row.key]}
            />
            <span class="join-item px-3 flex items-center bg-base-200 text-base-content/70 text-sm rounded-r">
              seconds
            </span>
          </div>
        </div>
      {/each}

      <div class="flex items-center gap-3 pt-2">
        <button type="submit" class="btn btn-soft btn-primary btn-cta">Save</button>
        <button type="button" class="btn btn-cancel" onclick={handleReset}>Reset Defaults</button>
        {#if status}<span class="text-success text-sm">{status}</span>{/if}
      </div>
    </form>
  </section>

  <!-- Fixed shortcuts -->
  <section>
    <h2 class="text-lg font-medium mb-3">Playlist &amp; actions</h2>
    <div class="overflow-x-auto max-w-xl">
      <table class="table table-sm">
        <thead>
          <tr>
            <th class="w-24">Key</th>
            <th>Action</th>
          </tr>
        </thead>
        <tbody>
          <tr>
            <td><kbd class="kbd kbd-sm">←</kbd></td>
            <td>
              Save metadata (if changed) and go back to the previous video.
              Works even when a tag input has focus.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">→</kbd></td>
            <td>
              Save metadata (if changed) and advance to the next video. Works
              even when a tag input has focus — no need to Escape out first.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">Space</kbd> / <kbd class="kbd kbd-sm">5</kbd></td>
            <td>Toggle <span class="font-semibold">play / pause</span>. Ignored while typing in a tag input.</td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">T</kbd> / <kbd class="kbd kbd-sm">1</kbd></td>
            <td>Toggle the Edit Tags panel</td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">W</kbd></td>
            <td>
              Toggle the <span class="font-semibold">Playback Issue</span> tag and advance to the next video.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">D</kbd></td>
            <td>
              Mark <span class="font-semibold">to Delete</span> — moves the file to
              <code>&lt;set&gt;/_ToDelete</code> and advances to the next video.
              The app never actually deletes anything.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">U</kbd></td>
            <td>
              <span class="font-semibold">Undo</span> the mark on the current video —
              moves the file back to its original location and clears the flag.
              Typical review flow: press <kbd class="kbd kbd-xs">W</kbd>/<kbd class="kbd kbd-xs">D</kbd>,
              realize the mistake, press <kbd class="kbd kbd-xs">←</kbd> to go back,
              then <kbd class="kbd kbd-xs">U</kbd>.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">I</kbd></td>
            <td>
              Toggle the <span class="font-semibold">File Info</span> dialog — shows
              dimensions, codec, bitrate, frame rate, stream counts, and other
              technical metadata for the current video.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">R</kbd></td>
            <td>
              Toggle the <span class="font-semibold">Needs Review</span> flag on
              the current video. If it was set, clears it and advances to the
              next video (the common review-pile workflow). If it was unset,
              re-flags the video and stays put so you can keep working with it.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">F</kbd></td>
            <td>
              Toggle the <span class="font-semibold">Favorite</span> flag on the
              current video and save. Works on both the Video Player and the
              inline player in the Video Browser.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">\</kbd></td>
            <td>
              Snap the video back to <span class="font-semibold">fit-to-column</span>
              size. The percent indicator next to the filename shows current
              size vs. native and also clicks back to fit. Capped at 2x native
              by default so a low-res file doesn't balloon. Resets on every
              new video.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">K</kbd></td>
            <td>
              Drop a <span class="font-semibold">bookmark</span> at the current
              time. Default label is the timestamp; rename it inline in the
              Bookmarks list under the video. Bookmarks show as blue pins on
              the scrubber and save with the video on next navigation.
            </td>
          </tr>
          <tr>
            <td>
              <kbd class="kbd kbd-sm">Ctrl</kbd>+<kbd class="kbd kbd-sm">⇧</kbd>+<kbd class="kbd kbd-sm">[</kbd> /
              <kbd class="kbd kbd-sm">Ctrl</kbd>+<kbd class="kbd kbd-sm">⇧</kbd>+<kbd class="kbd kbd-sm">]</kbd>
            </td>
            <td>
              Start / end a <span class="font-semibold">clip</span>.
              <kbd class="kbd kbd-xs">Ctrl</kbd>+<kbd class="kbd kbd-xs">⇧</kbd>+<kbd class="kbd kbd-xs">[</kbd>
              captures the clip's in-point and pins the scrubber visible.
              Auto-skip of existing Hide blocks is suspended so the in-point
              and end-point can both be reached.
              <kbd class="kbd kbd-xs">Ctrl</kbd>+<kbd class="kbd kbd-xs">⇧</kbd>+<kbd class="kbd kbd-xs">]</kbd>
              commits and saves a new video row with <code>IsClip = true</code>;
              <kbd class="kbd kbd-xs">Esc</kbd> cancels. Tags are inherited
              from the parent. Playing the clip auto-seeks to the in-point
              and loops at the out-point. Filter your library by the Clip
              flag to see just your clips.
            </td>
          </tr>
          <tr>
            <td>
              <kbd class="kbd kbd-sm">⇧</kbd>+<kbd class="kbd kbd-sm">[</kbd> /
              <kbd class="kbd kbd-sm">⇧</kbd>+<kbd class="kbd kbd-sm">]</kbd>
            </td>
            <td>
              <span class="font-semibold">Block editor.</span>
              <kbd class="kbd kbd-xs">⇧</kbd>+<kbd class="kbd kbd-xs">[</kbd>
              captures the start of a do-not-play segment at the current
              time and pins the scrubber visible. Auto-skip is suspended
              while you're picking the end so the entire timeline is
              scrubbable. <kbd class="kbd kbd-xs">⇧</kbd>+<kbd class="kbd kbd-xs">]</kbd>
              commits the block; <kbd class="kbd kbd-xs">Esc</kbd> cancels.
              Overlapping or directly-adjacent blocks merge into one. Blocks
              render as red strips on the scrubber and in a timestamped
              list below it for jump-to and delete. (A double-tap of
              <kbd class="kbd kbd-xs">]</kbd> / <kbd class="kbd kbd-xs">[</kbd>
              blocks the head / tail in one gesture.)
            </td>
          </tr>
          <tr>
            <td>
              <kbd class="kbd kbd-sm">[</kbd> /
              <kbd class="kbd kbd-sm">]</kbd> /
              <kbd class="kbd kbd-sm">\</kbd>
            </td>
            <td>
              <span class="font-semibold">Video size.</span>
              <kbd class="kbd kbd-xs">[</kbd> shrinks,
              <kbd class="kbd kbd-xs">]</kbd> enlarges
              (cap 2× the native pixel width).
              <kbd class="kbd kbd-xs">\</kbd> resets to fit-to-column.
              The current scale shows next to the Download button and is
              click-to-reset.
            </td>
          </tr>
          <tr>
            <td>Numpad <kbd class="kbd kbd-sm">1/3/4/6/7/9</kbd></td>
            <td>
              Seek, no matter where focus is. Always preventDefault'd so
              numpad digits don't land in tag inputs.
            </td>
          </tr>
          <tr>
            <td>Numpad <kbd class="kbd kbd-sm">5</kbd></td>
            <td>Play / pause — works even while a tag input has focus.</td>
          </tr>
          <tr>
            <td>Numpad <kbd class="kbd kbd-sm">0</kbd></td>
            <td>Jump to the start of the video (00:00).</td>
          </tr>
          <tr>
            <td>Numpad <kbd class="kbd kbd-sm">−</kbd></td>
            <td>Jump to 10 seconds from the end of the video.</td>
          </tr>
          <tr>
            <td>
              <kbd class="kbd kbd-sm">⇧</kbd> + <kbd class="kbd kbd-sm">1/3/4/6/7/9</kbd>
            </td>
            <td>
              Seek while typing in a tag input using the top-row digit keys.
              Shift prevents the corresponding character (<code>!#$^&amp;(</code>)
              from landing in the field.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">8</kbd></td>
            <td>Jump to <span class="font-semibold">10 seconds from the end</span> of the video (same as Numpad <kbd class="kbd kbd-xs">−</kbd>).</td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">G</kbd></td>
            <td>
              Toggle <span class="font-semibold">Grid view</span> in the Video
              Browser — hide the player and show all thumbnails as a grid, or
              switch back to the player + strip.
            </td>
          </tr>
          <tr>
            <td><kbd class="kbd kbd-sm">M</kbd></td>
            <td>
              Open the <span class="font-semibold">Move</span> dialog for the
              current video — browse to a destination folder and move the file
              (logged and undoable). Disabled for clips.
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </section>
</div>

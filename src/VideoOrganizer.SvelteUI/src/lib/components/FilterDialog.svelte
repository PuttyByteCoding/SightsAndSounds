<script lang="ts">
  // Routing dialog for tag clicks. Every tag click parks the tag in
  // filterStore.pending; this dialog offers the user five things to
  // do with it:
  //   1. Filter — Required / Optional / Excluded (the original
  //      behavior; still the default action and what Enter binds to)
  //   2. Rename — quick inline rename via api.updateTag
  //   3. Favorite toggle — flip the tag's isFavorite flag so it
  //      shows up in the browse sidebar's Favorites tree
  //   4. Remove from this video — only when filterStore.pending was
  //      raised from a video's tag pill (videoId set)
  //   5. Delete tag — destructive, two-click confirm; removes the
  //      tag from every video and from the database
  //
  // Sections 2-5 are tag-only; folder / status / missing pendings
  // collapse to just the filter row, matching the previous behavior.
  import { filterStore } from '$lib/filterStore.svelte';
  import { api } from '$lib/api';
  import type { Tag } from '$lib/types';

  interface Props {
    // Fired after a successful rename or delete so the host can
    // refresh anything keyed off the tag (sidebar tag-tree counts,
    // tag pills on grid cards, etc.).
    onTagChanged?: () => void | Promise<void>;
    // Fired after "Remove from this video" succeeds. The host should
    // re-fetch the affected video and patch its grid copy so the tag
    // pill disappears immediately.
    onVideoChanged?: (videoId: string) => void | Promise<void>;
  }

  let { onTagChanged, onVideoChanged }: Props = $props();

  // Per-action busy state so a slow round-trip doesn't let the user
  // re-click and double-fire. Errors land in errorMessage; the modal
  // stays open on failure so the user can see what went wrong.
  let renaming = $state(false);
  let removing = $state(false);
  let deleting = $state(false);
  let togglingFav = $state(false);
  let togglingHidden = $state(false);
  let errorMessage = $state<string | null>(null);

  // Cached full Tag for the current pending. Pre-fetched on dialog
  // open so the favorite toggle can render with the correct
  // initial state, and so onRename / onToggleFavorite can build
  // their UpdateTagRequest payloads without a second round-trip.
  // Null while loading; tagLoadFailed when the fetch errored (the
  // favorite toggle then disables itself with an explanatory hint).
  let loadedTag = $state<Tag | null>(null);
  let tagLoadFailed = $state(false);

  // Two-step delete: first click sets `deleteConfirming`, second
  // click commits. Cleaner than nesting another modal inside this
  // one; keeps everything visible in the same dialog frame.
  let deleteConfirming = $state(false);

  // Inline rename input. Reset whenever pending changes so a stale
  // value from a previous tag click never leaks into a new dialog.
  // editingName toggles between read-only display (name + ✎ pencil)
  // and edit mode (input + Save). Esc cancels edit; outside edit
  // mode Esc closes the dialog as before.
  let renameValue = $state('');
  let renameInputEl: HTMLInputElement | null = $state(null);
  let editingName = $state(false);

  function startNameEdit() {
    if (busy) return;
    editingName = true;
    queueMicrotask(() => {
      renameInputEl?.focus();
      renameInputEl?.select();
    });
  }
  function cancelNameEdit() {
    editingName = false;
    // Revert any unsaved edits to the canonical label so reopening
    // the input doesn't ghost a stale draft.
    const p = filterStore.pending;
    if (p) renameValue = p.label;
  }

  // Track whichever pending-id was last used to seed renameValue so
  // we know when to refresh the field. Comparing the pending object
  // identity also works but the id is more stable across reactivity
  // ticks.
  let lastPendingValue: string | null = null;
  $effect(() => {
    const p = filterStore.pending;
    if (p && p.value !== lastPendingValue) {
      lastPendingValue = p.value;
      renameValue = p.label;
      errorMessage = null;
      deleteConfirming = false;
      editingName = false;
      loadedTag = null;
      tagLoadFailed = false;
      // Pre-fetch the full tag so the favorite toggle can render
      // immediately with the correct state. The id-gate inside the
      // .then() guards against the user clicking through to a
      // different tag while this one is in flight.
      if (p.type === 'tag') {
        const expected = p.value;
        api.getTag(expected).then(t => {
          if (filterStore.pending?.value === expected) loadedTag = t;
        }).catch(() => {
          if (filterStore.pending?.value === expected) tagLoadFailed = true;
        });
      }
    } else if (!p) {
      lastPendingValue = null;
      loadedTag = null;
      tagLoadFailed = false;
    }
  });

  // Keyboard contract:
  //   Esc    — cancel name-edit if it's active; otherwise close the
  //            dialog
  //   Enter  — commit rename if focus is in the name input;
  //            otherwise apply Optional filter (the original
  //            quick-pick default)
  function onKey(e: KeyboardEvent) {
    if (e.key === 'Escape') {
      e.preventDefault();
      if (editingName) {
        cancelNameEdit();
      } else {
        filterStore.cancelPending();
      }
      return;
    }
    if (e.key === 'Enter') {
      const target = e.target as HTMLElement | null;
      if (target === renameInputEl) {
        e.preventDefault();
        void onRename();
      } else if (target?.tagName !== 'INPUT' && target?.tagName !== 'TEXTAREA') {
        e.preventDefault();
        filterStore.applyPending('optional');
      }
    }
  }

  async function onRename() {
    const p = filterStore.pending;
    if (!p || p.type !== 'tag') return;
    const next = renameValue.trim();
    if (!next || next === p.label) {
      // No-op rename: just close the inline editor and bail.
      cancelNameEdit();
      return;
    }
    renaming = true;
    errorMessage = null;
    try {
      // Reuse the pre-fetched tag when available (already loaded by
      // the pending-watch effect); fall back to a fresh fetch if it
      // failed to load or is still in flight.
      const tag = loadedTag ?? await api.getTag(p.value);
      await api.updateTag(p.value, {
        name: next,
        aliases: tag.aliases,
        isFavorite: tag.isFavorite,
        sortOrder: tag.sortOrder,
        notes: tag.notes
      });
      editingName = false;
      filterStore.cancelPending();
      if (onTagChanged) await onTagChanged();
    } catch (e) {
      errorMessage = `Failed to rename: ${e instanceof Error ? e.message : String(e)}`;
    } finally {
      renaming = false;
    }
  }

  // Flip the tag's isFavorite flag. Optimistic update on loadedTag
  // so the toggle visibly snaps; rollback on API failure. Notifies
  // the host via onTagChanged so the browse sidebar's Favorites
  // tree picks up the change without a manual reload.
  async function onToggleFavorite() {
    const p = filterStore.pending;
    if (!p || p.type !== 'tag' || !loadedTag) return;
    togglingFav = true;
    errorMessage = null;
    const previous = loadedTag;
    const next = !previous.isFavorite;
    loadedTag = { ...previous, isFavorite: next };
    try {
      await api.updateTag(p.value, {
        name: previous.name,
        aliases: previous.aliases,
        isFavorite: next,
        sortOrder: previous.sortOrder,
        notes: previous.notes
      });
      if (onTagChanged) await onTagChanged();
    } catch (e) {
      // Roll back so the toggle reflects the actual server state.
      loadedTag = previous;
      errorMessage = `Failed to update favorite: ${e instanceof Error ? e.message : String(e)}`;
    } finally {
      togglingFav = false;
    }
  }

  // Flip the tag's "hidden by default" flag (issue #84). Optimistic, with
  // rollback on failure, same as the favorite toggle. onTagChanged lets the
  // host refresh the sidebar so the "(default hidden)" marker updates.
  async function onToggleHidden() {
    const p = filterStore.pending;
    if (!p || p.type !== 'tag' || !loadedTag) return;
    togglingHidden = true;
    errorMessage = null;
    const previous = loadedTag;
    const next = !previous.hiddenByDefault;
    loadedTag = { ...previous, hiddenByDefault: next };
    try {
      await api.setTagHiddenByDefault(p.value, next);
      if (onTagChanged) await onTagChanged();
    } catch (e) {
      loadedTag = previous;
      errorMessage = `Failed to update hidden-by-default: ${e instanceof Error ? e.message : String(e)}`;
    } finally {
      togglingHidden = false;
    }
  }

  async function onRemoveFromVideo() {
    const p = filterStore.pending;
    if (!p?.videoId || p.type !== 'tag') return;
    const videoId = p.videoId;
    removing = true;
    errorMessage = null;
    try {
      // Re-fetch the video so we set the *current* tag list minus
      // this one. Using a stale local copy could clobber a
      // concurrent tag change made elsewhere.
      const v = await api.getVideo(videoId);
      if (!v) throw new Error('Video not found');
      const nextIds = v.tags.filter(t => t.id !== p.value).map(t => t.id);
      await api.setVideoTags(videoId, { tagIds: nextIds });
      filterStore.cancelPending();
      if (onVideoChanged) await onVideoChanged(videoId);
    } catch (e) {
      errorMessage = `Failed to remove tag: ${e instanceof Error ? e.message : String(e)}`;
    } finally {
      removing = false;
    }
  }

  async function onDelete() {
    const p = filterStore.pending;
    if (!p || p.type !== 'tag') return;
    if (!deleteConfirming) {
      deleteConfirming = true;
      return;
    }
    deleting = true;
    errorMessage = null;
    try {
      await api.deleteTag(p.value);
      filterStore.cancelPending();
      if (onTagChanged) await onTagChanged();
    } catch (e) {
      errorMessage = `Failed to delete: ${e instanceof Error ? e.message : String(e)}`;
    } finally {
      deleting = false;
      deleteConfirming = false;
    }
  }

  // Convenience derived values for the template — readable conditions
  // beat repeated inline expressions on every {#if}.
  const isTag = $derived(filterStore.pending?.type === 'tag');
  const hasVideoCtx = $derived(!!filterStore.pending?.videoId);
  const busy = $derived(renaming || removing || deleting || togglingFav || togglingHidden);
</script>

<svelte:window onkeydown={filterStore.pending ? onKey : undefined} />

{#if filterStore.pending}
  {@const p = filterStore.pending}
  <!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
  <div class="modal modal-open" role="dialog" aria-modal="true" tabindex="-1">
    <div class="modal-box max-w-md">
      <h3 class="font-semibold text-lg mb-1">Tag Actions</h3>

      <!-- Tag identity row. Group label on the left; the name +
           pencil + favorite cluster as a tight group right next to
           it (gap-1 between the icons, gap-2 between the label and
           the cluster). With flex-1 dropped, the cluster hugs the
           label's right edge instead of stretching across the row. -->
      <div class="flex items-center gap-2 mb-4 flex-wrap">
        {#if p.tagGroupName}
          <span class="uppercase tracking-wide text-xs text-base-content/70 shrink-0">{p.tagGroupName}:</span>
        {:else}
          <span class="uppercase tracking-wide text-xs text-base-content/70 shrink-0">{p.type}:</span>
        {/if}

        {#if isTag && editingName}
          <!-- Edit mode keeps flex-1 on the input so the field has
               room to type into; the Save/Cancel buttons sit at the
               far right of the row.  -->
          <input
            bind:this={renameInputEl}
            type="text"
            class="input input-bordered input-sm flex-1 min-w-0"
            bind:value={renameValue}
            placeholder={p.label}
            disabled={busy}
          />
          <button
            class="btn btn-xs btn-soft btn-primary btn-cta shrink-0"
            onclick={onRename}
            disabled={busy || !renameValue.trim()}
            title="Save (Enter)"
          >
            {#if renaming}<span class="loading loading-spinner loading-xs"></span>{/if}
            Save
          </button>
          <button
            class="btn btn-xs btn-cancel shrink-0"
            onclick={cancelNameEdit}
            disabled={busy}
            title="Cancel rename (Esc)"
          >Cancel</button>
        {:else}
          <!-- Name + pencil + star cluster — gap-1 keeps the icons
               visually attached to the name. min-w-0 + truncate on
               the inner span lets a long tag name ellipsize without
               pushing the icons off-row, and flex-wrap on the parent
               lets the cluster drop to a new line on a very narrow
               modal width rather than overflow. -->
          <div class="flex items-center gap-1 min-w-0">
            <span class="font-medium min-w-0 truncate">{p.label}</span>
            {#if isTag}
              <button
                type="button"
                class="btn btn-ghost btn-xs px-1 shrink-0"
                onclick={startNameEdit}
                disabled={busy}
                aria-label="Rename tag"
                title="Rename tag"
              >✎</button>
              <!-- Favorite ★. Outline when off, filled-warning when
                   on — same visual language as the favorite toggle
                   on the video player and the /tags table. -->
              <button
                type="button"
                class="btn btn-ghost btn-xs px-1 shrink-0 leading-none"
                onclick={onToggleFavorite}
                disabled={busy || !loadedTag}
                aria-pressed={loadedTag?.isFavorite ?? false}
                aria-label={loadedTag?.isFavorite ? 'Unmark as favorite' : 'Mark as favorite'}
                title={loadedTag === null && !tagLoadFailed
                  ? 'Loading tag…'
                  : tagLoadFailed
                    ? 'Tag fetch failed — favorite toggle unavailable'
                    : loadedTag?.isFavorite
                      ? 'Unmark as favorite'
                      : 'Mark as favorite'}
              >
                {#if togglingFav}
                  <span class="loading loading-spinner loading-xs"></span>
                {:else}
                  <svg viewBox="0 0 24 24" class="h-5 w-5"
                    fill={loadedTag?.isFavorite ? 'rgb(234 179 8)' : 'none'}
                    stroke="rgb(255 255 255 / 0.85)" stroke-width="1.25" stroke-linejoin="round">
                    <path d="M12 2.5 L14.6 8.9 L21.5 9.5 L16.2 14.1 L17.8 20.9 L12 17.3 L6.2 20.9 L7.8 14.1 L2.5 9.5 L9.4 8.9 Z" />
                  </svg>
                {/if}
              </button>
            {/if}
          </div>
        {/if}
      </div>

      {#if errorMessage}
        <div class="alert alert-error text-sm mb-3">
          <span>{errorMessage}</span>
          <button class="btn btn-xs" onclick={() => (errorMessage = null)}>Dismiss</button>
        </div>
      {/if}

      <!-- Filter actions: add this item to one of the three buckets,
           keeping the current filter intact. (The old "Clear Existing
           Filter & Set" row that wiped the whole filter was removed.) -->
      <div class="text-xs uppercase tracking-wide text-base-content/60 mb-1">Add to Filter</div>
      <div class="flex gap-2">
        <button
          class="btn btn-sm btn-soft btn-success border border-success/50 flex-1"
          onclick={() => filterStore.applyPending('required')}
          disabled={busy}
        >Required</button>
        <button
          class="btn btn-sm btn-soft btn-info border border-info/50 flex-1"
          onclick={() => filterStore.applyPending('optional')}
          disabled={busy}
        >Optional</button>
        <button
          class="btn btn-sm btn-soft btn-error border border-error/50 flex-1"
          onclick={() => filterStore.applyPending('excluded')}
          disabled={busy}
        >Exclude</button>
      </div>

      {#if isTag}
        <!-- Hidden-by-default toggle (issue #84). Videos with this tag are
             hidden from the grid unless the user filters for the tag. Reflects
             the pre-fetched tag's flag; optimistic on click. -->
        <div class="flex items-center justify-between gap-3 mt-4 p-2 rounded bg-base-200">
          <div class="min-w-0">
            <div class="text-sm font-medium">Hidden by default</div>
            <div class="text-xs text-base-content/60">
              Hide videos with this tag unless you filter for it.
            </div>
          </div>
          <span class="shrink-0 flex items-center gap-2">
            {#if togglingHidden}<span class="loading loading-spinner loading-xs"></span>{/if}
            <input
              type="checkbox"
              class="toggle toggle-sm toggle-primary"
              checked={loadedTag?.hiddenByDefault ?? false}
              disabled={busy || !loadedTag}
              onchange={onToggleHidden}
              aria-label="Hidden by default"
            />
          </span>
        </div>
      {/if}

      {#if isTag}
        <!-- Visual divider between the filter row (additive,
             primary action) and the destructive row below. daisyUI
             `divider divider-start` draws a horizontal line with a
             small "Remove" label flush left, so the user can't
             confuse the two clusters at a glance — particularly
             important on a narrow modal where button widths look
             similar. mt-8 gives extra breathing room so the destructive
             cluster is clearly its own section, not a continuation
             of Filter. -->
        <div class="divider divider-start text-[10px] uppercase tracking-wider text-base-content/50 mt-8 mb-3">Remove</div>

        <!-- Compact destructive actions — Remove-from-video (when
             the click came from a video tag pill) and Delete (the
             whole tag). Side-by-side btn-xs so they read as
             secondary affordances, not headline actions. The Delete
             button still uses a two-step confirm pattern, the
             confirmation just expands its label in place.
             Long tag names are truncated inside the buttons via the
             three-span CSS pattern: a fixed prefix, a flex-1 middle
             span with `truncate`, and a fixed suffix. The middle
             span ellipsizes (`Remove "Beck and t…" from Video`)
             when the rendered name doesn't fit, while the verb
             prefix and "from Video" / closing quote stay visible —
             so the user can always see what action they're about
             to take. The full label is in the tooltip. -->
        <!-- Truncation pattern (applied to every button below where
             a tag name is interpolated into the label):
               · button:        flex-1 min-w-0 flex-nowrap
               · inner wrapper: flex w-full min-w-0
               · prefix span:   shrink-0
               · target span:   flex-1 min-w-0 truncate
               · suffix span:   shrink-0
             The wrapper takes the button's full width so the
             flex layout has a constrained reference; flex-1 +
             min-w-0 on the target lets it shrink below content
             width; truncate then ellipsizes. Without the flex-1
             the target stays at intrinsic content width and the
             whole row overflows / wraps. -->
        <div class="flex items-stretch gap-2">
          {#if hasVideoCtx}
            <button
              class="btn btn-xs btn-soft btn-warning border border-warning/50 flex-1 min-w-0 flex-nowrap"
              onclick={onRemoveFromVideo}
              disabled={busy}
              title={`Remove "${p.label}" from this video`}
            >
              {#if removing}<span class="loading loading-spinner loading-xs"></span>{/if}
              <span class="flex items-baseline w-full min-w-0">
                <span class="shrink-0">Remove&nbsp;"</span>
                <span class="flex-1 min-w-0 truncate">{p.label}</span>
                <span class="shrink-0">"&nbsp;from&nbsp;Video</span>
              </span>
            </button>
          {/if}
          <button
            class="btn btn-xs {deleteConfirming ? 'btn-error' : 'btn-soft btn-error border border-error/50'} flex-1 min-w-0 flex-nowrap"
            onclick={onDelete}
            disabled={busy}
            title={deleteConfirming
              ? `Click again to permanently delete "${p.label}" from every video`
              : `Permanently delete "${p.label}" (removes it from every video)`}
          >
            {#if deleting}<span class="loading loading-spinner loading-xs"></span>{/if}
            <span class="flex items-baseline w-full min-w-0">
              {#if deleteConfirming}
                <span class="shrink-0">Confirm&nbsp;delete&nbsp;"</span>
                <span class="flex-1 min-w-0 truncate">{p.label}</span>
                <span class="shrink-0">"</span>
              {:else}
                <span class="shrink-0">Delete&nbsp;"</span>
                <span class="flex-1 min-w-0 truncate">{p.label}</span>
                <span class="shrink-0">"</span>
              {/if}
            </span>
          </button>
        </div>
      {/if}

      <div class="modal-action mt-4">
        <button
          class="btn btn-sm btn-cancel"
          onclick={() => filterStore.cancelPending()}
          disabled={busy}
        >Cancel</button>
      </div>
    </div>
    <div class="modal-backdrop"></div>
  </div>
{/if}

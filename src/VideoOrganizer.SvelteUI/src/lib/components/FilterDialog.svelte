<script lang="ts">
  // Routing dialog for tag clicks. Every tag click parks the tag in
  // filterStore.pending; this dialog offers the user four things to
  // do with it:
  //   1. Filter — Required / Optional / Excluded (the original
  //      behavior; still the default action and what Enter binds to)
  //   2. Rename — quick inline rename via api.updateTag
  //   3. Remove from this video — only when filterStore.pending was
  //      raised from a video's tag pill (videoId set)
  //   4. Delete tag — destructive, two-click confirm; removes the
  //      tag from every video and from the database
  //
  // Sections 2-4 are tag-only; folder / status / missing pendings
  // collapse to just the filter row, matching the previous behavior.
  import { filterStore } from '$lib/filterStore.svelte';
  import { api } from '$lib/api';

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
  let errorMessage = $state<string | null>(null);

  // Two-step delete: first click sets `deleteConfirming`, second
  // click commits. Cleaner than nesting another modal inside this
  // one; keeps everything visible in the same dialog frame.
  let deleteConfirming = $state(false);

  // Inline rename input. Reset whenever pending changes so a stale
  // value from a previous tag click never leaks into a new dialog.
  let renameValue = $state('');
  let renameInputEl: HTMLInputElement | null = $state(null);

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
    } else if (!p) {
      lastPendingValue = null;
    }
  });

  // Keyboard contract:
  //   Esc    — cancel and close
  //   Enter  — apply Optional filter, UNLESS focus is in the rename
  //            input (then Enter commits the rename)
  // Same approach as the previous version, just split so we can
  // route Enter to either action depending on focus.
  function onKey(e: KeyboardEvent) {
    if (e.key === 'Escape') {
      e.preventDefault();
      filterStore.cancelPending();
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
    if (!next || next === p.label) return;
    renaming = true;
    errorMessage = null;
    try {
      // Need the existing aliases / favorite / sortOrder / notes —
      // UpdateTagRequest is a full PUT, not a partial PATCH.
      const tag = await api.getTag(p.value);
      await api.updateTag(p.value, {
        name: next,
        aliases: tag.aliases,
        isFavorite: tag.isFavorite,
        sortOrder: tag.sortOrder,
        notes: tag.notes
      });
      filterStore.cancelPending();
      if (onTagChanged) await onTagChanged();
    } catch (e) {
      errorMessage = `Failed to rename: ${e instanceof Error ? e.message : String(e)}`;
    } finally {
      renaming = false;
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
  const busy = $derived(renaming || removing || deleting);
</script>

<svelte:window onkeydown={filterStore.pending ? onKey : undefined} />

{#if filterStore.pending}
  {@const p = filterStore.pending}
  <!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
  <div class="modal modal-open" role="dialog" aria-modal="true" tabindex="-1">
    <div class="modal-box max-w-md">
      <h3 class="font-semibold text-lg mb-1">Tag Actions</h3>
      <div class="text-base-content/70 mb-4">
        {#if p.tagGroupName}
          <span class="uppercase tracking-wide text-xs">{p.tagGroupName}:</span>
        {:else}
          <span class="uppercase tracking-wide text-xs">{p.type}:</span>
        {/if}
        <span class="font-medium">{p.label}</span>
      </div>

      {#if errorMessage}
        <div class="alert alert-error text-sm mb-3">
          <span>{errorMessage}</span>
          <button class="btn btn-xs" onclick={() => (errorMessage = null)}>Dismiss</button>
        </div>
      {/if}

      <!-- Filter (always shown — works for tag, folder, status, missing) -->
      <div class="text-xs uppercase tracking-wide text-base-content/60 mb-1">Add to filter</div>
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
        <!-- Rename — inline so the user can fix a typo without
             leaving the dialog. The full Edit Tag flow (aliases,
             favorite, notes) is still available via the ✎ pencil
             on tag pills; this is the quick path. -->
        <div class="text-xs uppercase tracking-wide text-base-content/60 mt-4 mb-1">Rename Tag</div>
        <div class="flex gap-2">
          <input
            bind:this={renameInputEl}
            type="text"
            class="input input-bordered input-sm flex-1"
            bind:value={renameValue}
            placeholder={p.label}
            disabled={busy}
          />
          <button
            class="btn btn-sm btn-soft btn-primary btn-cta"
            onclick={onRename}
            disabled={busy || !renameValue.trim() || renameValue.trim() === p.label}
          >
            {#if renaming}<span class="loading loading-spinner loading-xs"></span>{/if}
            Rename
          </button>
        </div>

        {#if hasVideoCtx}
          <!-- Remove-from-video — only available when the click
               originated from a specific video's tag pill, since
               we need a videoId to act on. -->
          <div class="text-xs uppercase tracking-wide text-base-content/60 mt-4 mb-1">This video</div>
          <button
            class="btn btn-sm btn-soft btn-warning border border-warning/50 w-full"
            onclick={onRemoveFromVideo}
            disabled={busy}
            title="Remove this tag from the video, but keep the tag itself"
          >
            {#if removing}<span class="loading loading-spinner loading-xs"></span>{/if}
            Remove "{p.label}" from this video
          </button>
        {/if}

        <!-- Delete tag — destructive, two-step confirm so a stray
             click doesn't wipe a tag from every video that uses it. -->
        <div class="text-xs uppercase tracking-wide text-base-content/60 mt-4 mb-1">Danger zone</div>
        <button
          class="btn btn-sm {deleteConfirming ? 'btn-error' : 'btn-soft btn-error border border-error/50'} w-full"
          onclick={onDelete}
          disabled={busy}
          title={deleteConfirming
            ? 'Click again to permanently delete this tag from every video'
            : 'Permanently delete this tag (removes it from every video)'}
        >
          {#if deleting}<span class="loading loading-spinner loading-xs"></span>{/if}
          {#if deleteConfirming}
            Click again to confirm — Delete "{p.label}" everywhere
          {:else}
            Delete tag "{p.label}"
          {/if}
        </button>
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

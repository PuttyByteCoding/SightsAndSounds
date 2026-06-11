// Module-level state for the transient "tag applied" overlay drawn over
// the video player. Any surface that mutates the current video's tag set
// (EditTagsPanel composer/checkboxes, Alt+digit flag toggles, future
// key-bound tags) calls tagFlash.show("Jackson") and VideoPlayer renders
// the text floating over the picture for a moment — immediate visual
// confirmation that the tag landed without the user having to glance
// down at the pill row.

export interface TagFlashEntry {
  id: number;
  text: string;
  // 'added' renders in the success tint, 'removed' in the muted/error
  // tint with a leading minus so a toggle-off is distinguishable from
  // a toggle-on at a glance.
  kind: 'added' | 'removed';
}

// How long an entry stays mounted. Slightly longer than the CSS
// animation (1.4s) so the fade-out completes before unmount instead of
// the element popping out mid-animation.
const FLASH_LIFETIME_MS = 1500;

function _TagFlashStore() {
  let entries = $state<TagFlashEntry[]>([]);
  let nextId = 0;

  function push(text: string, kind: TagFlashEntry['kind']) {
    const id = nextId++;
    entries = [...entries, { id, text, kind }];
    setTimeout(() => {
      entries = entries.filter((e) => e.id !== id);
    }, FLASH_LIFETIME_MS);
  }

  return {
    get entries() { return entries; },
    show(text: string) { push(text, 'added'); },
    showRemoved(text: string) { push(text, 'removed'); }
  };
}

export const tagFlash = _TagFlashStore();

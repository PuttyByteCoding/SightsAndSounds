// Module-level state for user-defined key → tag bindings. The user maps
// a free key (F-keys or letters the player doesn't already use) to an
// existing tag; pressing that key in the player toggles the tag on the
// current video. Managed via TagKeyBindingsModal (⌨ button in the
// player toolbar) and persisted to localStorage.
//
// The binding snapshots the tag's name/group at bind time so the player
// can build the optimistic VideoTagSummary without a per-keypress
// round-trip. A later tag rename leaves the snapshot stale until the
// next bind — cosmetic only, since the server re-fetch after each
// toggle restores canonical names everywhere visible.

export interface TagKeyBinding {
  // Canonical key value as reported by KeyboardEvent.key — 'F1'..'F10'
  // verbatim, letters stored lowercase.
  key: string;
  tagId: string;
  tagName: string;
  tagGroupId: string;
  tagGroupName: string;
}

// Keys offered for binding. Everything the player already claims is
// excluded: Space (play/pause), W/D/U/R/T/I/F/K (marks + panels),
// brackets + backslash (blocks/clips/zoom), digits (seeks), arrows
// (navigation). Browser-critical F-keys are also out: F5 (refresh),
// F11 (fullscreen), F12 (devtools).
export const BINDABLE_KEYS: readonly string[] = [
  'F1', 'F2', 'F3', 'F4', 'F6', 'F7', 'F8', 'F9', 'F10',
  'a', 'b', 'c', 'e', 'g', 'h', 'j', 'l', 'm',
  'n', 'o', 'p', 'q', 's', 'v', 'x', 'y', 'z'
];

const STORAGE_KEY = 'tagKeyBindings';

function normalizeKey(key: string): string {
  return key.length === 1 ? key.toLowerCase() : key;
}

function loadPersisted(): TagKeyBinding[] {
  if (typeof localStorage === 'undefined') return [];
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    // Keep only structurally sound rows on keys we still offer, so a
    // hand-edited or out-of-date payload can't wedge the player.
    return parsed.filter((b: any) =>
      typeof b?.key === 'string' && BINDABLE_KEYS.includes(b.key)
      && typeof b?.tagId === 'string' && typeof b?.tagName === 'string'
      && typeof b?.tagGroupId === 'string' && typeof b?.tagGroupName === 'string');
  } catch {
    return [];
  }
}

function _TagKeyBindingsStore() {
  let bindings = $state<TagKeyBinding[]>(loadPersisted());

  function persist() {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(STORAGE_KEY, JSON.stringify(bindings));
  }

  return {
    get bindings() { return bindings; },

    // Lookup by KeyboardEvent.key. Letters arrive in whichever case
    // Shift dictates, so normalize before matching.
    forKey(key: string): TagKeyBinding | undefined {
      const k = normalizeKey(key);
      return bindings.find((b) => b.key === k);
    },

    // Bind a key, replacing any existing binding on the same key AND
    // any existing binding of the same tag (one key per tag keeps the
    // list legible).
    bind(binding: TagKeyBinding) {
      const k = normalizeKey(binding.key);
      bindings = [
        ...bindings.filter((b) => b.key !== k && b.tagId !== binding.tagId),
        { ...binding, key: k }
      ];
      persist();
    },

    unbind(key: string) {
      const k = normalizeKey(key);
      bindings = bindings.filter((b) => b.key !== k);
      persist();
    },

    // Keys still free for the "Add binding" select.
    get availableKeys(): string[] {
      return BINDABLE_KEYS.filter((k) => !bindings.some((b) => b.key === k));
    }
  };
}

export const tagKeyBindings = _TagKeyBindingsStore();

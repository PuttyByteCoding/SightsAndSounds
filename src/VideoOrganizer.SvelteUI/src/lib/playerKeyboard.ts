// Pure keyboard-decision helpers extracted from VideoPlayer.svelte (#126).
//
// These map a key event to a *playback action* (or a flag index) without
// touching component state, so they can be unit-tested in isolation and the
// 3.2k-line component's keydown handler shrinks to "resolve, then dispatch".
// The component still owns the modal/typing guards, ordering, and dispatch —
// only the per-key mapping (which was duplicated three times for the numpad,
// Shift+digit, and plain-digit seek tables) lives here now.

export interface SeekSettings {
  key1Seconds: number;
  key3Seconds: number;
  key4Seconds: number;
  key6Seconds: number;
  key7Seconds: number;
  key9Seconds: number;
}

export type PlaybackAction =
  | { kind: 'seek'; seconds: number } // relative, signed (negative = backward)
  | { kind: 'playPause' }
  | { kind: 'seekToStart' }
  | { kind: 'seekToNearEnd' };

// Numpad keys are dedicated playback controls — they fire even while a tag
// input has focus, matched on `code` so NumLock state is irrelevant.
//   Numpad5 → play/pause   Numpad0 → start   NumpadSubtract → near end
//   Numpad 1/3/4/6/7/9 → relative seek (per settings; 1/4/7 back, 3/6/9 fwd)
export function numpadPlaybackAction(code: string, s: SeekSettings): PlaybackAction | null {
  switch (code) {
    case 'Numpad5': return { kind: 'playPause' };
    case 'Numpad0': return { kind: 'seekToStart' };
    case 'NumpadSubtract': return { kind: 'seekToNearEnd' };
    case 'Numpad1': return { kind: 'seek', seconds: -s.key1Seconds };
    case 'Numpad3': return { kind: 'seek', seconds: s.key3Seconds };
    case 'Numpad4': return { kind: 'seek', seconds: -s.key4Seconds };
    case 'Numpad6': return { kind: 'seek', seconds: s.key6Seconds };
    case 'Numpad7': return { kind: 'seek', seconds: -s.key7Seconds };
    case 'Numpad9': return { kind: 'seek', seconds: s.key9Seconds };
    default: return null;
  }
}

// Shift+top-row digit: the numpad-seek equivalent for keyboards without a
// numpad. Matched on `code` OR the shifted glyph so it works across layouts.
// The caller gates this on Shift held with no Ctrl/Meta/Alt.
export function shiftDigitSeek(code: string, key: string, s: SeekSettings): PlaybackAction | null {
  if (code === 'Digit1' || key === '!') return { kind: 'seek', seconds: -s.key1Seconds };
  if (code === 'Digit3' || key === '#') return { kind: 'seek', seconds: s.key3Seconds };
  if (code === 'Digit4' || key === '$') return { kind: 'seek', seconds: -s.key4Seconds };
  if (code === 'Digit6' || key === '^') return { kind: 'seek', seconds: s.key6Seconds };
  if (code === 'Digit7' || key === '&') return { kind: 'seek', seconds: -s.key7Seconds };
  if (code === 'Digit9' || key === '(') return { kind: 'seek', seconds: s.key9Seconds };
  return null;
}

// Plain top-row digit (only reached after the typing-target guard):
//   1/4/7 back, 3/6/9 fwd, 5 play/pause, 8 jump to near end.
export function plainDigitPlaybackAction(key: string, s: SeekSettings): PlaybackAction | null {
  switch (key) {
    case '1': return { kind: 'seek', seconds: -s.key1Seconds };
    case '3': return { kind: 'seek', seconds: s.key3Seconds };
    case '4': return { kind: 'seek', seconds: -s.key4Seconds };
    case '5': return { kind: 'playPause' };
    case '6': return { kind: 'seek', seconds: s.key6Seconds };
    case '7': return { kind: 'seek', seconds: -s.key7Seconds };
    case '8': return { kind: 'seekToNearEnd' };
    case '9': return { kind: 'seek', seconds: s.key9Seconds };
    default: return null;
  }
}

// Alt+1..9 toggles the Nth checkbox-group tag. Returns the 1-based digit for a
// top-row OR numpad digit key, else null. The caller gates on Alt-only.
export function altFlagDigit(code: string): number | null {
  let prefixLen: number;
  if (code.startsWith('Digit')) prefixLen = 'Digit'.length;
  else if (code.startsWith('Numpad')) prefixLen = 'Numpad'.length;
  else return null;
  const d = parseInt(code.slice(prefixLen), 10);
  return d >= 1 && d <= 9 ? d : null;
}

// True when the event target is a text-entry surface, so single-letter and
// plain-digit shortcuts shouldn't hijack the keystroke.
export function isTypingTarget(t: EventTarget | null): boolean {
  if (!(t instanceof HTMLElement)) return false;
  const tag = t.tagName;
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || t.isContentEditable;
}

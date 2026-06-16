// Shared "drive a <video> with the app's playback shortcuts" helper.
//
// The main player (VideoPlayer.svelte) has a big window-level keydown handler,
// but the same numpad / Shift+digit / plain-digit / space seek scheme should
// also work where there's no VideoPlayer: the clip-preview confirmation modal,
// the Export Clips page, and the Remove Blocked page (the user reviews clips/
// videos there). This module resolves a key event to a PlaybackAction (reusing
// the pure resolvers from playerKeyboard) and applies it to a given video
// element — so those surfaces get the same navigation without duplicating the
// 3k-line component. Pure except applyPlaybackAction, which touches the element.

import {
  numpadPlaybackAction,
  shiftDigitSeek,
  plainDigitPlaybackAction,
  isTypingTarget,
  type PlaybackAction,
  type SeekSettings
} from './playerKeyboard';

// Map a key event to a playback action, honouring the same precedence the main
// player uses: numpad fires even over text inputs (matched on code); Shift+digit
// next; then — only when NOT typing and with no Ctrl/Meta/Alt — plain digits and
// Space. Returns null when the key isn't a playback shortcut.
export function resolvePlaybackKey(
  e: Pick<KeyboardEvent, 'code' | 'key' | 'shiftKey' | 'ctrlKey' | 'metaKey' | 'altKey' | 'target'>,
  settings: SeekSettings
): PlaybackAction | null {
  const np = numpadPlaybackAction(e.code, settings);
  if (np) return np;

  if (e.shiftKey && !e.ctrlKey && !e.metaKey && !e.altKey) {
    const sd = shiftDigitSeek(e.code, e.key, settings);
    if (sd) return sd;
  }

  if (isTypingTarget(e.target)) return null;

  if (!e.ctrlKey && !e.metaKey && !e.altKey) {
    const pd = plainDigitPlaybackAction(e.key, settings);
    if (pd) return pd;
    if (e.key === ' ' || e.key === 'Spacebar') return { kind: 'playPause' };
  }
  return null;
}

// Apply a resolved action to a concrete video element (clamped to its range).
export function applyPlaybackAction(el: HTMLVideoElement, a: PlaybackAction): void {
  const dur = Number.isFinite(el.duration) ? el.duration : Number.MAX_SAFE_INTEGER;
  switch (a.kind) {
    case 'seek':
      el.currentTime = Math.max(0, Math.min(dur, el.currentTime + a.seconds));
      break;
    case 'playPause':
      if (el.paused) el.play().catch(() => {});
      else el.pause();
      break;
    case 'seekToStart':
      el.currentTime = 0;
      break;
    case 'seekToNearEnd':
      if (dur > 10 && dur !== Number.MAX_SAFE_INTEGER) el.currentTime = dur - 10;
      break;
  }
}

// Resolve + apply against a video element. Returns true (and preventDefaults)
// when the key was a playback shortcut and an element was available.
export function handleVideoKey(
  e: KeyboardEvent,
  el: HTMLVideoElement | null | undefined,
  settings: SeekSettings
): boolean {
  if (!el) return false;
  const action = resolvePlaybackKey(e, settings);
  if (!action) return false;
  e.preventDefault();
  applyPlaybackAction(el, action);
  return true;
}

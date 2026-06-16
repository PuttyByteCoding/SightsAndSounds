import { describe, expect, test } from 'vitest';
import { resolvePlaybackKey } from './videoKeyboard';
import type { SeekSettings } from './playerKeyboard';

const S: SeekSettings = {
  key1Seconds: 1,
  key3Seconds: 3,
  key4Seconds: 4,
  key6Seconds: 6,
  key7Seconds: 7,
  key9Seconds: 9,
};

// Minimal stand-in for a KeyboardEvent — resolvePlaybackKey only reads these.
function ev(
  partial: Partial<{ code: string; key: string; shiftKey: boolean; ctrlKey: boolean; metaKey: boolean; altKey: boolean; target: EventTarget | null }>
) {
  return {
    code: '',
    key: '',
    shiftKey: false,
    ctrlKey: false,
    metaKey: false,
    altKey: false,
    target: null,
    ...partial,
  };
}

describe('resolvePlaybackKey', () => {
  test('numpad seeks fire even over a text input (matched on code)', () => {
    const input = document.createElement('input');
    expect(resolvePlaybackKey(ev({ code: 'Numpad4', target: input }), S)).toEqual({ kind: 'seek', seconds: -4 });
    expect(resolvePlaybackKey(ev({ code: 'Numpad5', target: input }), S)).toEqual({ kind: 'playPause' });
  });

  test('Shift+digit seeks only with shift and no other modifier', () => {
    expect(resolvePlaybackKey(ev({ code: 'Digit6', key: '^', shiftKey: true }), S)).toEqual({ kind: 'seek', seconds: 6 });
    expect(resolvePlaybackKey(ev({ code: 'Digit6', key: '6', shiftKey: false }), S)).toEqual({ kind: 'seek', seconds: 6 }); // plain digit path
    expect(resolvePlaybackKey(ev({ code: 'Digit6', key: '^', shiftKey: true, ctrlKey: true }), S)).toBeNull();
  });

  test('plain digits and space work when not typing', () => {
    expect(resolvePlaybackKey(ev({ key: '7' }), S)).toEqual({ kind: 'seek', seconds: -7 });
    expect(resolvePlaybackKey(ev({ key: '5' }), S)).toEqual({ kind: 'playPause' });
    expect(resolvePlaybackKey(ev({ key: ' ' }), S)).toEqual({ kind: 'playPause' });
    expect(resolvePlaybackKey(ev({ key: '8' }), S)).toEqual({ kind: 'seekToNearEnd' });
  });

  test('plain digits and space are suppressed while typing in an input', () => {
    const input = document.createElement('input');
    expect(resolvePlaybackKey(ev({ key: '7', target: input }), S)).toBeNull();
    expect(resolvePlaybackKey(ev({ key: ' ', target: input }), S)).toBeNull();
  });

  test('non-shortcut keys resolve to null', () => {
    expect(resolvePlaybackKey(ev({ key: 'a' }), S)).toBeNull();
    expect(resolvePlaybackKey(ev({ key: 'Enter' }), S)).toBeNull();
  });
});

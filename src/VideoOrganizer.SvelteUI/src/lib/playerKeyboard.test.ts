import { describe, expect, test } from 'vitest';
import {
  numpadPlaybackAction,
  shiftDigitSeek,
  plainDigitPlaybackAction,
  altFlagDigit,
  type SeekSettings,
} from './playerKeyboard';

// Distinct per-key values so a swapped mapping is caught.
const S: SeekSettings = {
  key1Seconds: 1,
  key3Seconds: 3,
  key4Seconds: 4,
  key6Seconds: 6,
  key7Seconds: 7,
  key9Seconds: 9,
};

describe('numpadPlaybackAction', () => {
  test('control keys', () => {
    expect(numpadPlaybackAction('Numpad5', S)).toEqual({ kind: 'playPause' });
    expect(numpadPlaybackAction('Numpad0', S)).toEqual({ kind: 'seekToStart' });
    expect(numpadPlaybackAction('NumpadSubtract', S)).toEqual({ kind: 'seekToNearEnd' });
  });
  test('seek keys carry the right signed seconds (1/4/7 back, 3/6/9 fwd)', () => {
    expect(numpadPlaybackAction('Numpad1', S)).toEqual({ kind: 'seek', seconds: -1 });
    expect(numpadPlaybackAction('Numpad4', S)).toEqual({ kind: 'seek', seconds: -4 });
    expect(numpadPlaybackAction('Numpad7', S)).toEqual({ kind: 'seek', seconds: -7 });
    expect(numpadPlaybackAction('Numpad3', S)).toEqual({ kind: 'seek', seconds: 3 });
    expect(numpadPlaybackAction('Numpad6', S)).toEqual({ kind: 'seek', seconds: 6 });
    expect(numpadPlaybackAction('Numpad9', S)).toEqual({ kind: 'seek', seconds: 9 });
  });
  test('unrelated keys return null', () => {
    expect(numpadPlaybackAction('Numpad2', S)).toBeNull();
    expect(numpadPlaybackAction('KeyW', S)).toBeNull();
  });
});

describe('shiftDigitSeek', () => {
  test('matches on code', () => {
    expect(shiftDigitSeek('Digit1', '!', S)).toEqual({ kind: 'seek', seconds: -1 });
    expect(shiftDigitSeek('Digit9', '(', S)).toEqual({ kind: 'seek', seconds: 9 });
  });
  test('matches on shifted glyph when code differs (alt layouts)', () => {
    expect(shiftDigitSeek('', '#', S)).toEqual({ kind: 'seek', seconds: 3 });
    expect(shiftDigitSeek('', '^', S)).toEqual({ kind: 'seek', seconds: 6 });
  });
  test('non-seek digits return null', () => {
    expect(shiftDigitSeek('Digit2', '@', S)).toBeNull();
    expect(shiftDigitSeek('Digit5', '%', S)).toBeNull();
  });
});

describe('plainDigitPlaybackAction', () => {
  test('digit seeks + play/pause + near-end', () => {
    expect(plainDigitPlaybackAction('1', S)).toEqual({ kind: 'seek', seconds: -1 });
    expect(plainDigitPlaybackAction('9', S)).toEqual({ kind: 'seek', seconds: 9 });
    expect(plainDigitPlaybackAction('5', S)).toEqual({ kind: 'playPause' });
    expect(plainDigitPlaybackAction('8', S)).toEqual({ kind: 'seekToNearEnd' });
  });
  test('2 and non-digits return null', () => {
    expect(plainDigitPlaybackAction('2', S)).toBeNull();
    expect(plainDigitPlaybackAction('w', S)).toBeNull();
  });
});

describe('altFlagDigit', () => {
  test('top-row and numpad digits 1-9', () => {
    expect(altFlagDigit('Digit1')).toBe(1);
    expect(altFlagDigit('Numpad9')).toBe(9);
  });
  test('0 and out-of-range / non-digit return null', () => {
    expect(altFlagDigit('Digit0')).toBeNull();
    expect(altFlagDigit('KeyA')).toBeNull();
  });
});

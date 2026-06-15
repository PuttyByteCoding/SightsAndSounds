import { describe, expect, test } from 'vitest';
import { parseScrubberFrames } from './scrubber';

describe('parseScrubberFrames', () => {
  test('parses cues in document order and computes the bounding sprite size', () => {
    const vtt = `WEBVTT

00:00:00.000 --> 00:00:02.000
/api/videos/abc/sprite.jpg#xywh=0,0,160,90

00:00:02.000 --> 00:00:04.000
/api/videos/abc/sprite.jpg#xywh=160,0,160,90

00:00:04.000 --> 00:00:06.000
/api/videos/abc/sprite.jpg#xywh=0,90,160,90
`;
    const { frames, spriteSize } = parseScrubberFrames(vtt);
    expect(frames).toEqual([
      { x: 0, y: 0, w: 160, h: 90 },
      { x: 160, y: 0, w: 160, h: 90 },
      { x: 0, y: 90, w: 160, h: 90 },
    ]);
    // max(x+w) = 160+160 = 320 ; max(y+h) = 90+90 = 180
    expect(spriteSize).toEqual({ w: 320, h: 180 });
  });

  test('single frame', () => {
    const { frames, spriteSize } = parseScrubberFrames(
      '/x/sprite.jpg#xywh=10,20,160,90'
    );
    expect(frames).toEqual([{ x: 10, y: 20, w: 160, h: 90 }]);
    expect(spriteSize).toEqual({ w: 170, h: 110 });
  });

  test('empty input yields no frames and a zero sprite size', () => {
    expect(parseScrubberFrames('')).toEqual({
      frames: [],
      spriteSize: { w: 0, h: 0 },
    });
  });

  test('VTT with no sprite fragments yields no frames', () => {
    const vtt = `WEBVTT

00:00:00.000 --> 00:00:02.000
just some caption text
`;
    expect(parseScrubberFrames(vtt).frames).toEqual([]);
  });

  test('ignores malformed fragment lines but keeps the valid ones', () => {
    const vtt = [
      '/x/sprite.jpg#xywh=0,0,160,90',
      '/x/sprite.jpg#xywh=bad,values,here,nope', // not digits → ignored
      '/x/sprite.jpg#xywh=160,0,160,90',
      '/x/other.png#xywh=999,999,1,1', // wrong image name → ignored
    ].join('\n');
    const { frames, spriteSize } = parseScrubberFrames(vtt);
    expect(frames).toEqual([
      { x: 0, y: 0, w: 160, h: 90 },
      { x: 160, y: 0, w: 160, h: 90 },
    ]);
    expect(spriteSize).toEqual({ w: 320, h: 90 });
  });

  test('is stateless across calls (module-scoped /g regex is reset)', () => {
    const vtt = '/x/sprite.jpg#xywh=0,0,160,90';
    const first = parseScrubberFrames(vtt);
    const second = parseScrubberFrames(vtt);
    expect(second).toEqual(first);
    expect(second.frames).toHaveLength(1);
  });
});

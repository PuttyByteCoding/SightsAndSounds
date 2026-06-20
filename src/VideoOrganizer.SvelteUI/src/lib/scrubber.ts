// Pure parsing for the scrub-preview filmstrip, extracted from VideoPlayer.svelte
// (#126) so it can be unit-tested without mounting the 3.2k-line component.
//
// The thumbnails VTT served by the API points every cue at a region of one
// sprite sheet via the WebVTT media-fragment syntax:
//
//   00:00:00.000 --> 00:00:02.000
//   /api/.../sprite.jpg#xywh=0,0,160,90
//
// We only need the rectangles (the cue timings are implied by frame order and
// the known interval), plus the overall sprite dimensions so the component can
// size the background image. This mirrors the original inline logic exactly.

export interface ScrubFrame {
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface ScrubberFrames {
  frames: ScrubFrame[];
  /** Bounding size of the sprite sheet: max(x+w) by max(y+h) across all frames. */
  spriteSize: { w: number; h: number };
}

const XYWH = /sprite\.jpg#xywh=(\d+),(\d+),(\d+),(\d+)/g;

/**
 * Parse the `#xywh=` rectangles out of a thumbnails VTT document. Returns the
 * frames in document order and the bounding sprite size. Lines that don't match
 * the sprite-fragment shape are ignored; empty / non-matching input yields an
 * empty result with a zero sprite size.
 */
export function parseScrubberFrames(vtt: string): ScrubberFrames {
  const frames: ScrubFrame[] = [];
  let maxRight = 0;
  let maxBottom = 0;

  // Fresh lastIndex per call — the regex is module-scoped with /g.
  XYWH.lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = XYWH.exec(vtt)) !== null) {
    const x = +m[1];
    const y = +m[2];
    const w = +m[3];
    const h = +m[4];
    frames.push({ x, y, w, h });
    if (x + w > maxRight) maxRight = x + w;
    if (y + h > maxBottom) maxBottom = y + h;
  }

  return { frames, spriteSize: { w: maxRight, h: maxBottom } };
}

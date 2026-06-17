import { describe, expect, test } from 'vitest';
import { playerBudget, videoHeightCap } from './playerSizing';

describe('playerBudget', () => {
  test('reserves header + strip + gaps below the card top', () => {
    // 1000 viewport, card starts at 100, header 40, strip 180, gaps 28.
    expect(playerBudget(1000, 100, 40, 180, 28, 220)).toBe(652);
  });

  test('floors at the minimum so the card never collapses', () => {
    // Tall strip + tiny viewport would go negative without the floor.
    expect(playerBudget(400, 100, 40, 300, 28, 220)).toBe(220);
  });

  test('rounds the raw value', () => {
    expect(playerBudget(1000.4, 100.2, 40, 180, 28, 220)).toBe(652);
  });
});

describe('videoHeightCap', () => {
  test('subtracts the measured chrome so video + chrome == budget', () => {
    // Card naturally 700 tall, video box currently 500 → chrome 200.
    // Budget 600 → video should cap at 400 (400 + 200 == 600).
    expect(videoHeightCap(600, 700, 500, 120)).toBe(400);
  });

  test('chrome is independent of the current video height', () => {
    // Same chrome (200) regardless of whether the video is currently
    // 500 (scroll 700) or 300 (scroll 500) tall — cap is the same.
    expect(videoHeightCap(600, 700, 500, 120)).toBe(400);
    expect(videoHeightCap(600, 500, 300, 120)).toBe(400);
  });

  test('floors at the minimum video height when chrome eats the budget', () => {
    // Chrome 500 with a 400 budget would give -100; clamp to the floor.
    expect(videoHeightCap(400, 900, 400, 120)).toBe(120);
  });

  test('never treats negative (rounding) chrome as a bonus', () => {
    // videoBox momentarily larger than scrollHeight → chrome floored at 0.
    expect(videoHeightCap(600, 480, 500, 120)).toBe(600);
  });
});

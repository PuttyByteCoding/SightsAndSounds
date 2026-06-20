import { afterEach, beforeEach, describe, expect, test, vi } from 'vitest';
import { errorBanner, httpErrorMessage } from './errorBanner.svelte';

// errorBanner is a $state singleton, so it lives in the components project
// (the unit project has no Svelte plugin to compile runes).

function drain() {
  for (const e of [...errorBanner.entries]) errorBanner.dismiss(e.id);
}

describe('httpErrorMessage', () => {
  test('prefers a JSON { error } message', () => {
    expect(httpErrorMessage(404, 'GET', '/api/videos/x', '{"error":"Not found"}'))
      .toBe('Error 404: Not found');
  });

  test('falls back to plain-text body', () => {
    expect(httpErrorMessage(500, 'POST', '/api/x', 'boom'))
      .toBe('Error 500: boom');
  });

  test('uses method + path when there is no body', () => {
    expect(httpErrorMessage(403, 'DELETE', 'http://host/api/tags/1', ''))
      .toBe('Error 403 on DELETE /api/tags/1');
  });

  test('collapses whitespace and truncates long bodies', () => {
    const msg = httpErrorMessage(500, 'GET', '/api/x', 'a\n  b ' + 'x'.repeat(400));
    expect(msg.startsWith('Error 500: a b ')).toBe(true);
    expect(msg.length).toBeLessThanOrEqual('Error 500: '.length + 160);
  });
});

describe('errorBanner store', () => {
  beforeEach(() => { vi.useFakeTimers(); drain(); });
  afterEach(() => { drain(); vi.useRealTimers(); });

  test('push adds an entry; dismiss removes it', () => {
    errorBanner.push('boom');
    expect(errorBanner.entries.map(e => e.message)).toContain('boom');
    errorBanner.dismiss(errorBanner.entries[0].id);
    expect(errorBanner.entries.length).toBe(0);
  });

  test('an entry auto-expires after 3 seconds', () => {
    errorBanner.push('temporary');
    expect(errorBanner.entries.length).toBe(1);
    vi.advanceTimersByTime(2999);
    expect(errorBanner.entries.length).toBe(1);
    vi.advanceTimersByTime(1);
    expect(errorBanner.entries.length).toBe(0);
  });

  test('multiple errors stack', () => {
    errorBanner.push('one');
    errorBanner.push('two');
    expect(errorBanner.entries.map(e => e.message)).toEqual(['one', 'two']);
  });
});

import { describe, it, expect } from 'vitest';
import { adminOnlyPaths, isAdminPath } from './navAccess';

describe('isAdminPath', () => {
  it('flags every admin-only route (exact match)', () => {
    for (const p of adminOnlyPaths) {
      expect(isAdminPath(p)).toBe(true);
    }
  });

  it('flags sub-routes of an admin-only route', () => {
    expect(isAdminPath('/tags/edit')).toBe(true);
    expect(isAdminPath('/import/job/123')).toBe(true);
  });

  it('does NOT flag the read surfaces kept for read-only users', () => {
    for (const p of ['/browse', '/history', '/logs', '/keyboard-shortcuts',
                     '/help', '/api-docs', '/style-guide', '/']) {
      expect(isAdminPath(p)).toBe(false);
    }
  });

  it('does not flag a path that merely starts with an admin name', () => {
    // '/importer' is not '/import' nor '/import/...'
    expect(isAdminPath('/importer')).toBe(false);
    expect(isAdminPath('/sources-list')).toBe(false);
  });
});

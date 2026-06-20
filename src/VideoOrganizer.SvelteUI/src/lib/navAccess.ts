// Read-only sweep (#124). The set of routes that exist to perform write
// operations (tag/source management, imports, library-shaping tools,
// background-task controls, and review queues with mutating actions). For a
// read-only 'viewer' these are hidden from the nav and route-guarded — the API
// 403s their writes anyway, so this is UX + defense-in-depth, not the security
// boundary. Kept routes (browse/play, history, logs, reference) are read
// surfaces; their in-page write controls are gated individually.
export const adminOnlyPaths = [
  '/tags', '/hidden-tags', '/import', '/sources', '/backups',
  '/background-tasks', '/playback-issues', '/duplicates', '/purge',
  '/clips-export', '/remove-blocked', '/join', '/encode', '/optimize',
  '/moves', '/data-validation'
];

// True when `path` is one of the admin-only routes (exact match or a sub-route).
export function isAdminPath(path: string): boolean {
  return adminOnlyPaths.some((p) => path === p || path.startsWith(`${p}/`));
}

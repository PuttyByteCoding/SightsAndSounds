import type { VideoSet } from './types';

// Returns the VideoSet whose Path is a prefix of the given path, or null.
function findSetForPath(path: string, sets: VideoSet[]): VideoSet | null {
  if (!path) return null;
  const needle = path.toLowerCase();
  let best: VideoSet | null = null;
  for (const s of sets) {
    const root = s.path.replace(/[/\\]+$/, '').toLowerCase();
    if (needle === root || needle.startsWith(root + '/') || needle.startsWith(root + '\\')) {
      if (!best || s.path.length > best.path.length) best = s;
    }
  }
  return best;
}

export interface Breadcrumb {
  name: string;
  // null means "navigate back to the sets root" (empty-path browse response).
  path: string | null;
}

export function breadcrumbs(currentPath: string, sets: VideoSet[]): Breadcrumb[] {
  const crumbs: Breadcrumb[] = [{ name: 'Sets', path: null }];
  if (!currentPath) return crumbs;

  const current = currentPath.replace(/[/\\]+$/, '');
  const set = findSetForPath(current, sets);
  if (!set) return crumbs;

  const setRoot = set.path.replace(/[/\\]+$/, '');
  crumbs.push({ name: set.name, path: setRoot });

  if (current.toLowerCase() === setRoot.toLowerCase()) return crumbs;

  const rel = current.slice(setRoot.length).replace(/^[/\\]+/, '');
  let pathSoFar = setRoot;
  for (const part of rel.split(/[/\\]+/).filter(Boolean)) {
    // Preserve whatever separator style the set root uses.
    const sep = setRoot.includes('\\') ? '\\' : '/';
    pathSoFar = pathSoFar.replace(/[/\\]+$/, '') + sep + part;
    crumbs.push({ name: part, path: pathSoFar });
  }

  return crumbs;
}

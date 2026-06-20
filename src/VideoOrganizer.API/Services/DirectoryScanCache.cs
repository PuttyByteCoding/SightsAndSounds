using System.Collections.Concurrent;

namespace VideoOrganizer.API.Services;

// Caches the recursive on-disk video-file count per directory (issue #4).
//
// /import/browse annotates every folder row with CountVideoFilesRecursive —
// a full EnumerateFiles(AllDirectories) walk run once PER folder on every
// call, and the dominant cost of loading the Sources tree. The filesystem
// count only changes when video files physically move/appear/disappear on
// disk (the file-move feature), NOT on import — which just records existing
// files in the DB. So the count is safe to memoize: cheap hits on repeat
// browses, and invalidation only where the app moves files.
//
// Invalidation:
//   · Remove(dir) for just the folders whose count actually changed on a
//     move/undo (MoveVideo, RevertFileMove) — NOT a blanket Clear(), which
//     would empty the cache exactly when the user moves a file and then
//     browses, forcing a full re-walk (issue #4: "move is still too slow to
//     browse"). A move only changes the counts of directories that gained or
//     lost the file; common ancestors (e.g. the source root for a within-
//     source move) are unchanged and stay cached.
//   · The Sources refresh button hits /import/browse?refresh=true, which
//     Clear()s before re-scanning — the deliberate full refresh that catches
//     anything changed outside the app.
//
// ImportedCount is NOT cached here — it's a fast DB count recomputed each
// call, so it always reflects the latest imports. In-memory only; a restart
// starts cold. Keys are PathNormalizer-normalized absolute paths; the
// dictionary is case-insensitive because Windows paths are.
public sealed class DirectoryScanCache
{
    private readonly ConcurrentDictionary<string, int> _counts =
        new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string normalizedPath, out int count) =>
        _counts.TryGetValue(normalizedPath, out count);

    public void Set(string normalizedPath, int count) =>
        _counts[normalizedPath] = count;

    // Evict a single directory's cached count. No-op if it was never cached.
    public void Remove(string normalizedPath) =>
        _counts.TryRemove(normalizedPath, out _);

    // Drop everything — used only by the explicit ?refresh=true full refresh.
    public void Clear() => _counts.Clear();

    // Exposed for diagnostics.
    public int Count => _counts.Count;
}

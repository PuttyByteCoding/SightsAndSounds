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
//   · Clear() after a file move/undo (MoveVideo, RevertFileMove).
//   · The Sources refresh button hits /import/browse?refresh=true, which
//     Clear()s before re-scanning — catching anything changed outside the
//     app (a manual mark-for-deletion move, files dropped in by the user).
//
// ImportedCount is NOT cached here — it's a fast DB count recomputed each
// call, so it always reflects the latest imports. In-memory only; a restart
// starts cold. Keys are PathNormalizer-normalized absolute paths.
public sealed class DirectoryScanCache
{
    private readonly ConcurrentDictionary<string, int> _counts = new();

    public bool TryGet(string normalizedPath, out int count) =>
        _counts.TryGetValue(normalizedPath, out count);

    public void Set(string normalizedPath, int count) =>
        _counts[normalizedPath] = count;

    // Drop everything. Recursive counts roll up through ancestors, so one
    // moved file can change many cached entries — clearing all is the
    // simplest correct response to any filesystem mutation.
    public void Clear() => _counts.Clear();

    // Exposed for tests / diagnostics.
    public int Count => _counts.Count;
}

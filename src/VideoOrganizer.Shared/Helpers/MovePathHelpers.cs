using System;
using System.IO;

namespace VideoOrganizer.Shared.Helpers;

// Pure path helpers for the file-move feature (issue #4). Filesystem
// effects (existence checks) are injected so these stay unit-testable.
public static class MovePathHelpers
{
    // True when two paths live on the same volume/root — so File.Move is an
    // instant rename rather than a copy+delete. Case-insensitive root
    // compare with trailing separators normalized.
    public static bool IsSameVolume(string a, string b)
    {
        var ra = Path.GetPathRoot(Path.GetFullPath(a)) ?? string.Empty;
        var rb = Path.GetPathRoot(Path.GetFullPath(b)) ?? string.Empty;
        return string.Equals(
            ra.TrimEnd('/', '\\'),
            rb.TrimEnd('/', '\\'),
            StringComparison.OrdinalIgnoreCase);
    }

    // Given a desired destination path and a predicate that reports whether
    // a candidate already exists, return a non-colliding path by appending
    // " (2)", " (3)", … before the extension. Existence is injected so this
    // is testable without touching the disk.
    public static string UniqueDestination(string desiredPath, Func<string, bool> exists)
    {
        if (!exists(desiredPath)) return desiredPath;
        var dir = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(desiredPath);
        var ext = Path.GetExtension(desiredPath);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!exists(candidate)) return candidate;
        }
    }
}

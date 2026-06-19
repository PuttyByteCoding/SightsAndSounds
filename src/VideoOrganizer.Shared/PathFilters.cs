namespace VideoOrganizer.Shared;

/// <summary>
/// Folders the app manages internally and doesn't want to appear in browse
/// trees or import scans. The app creates these as children of VideoSet roots:
///   _Thumbnails       — scrub-sprite cache (if the user points the ThumbnailsDirectory under a set root)
///   _ToDelete         — videos the user has flagged for deletion (files moved here, never auto-deleted)
///   _PlaybackIssue    — videos the user has flagged as not playing cleanly
///   _WontPlay         — legacy name for _PlaybackIssue (kept so leftover files
///                       from before the rename stay hidden from browse / import)
/// </summary>
public static class PathFilters
{
    public static readonly HashSet<string> ExcludedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "_Thumbnails",
        "_ToDelete",
        "_PlaybackIssue",
        "_WontPlay"
    };

    private static readonly char[] PathSeparators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

    /// <summary>True if the given folder name is one of the app-managed excluded names.</summary>
    public static bool IsExcludedFolderName(string folderName)
        => ExcludedFolderNames.Contains(folderName);

    /// <summary>
    /// True if the file is hidden — its name starts with a dot (".DS_Store",
    /// "._clip.mp4"). The import tool ignores these in its counts and scan and
    /// surfaces them in a separate "Hidden files" list instead. (issue #62)
    /// </summary>
    public static bool IsHiddenFile(string path)
        => Path.GetFileName(path).StartsWith('.');

    /// <summary>
    /// True if <paramref name="fullPath"/> passes through any excluded folder between
    /// <paramref name="baseDir"/> and the item itself. Paths that ARE excluded folders
    /// (or are outside baseDir) don't match — the check only hides descendants.
    /// </summary>
    public static bool IsInExcludedFolder(string fullPath, string baseDir)
    {
        string rel;
        try { rel = Path.GetRelativePath(baseDir, fullPath); }
        catch { return false; }
        if (rel.StartsWith("..", StringComparison.Ordinal)) return false;
        foreach (var segment in rel.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (ExcludedFolderNames.Contains(segment)) return true;
        }
        return false;
    }

    /// <summary>
    /// True if <paramref name="fullPath"/> lives inside a hidden (dot-prefixed)
    /// directory somewhere between <paramref name="baseDir"/> and the item — e.g.
    /// ".git/config" or ".cache/x.mp4". Only ANCESTOR directory segments are
    /// checked, NOT the leaf: a top-level dotfile is left to <see cref="IsHiddenFile"/>
    /// so it can still be surfaced on the "Hidden files" tab, while the contents
    /// of hidden directories (often huge, e.g. ".git") are dropped entirely from
    /// browse trees and import scans (issue #62).
    /// </summary>
    public static bool IsInHiddenFolder(string fullPath, string baseDir)
    {
        string rel;
        try { rel = Path.GetRelativePath(baseDir, fullPath); }
        catch { return false; }
        if (rel.StartsWith("..", StringComparison.Ordinal)) return false;
        var segments = rel.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        // Exclude the final segment (the file/dir itself); check its ancestors.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].StartsWith('.')) return true;
        }
        return false;
    }
}

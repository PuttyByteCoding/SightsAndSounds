namespace VideoOrganizer.Shared;

/// <summary>
/// Folders the app manages internally and doesn't want to appear in browse
/// trees or import scans. The app creates these as children of VideoSet roots:
///   _Thumbnails — scrub-sprite cache (if the user points the ThumbnailsDirectory under a set root)
///   _ToDelete   — videos the user has flagged for deletion (files moved here, never auto-deleted)
///   _WontPlay   — videos the user has flagged as unplayable
/// </summary>
public static class PathFilters
{
    public static readonly HashSet<string> ExcludedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "_Thumbnails",
        "_ToDelete",
        "_WontPlay"
    };

    private static readonly char[] PathSeparators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

    /// <summary>True if the given folder name is one of the app-managed excluded names.</summary>
    public static bool IsExcludedFolderName(string folderName)
        => ExcludedFolderNames.Contains(folderName);

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
}

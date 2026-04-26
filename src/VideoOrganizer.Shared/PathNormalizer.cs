namespace VideoOrganizer.Shared;

/// <summary>
/// Canonicalizes stored paths to forward-slash separators. Windows .NET Path
/// APIs accept forward slashes fine, so storing canonically sidesteps the
/// backslash-vs-forward-slash StartsWith comparison bug that would otherwise
/// make every video look "outside" its VideoSet.
/// </summary>
public static class PathNormalizer
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        return path.Replace('\\', '/');
    }
}

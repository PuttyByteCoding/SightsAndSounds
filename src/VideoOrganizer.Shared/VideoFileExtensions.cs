namespace VideoOrganizer.Shared;

/// <summary>
/// Single source of truth for "which file extensions are treated as video
/// files." Used by the API's browse/files/thumbnail endpoints and by the
/// import service so preview counts match actual import counts.
/// </summary>
public static class VideoFileExtensions
{
    // Intentionally narrow — user only imports .mp4 and .m4v. Everything else
    // goes into the non-importable bucket. Expand this set if/when we add
    // transcoding for other formats.
    private static readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v"
    };

    public static IReadOnlyCollection<string> All => _extensions;

    public static bool IsVideo(string filePath)
        => _extensions.Contains(Path.GetExtension(filePath));
}

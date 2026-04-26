namespace VideoOrganizer.API.Services;

/// <summary>
/// Service for generating video thumbnail sprites and VTT files for video scrubbing preview.
/// </summary>
public interface IThumbnailGenerator
{
    /// <summary>
    /// Generates thumbnails for a video and creates a sprite image with VTT metadata.
    /// </summary>
    /// <param name="videoPath">Path to the video file</param>
    /// <param name="videoId">Unique identifier for the video</param>
    /// <param name="intervalSeconds">Time interval between thumbnails in seconds. Pass 0 for adaptive (default).</param>
    /// <param name="thumbnailWidth">Width of each thumbnail in pixels</param>
    /// <param name="thumbnailHeight">Height of each thumbnail in pixels</param>
    /// <param name="columns">Number of columns in the sprite sheet</param>
    /// <returns>A tuple containing the sprite image path and VTT content</returns>
    Task<(string spriteImagePath, string vttContent)> GenerateThumbnailsAsync(
        string videoPath,
        Guid videoId,
        int intervalSeconds = 0,
        int thumbnailWidth = 320,
        int thumbnailHeight = 180,
        int columns = 5,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the path to the cached sprite image for a video if it exists.
    /// </summary>
    /// <param name="videoId">Unique identifier for the video</param>
    /// <returns>The sprite image path if it exists, otherwise an empty string</returns>
    string GetSpriteImagePath(Guid videoId);
}
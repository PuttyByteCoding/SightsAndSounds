namespace VideoOrganizer.Shared.Configuration;

/// <summary>
/// Bootstrap config for video storage. Only used on first startup to seed a
/// "Default" VideoSet when the database is empty — runtime-managed VideoSets
/// in the DB are the source of truth thereafter.
/// </summary>
public class VideoStorageOptions
{
    /// <summary>
    /// Initial root the API reads video files from. On Docker this is usually
    /// a container path (e.g. "/videos") mapped by docker-compose; on native
    /// Windows it's just a Windows path (e.g. "C:/VideoOrganizerData/Videos").
    /// Maps to appsettings.json: VideoStorage:Root
    /// </summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>
    /// Directory where generated scrub-sprite thumbnails are cached. If empty,
    /// the API falls back to a "video-thumbnails" subdirectory of the system
    /// temp path (%TEMP% on Windows, /tmp on Linux).
    /// Maps to appsettings.json: VideoStorage:ThumbnailsDirectory
    /// </summary>
    public string ThumbnailsDirectory { get; set; } = string.Empty;
}

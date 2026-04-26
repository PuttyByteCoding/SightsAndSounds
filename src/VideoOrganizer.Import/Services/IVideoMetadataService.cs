namespace VideoOrganizer.Import.Services;

public interface IVideoMetadataService
{
    Task<VideoMetadata?> GetMetadataAsync(string filePath, CancellationToken cancellationToken = default);
}

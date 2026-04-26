namespace VideoOrganizer.API.Services;

public interface IDirectoryImportService
{
    Task ImportFromDirectoryAsync(
        string directoryPath,
        IReadOnlyList<Guid>? initialTagIds = null,
        string? notes = null,
        bool includeSubdirectories = true,
        Guid? jobId = null,
        CancellationToken ct = default);
}

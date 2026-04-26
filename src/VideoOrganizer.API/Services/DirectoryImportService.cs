using VideoOrganizer.Import.Services;
using VideoOrganizer.Shared.Dto;

namespace VideoOrganizer.API.Services;

public class DirectoryImportService : IDirectoryImportService
{
    private readonly Import.Services.DirectoryImportService _importService;
    private readonly ImportProgressTracker _progressTracker;

    public DirectoryImportService(Import.Services.DirectoryImportService importService, ImportProgressTracker progressTracker)
    {
        _importService = importService;
        _progressTracker = progressTracker;
    }

    public async Task ImportFromDirectoryAsync(
        string directoryPath,
        IReadOnlyList<Guid>? initialTagIds = null,
        string? notes = null,
        bool includeSubdirectories = true,
        Guid? jobId = null,
        CancellationToken ct = default)
    {
        Action<string, long, ImportFileStatus, long, long, string?>? reporter = null;
        if (jobId.HasValue)
        {
            reporter = (filePath, fileSize, status, md5Processed, md5Total, error) =>
                _progressTracker.UpdateFileProgress(jobId.Value, filePath, fileSize, status, md5Processed, md5Total, error);
        }

        await _importService.ImportFromDirectoryAsync(
            directoryPath,
            initialTagIds: initialTagIds,
            notes: notes,
            includeSubdirectories: includeSubdirectories,
            fileStatusReporter: reporter,
            importJobId: jobId,
            ct: ct);

        if (jobId.HasValue)
            _progressTracker.AddMessage(jobId.Value, "Import complete.");
    }
}

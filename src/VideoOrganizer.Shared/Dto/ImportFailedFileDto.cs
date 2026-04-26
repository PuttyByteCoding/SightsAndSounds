namespace VideoOrganizer.Shared.Dto;

// One row in the "Show Failed" modal for the Imports section. Aggregated
// across every in-memory import job; lost on API restart (same as the rest
// of the import progress data).
public record ImportFailedFileDto(
    Guid JobId,
    string JobDirectoryPath,
    string FilePath,
    string FileName,
    long FileSizeBytes,
    string? Error);

// One row in the "Show Queue" modal for the Imports section. Pending or
// currently-importing files across active jobs.
public record ImportQueueFileDto(
    Guid JobId,
    string JobDirectoryPath,
    string FilePath,
    string FileName,
    long FileSizeBytes,
    ImportFileStatus Status);

namespace VideoOrganizer.Shared.Dto;

// Lightweight snapshot of a single import job. Drives the "Imports" card on
// the Background Tasks page so the user can see progress after navigating
// away from the import page itself. Does not include the full file list —
// the existing /api/import/progress/{jobId} endpoint serves that.
public record ImportJobSummaryDto(
    Guid JobId,
    string Name,
    string DirectoryPath,
    // EnqueuedAt: when POST /api/import/directory was accepted.
    // StartedAt: null while the job is queued behind other imports;
    //   set when the ImportQueueService pulls it and Add-to-database begins.
    DateTime EnqueuedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    bool IsCompleted,
    string? Error,
    int TotalFiles,
    int CompletedCount,
    int FailedCount,
    int SkippedCount,
    int ImportingCount,
    string? CurrentFilePath,
    // Per-import progress for the downstream workers. Computed by joining
    // Videos.ImportJobId == JobId at endpoint time. The fields are 0 when
    // no Videos carry this JobId yet (e.g. the import is still creating
    // rows or the job pre-dates the ImportJobId column).
    ImportTaskProgressDto Thumbnails,
    ImportTaskProgressDto Md5);

public record ImportTaskProgressDto(
    int Total,
    int Done,
    int Pending,
    int Failed);

namespace VideoOrganizer.Shared.Dto;

// One row in the "Show Failed" modal for the Thumbnail or Md5 worker.
// Backed by Video rows where the corresponding *Failed flag is true.
public record WorkerFailedRowDto(
    Guid VideoId,
    string FileName,
    string FilePath,
    long FileSizeBytes,
    string? Error);

// One row in the "Show Queue" modal for the Thumbnail or Md5 worker.
// Built by joining the in-memory queue path list with Video rows so the
// table can show file size alongside the path.
public record WorkerQueueRowDto(
    Guid? VideoId,
    string FileName,
    string FilePath,
    long FileSizeBytes);

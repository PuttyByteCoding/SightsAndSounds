namespace VideoOrganizer.Shared.Dto;

public record ImportFileProgressDto(
    string FilePath,
    long FileSizeBytes,
    long Md5BytesProcessed,
    long Md5TotalBytes,
    ImportFileStatus Status,
    // Populated when Status is Failed; null otherwise. Surfaced in the
    // "Show Failed" table on the Background Tasks page so the user has
    // immediate context without digging through logs.
    string? Error = null
);

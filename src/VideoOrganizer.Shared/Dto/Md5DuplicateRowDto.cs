namespace VideoOrganizer.Shared.Dto;

// One row per video that shares its Md5 with at least one other video.
// Returned by /api/md5-backfill/duplicates and rendered in the
// "Check for Md5 dups" modal — sorted so videos sharing an Md5 cluster
// together. The Md5 column is included so the user can see which
// videos belong to the same group.
public record Md5DuplicateRowDto(
    Guid VideoId,
    string FileName,
    string FilePath,
    long FileSizeBytes,
    string Md5,
    int GroupSize);

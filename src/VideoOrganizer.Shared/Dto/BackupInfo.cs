namespace VideoOrganizer.Shared.Dto;

// Metadata for one backup file in the backups folder (issue #32).
// Type is the lower-cased extension: "json" (quick snapshot) or "sqlite"
// (full backup).
public record BackupInfo(
    string FileName,
    string Type,
    long SizeBytes,
    DateTime CreatedUtc
);

namespace VideoOrganizer.Shared.Dto;

// Metadata for one backup file in the backups folder (issue #32).
// Type is the lower-cased extension: "json" (the quick snapshot).
public record BackupInfo(
    string FileName,
    string Type,
    long SizeBytes,
    DateTime CreatedUtc
);

// Where backups are stored + whether that directory is writable (issue #32).
public record BackupSettings(
    string Directory,
    bool Writable,
    string? Error
);

public record SetBackupDirectoryRequest(string Directory);

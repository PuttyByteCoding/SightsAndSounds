namespace VideoOrganizer.Shared.Dto;

// Import a directory of video files. InitialTagIds are applied to every
// newly-created Video so the user can pre-stage tags at import time.
// Notes is appended verbatim to Video.Notes.
public record DirectoryImportRequest(
    string DirectoryPath,
    bool IncludeSubdirectories,
    // Display label for the job. Falls back to the directory leaf name if
    // the client doesn't supply one.
    string? Name = null,
    string? Notes = null,
    List<Guid>? InitialTagIds = null
);

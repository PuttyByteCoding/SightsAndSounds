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
    List<Guid>? InitialTagIds = null,
    // Flags to set on every imported video (#168). Recognized keys: "favorite",
    // "clip". Structural flags (exported/edited/embedded) are system-set, and
    // playbackIssue/markedForDeletion are excluded (they move files on disk).
    List<string>? InitialFlags = null
);

namespace VideoOrganizer.Shared.Dto;

// A video marked for deletion that still has embedded (non-exported) clips
// defined inside it (#174). Purging it deletes the file, losing those clip
// ranges — so the Purge page warns and offers to export them first.
public record PurgeClipWarningDto(
    System.Guid VideoId,
    string FileName,
    int EmbeddedClipCount);

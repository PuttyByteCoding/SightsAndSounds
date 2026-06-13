namespace VideoOrganizer.Shared.Dto;

// POST /api/videos/{id}/move — move the video's file into TargetDirectory
// (an absolute folder under some enabled source, within or across
// sources). (issue #4)
public record MoveVideoRequest(string TargetDirectory);

// A logged file move, surfaced on the Moves list with an Undo action.
// RevertedAt is null while the move can still be undone.
public record FileMoveLogDto(
    System.Guid Id,
    System.Guid VideoId,
    string FileName,
    string FromPath,
    string ToPath,
    string MovedAt,
    string? RevertedAt);

// Live progress of an in-flight move/undo, polled by the move dialog. A
// same-volume move is an instant rename (jumps straight to done); a
// cross-volume move streams the copy and reports real bytes. Phase is one
// of "idle" | "copying" | "finalizing" | "done". (issue #4)
public record MoveProgressDto(
    bool Active,
    long BytesCopied,
    long TotalBytes,
    string Phase);

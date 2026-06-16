namespace VideoOrganizer.Shared.Dto;

// One video that has "Hide" blocks which can be removed into a new file
// (issue #70), with those blocks. Drives the remove-blocked page's queue.
public record BlockRemovalQueueItemDto(
    System.Guid VideoId,
    string FileName,
    double DurationSeconds,
    System.Collections.Generic.IReadOnlyList<VideoBlockDto> HideBlocks);

// Request to trim the Hide sections out of a set of videos (issue #70).
public record RemoveBlocksRequest(
    System.Collections.Generic.IReadOnlyList<System.Guid> VideoIds);

// Live state of the block-removal run, polled by the page.
// Phase: "idle" | "trimming" | "stopping" | "done" | "error".
public record BlockRemovalProgressDto(
    bool Active,
    int Total,
    int Done,
    string Current,
    string Phase,
    System.Collections.Generic.IReadOnlyList<string> Errors);

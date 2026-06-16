namespace VideoOrganizer.Shared.Dto;

// Request to repair (re-encode) a set of videos (issue #165).
public record RepairRequest(
    System.Collections.Generic.IReadOnlyList<System.Guid> VideoIds);

// Live state of the repair run, polled by the Playback Issues page.
// Phase: "idle" | "repairing" | "stopping" | "done" | "error".
public record RepairProgressDto(
    bool Active,
    int Total,
    int Done,
    string Current,
    string Phase,
    System.Collections.Generic.IReadOnlyList<string> Errors);

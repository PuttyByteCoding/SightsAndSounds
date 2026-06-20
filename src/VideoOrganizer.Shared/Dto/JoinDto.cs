namespace VideoOrganizer.Shared.Dto;

// Request to join (concatenate) videos in order into one new file (issue #163).
// VideoIds is the join order. Reencode normalizes mismatched inputs (slower,
// lossy) instead of the default lossless stream-copy. Name is the output base
// name (blank → "<first>_joined").
public record JoinRequest(
    System.Collections.Generic.IReadOnlyList<System.Guid> VideoIds,
    bool Reencode = false,
    string? Name = null);

// Live state of the join run, polled by the Join page.
// Phase: "idle" | "joining" | "stopping" | "done" | "error".
public record JoinProgressDto(
    bool Active,
    int Total,
    int Done,
    string Current,
    string Phase,
    System.Collections.Generic.IReadOnlyList<string> Errors);

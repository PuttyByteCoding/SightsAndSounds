namespace VideoOrganizer.Shared.Dto;

// Request to optimize videos for streaming (faststart remux) (issue #166).
public record OptimizeStreamingRequest(
    System.Collections.Generic.IReadOnlyList<System.Guid> VideoIds);

// Live state of the optimize run, polled by the Optimize page. Optimized =
// files that were remuxed; Skipped = already faststart or not MP4.
// Phase: "idle" | "optimizing" | "stopping" | "done" | "error".
public record StreamingOptimizeProgressDto(
    bool Active,
    int Total,
    int Done,
    int Optimized,
    int Skipped,
    string Current,
    string Phase,
    System.Collections.Generic.IReadOnlyList<string> Errors);

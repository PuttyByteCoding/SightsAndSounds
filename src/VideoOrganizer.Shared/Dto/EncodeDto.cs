namespace VideoOrganizer.Shared.Dto;

// Request to encode/convert videos to the configured profile (issue #164).
public record EncodeRequest(
    System.Collections.Generic.IReadOnlyList<System.Guid> VideoIds);

// Live state of the encode run, polled by the Encode page.
// Phase: "idle" | "encoding" | "stopping" | "done" | "error".
public record EncodeProgressDto(
    bool Active,
    int Total,
    int Done,
    string Current,
    string Phase,
    System.Collections.Generic.IReadOnlyList<string> Errors);

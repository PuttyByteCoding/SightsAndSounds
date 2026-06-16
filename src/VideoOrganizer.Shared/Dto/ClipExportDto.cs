namespace VideoOrganizer.Shared.Dto;

// One parent file that has clips waiting to be exported (issue #69), with its
// not-yet-exported clips. Drives the clips-export page's queue.
public record ClipExportQueueItemDto(
    System.Guid ParentId,
    string ParentFileName,
    double ParentDurationSeconds,
    System.Collections.Generic.IReadOnlyList<ClipSummaryDto> Clips);

// Request to export a set of clips to their own files (issue #69).
public record ExportClipsRequest(
    System.Collections.Generic.IReadOnlyList<System.Guid> ClipIds);

// Live state of the clip-export run, polled by the page.
// Phase: "idle" | "exporting" | "stopping" | "done" | "error".
public record ClipExportProgressDto(
    bool Active,
    int Total,
    int Done,
    string Current,
    string Phase,
    System.Collections.Generic.IReadOnlyList<string> Errors);

// Keyframe-snapped cut points for a clip (issue #69). With stream-copy export
// the output really starts at SnappedStartSeconds (the keyframe at/before the
// requested in-point), so the page can preview the actual lead-in.
public record KeyframeCutDto(
    double RequestedStartSeconds,
    double SnappedStartSeconds,
    double EndSeconds);

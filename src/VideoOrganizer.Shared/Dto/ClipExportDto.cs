namespace VideoOrganizer.Shared.Dto;

// One parent file that has clips waiting to be exported (issue #69), with its
// not-yet-exported clips. Drives the clips-export page's queue.
public record ClipExportQueueItemDto(
    System.Guid ParentId,
    string ParentFileName,
    double ParentDurationSeconds,
    System.Collections.Generic.IReadOnlyList<ClipSummaryDto> Clips);

// One clip selected for export, with an optional output name (#173). Name is
// the base file name without extension; blank falls back to "<parent>_clip".
public record ExportClipItem(
    System.Guid ClipId,
    string? Name);

// Request to export a set of clips to their own files (issue #69 / #173).
public record ExportClipsRequest(
    System.Collections.Generic.IReadOnlyList<ExportClipItem> Clips);

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

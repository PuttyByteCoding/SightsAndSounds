namespace VideoOrganizer.Shared.Dto;

// Minimal projection of a clip for the parent video's scrubber
// overlays — enough to draw a green tinted band per clip without
// pulling the full VideoDto graph (tags, properties, etc.). Returned
// by GET /api/videos/{parentId}/clips, ordered by ClipStartSeconds.
public record ClipSummaryDto(
    System.Guid Id,
    string FileName,
    double ClipStartSeconds,
    double ClipEndSeconds,
    // True once this clip has been exported to its own standalone file (#69):
    // the scrubber still draws a band here, but styled as an "exported"
    // breadcrumb rather than a normal clip.
    bool Exported);

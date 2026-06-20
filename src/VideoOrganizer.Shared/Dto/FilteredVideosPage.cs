namespace VideoOrganizer.Shared.Dto;

// One keyset-paginated page of filtered videos (#127). NextCursor is an opaque
// token to pass back for the following page; null means this is the last page.
// TotalCount is the full number of (visible) matches across the whole filter —
// what the browse "video N of M" badge needs, since the client only holds the
// pages it has scrolled to. HiddenCount is the matches suppressed by a
// hidden-by-default tag (also a whole-filter total), driving the "N hidden"
// status.
public record FilteredVideosPage(
    IReadOnlyList<VideoDto> Videos,
    string? NextCursor,
    int TotalCount,
    int HiddenCount);

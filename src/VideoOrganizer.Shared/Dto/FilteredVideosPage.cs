namespace VideoOrganizer.Shared.Dto;

// One keyset-paginated page of filtered videos (#127). NextCursor is an opaque
// token to pass back for the following page; null means this is the last page.
// HiddenCount is the total number of matches suppressed by a hidden-by-default
// tag across the whole filter (not just this page), so the browse bar's
// "N hidden" status stays accurate.
public record FilteredVideosPage(
    IReadOnlyList<VideoDto> Videos,
    string? NextCursor,
    int HiddenCount);

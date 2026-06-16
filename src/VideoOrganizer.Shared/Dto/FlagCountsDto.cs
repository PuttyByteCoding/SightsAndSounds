namespace VideoOrganizer.Shared.Dto;

// Aggregate counts of videos with each boolean flag set, served by
// GET /api/videos/flag-counts. Drives the per-flag count badges on
// the Flags section of the browse-page filter sidebar so the user
// can see how many rows would match before applying the filter.
//
// Clip is the umbrella (embedded OR user-marked OR exported); Embedded is
// the structural child-clip subset (ParentVideoId.HasValue); Exported and
// Edited are the produced-file flags (#69/#70/#167).
public record FlagCountsDto(
    int Favorite,
    int NeedsReview,
    int PlaybackIssue,
    int MarkedForDeletion,
    int Clip,
    int Embedded,
    int Exported,
    int Edited);

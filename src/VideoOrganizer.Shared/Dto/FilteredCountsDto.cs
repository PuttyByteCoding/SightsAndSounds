namespace VideoOrganizer.Shared.Dto;

// Per-tag and per-flag counts over the CURRENTLY-FILTERED ("shown") video set,
// served by POST /api/videos/filtered-counts (#208). The browse sidebar pairs
// these with the unfiltered totals to show "shown/total" (e.g. 16/142) on each
// tag and flag while a filter is active.
//
// TagCounts is keyed by tag id; a tag absent from the map has zero matches in
// the current filter. Flags mirrors GET /api/videos/flag-counts but scoped to
// the filtered set.
public record FilteredCountsDto(
    Dictionary<Guid, int> TagCounts,
    FlagCountsDto Flags);

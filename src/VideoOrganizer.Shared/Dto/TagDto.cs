namespace VideoOrganizer.Shared.Dto;

// Wire shape for a Tag. VideoCount populated when listing for the Tag
// Management page (/tags?withCounts=true), 0 elsewhere.
public record TagDto(
    Guid Id,
    Guid TagGroupId,
    string TagGroupName,
    string Name,
    IReadOnlyList<string> Aliases,
    bool IsFavorite,
    int SortOrder,
    string Notes,
    int VideoCount = 0);

public record CreateTagRequest(
    Guid TagGroupId,
    string Name,
    IReadOnlyList<string>? Aliases = null,
    bool IsFavorite = false,
    int SortOrder = 0,
    string Notes = "");

public record UpdateTagRequest(
    string Name,
    IReadOnlyList<string> Aliases,
    bool IsFavorite,
    int SortOrder,
    string Notes);

// Merge sources into target. All Videos referencing any source tag are
// re-pointed at target; sources are then deleted. Target must not appear
// in sources, and all tags must belong to the same TagGroup.
public record MergeTagsRequest(Guid[] SourceIds, Guid TargetId);

// One row in /tags/search results.
public record TagSearchHit(
    Guid TagId,
    Guid TagGroupId,
    string TagGroupName,
    string Name,
    IReadOnlyList<string> Aliases);
